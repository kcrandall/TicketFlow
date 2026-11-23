using System.Buffers;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>
/// Persisted JSON for the capacity and skill parts of <see cref="AgentRoutingSettings"/> (the agent identifier and default capacity are
/// stored separately): <c>{"v":1,"capacities":[{"kind":"email","max":3}],"skills":[{"name":"billing","level":4}]}</c>. Decoding re-validates
/// every value and rejects unknown versions and properties.
/// </summary>
public static class AgentSettingsCodec
{
    public const int Version = 1;
    private const string EmptySettingsJson = """{"v":1,"capacities":[],"skills":[]}""";

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    public static string Serialize(AgentRoutingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Capacities.Count == 0 && settings.Skills.Count == 0)
            return EmptySettingsJson;
        var buffer = sharedBuffer;
        var writer = sharedWriter;
        sharedBuffer = null;
        sharedWriter = null;
        if (buffer is null)
            buffer = new ArrayBufferWriter<byte>(256);
        else
            buffer.ResetWrittenCount();
        if (writer is null)
            writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);
        try
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, Version);
            writer.WriteStartArray("capacities"u8);
            foreach (var (kind, capacity) in settings.Capacities)
            {
                writer.WriteStartObject();
                writer.WriteString("kind"u8, kind.Value);
                writer.WriteNumber("max"u8, capacity);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("skills"u8);
            for (var i = 0; i < settings.Skills.Count; i++)
            {
                var skill = settings.Skills[i];
                writer.WriteStartObject();
                writer.WriteString("name"u8, skill.Name);
                writer.WriteNumber("level"u8, skill.Level);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            if (buffer.Capacity <= 65536)
            {
                buffer.ResetWrittenCount();
                sharedBuffer = buffer;
                sharedWriter = writer;
            }
            else
                writer.Dispose();
        }
    }

    public static AgentRoutingSettings Deserialize(UserId agent, int defaultCapacity, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (string.Equals(json, EmptySettingsJson, StringComparison.Ordinal))
            return new AgentRoutingSettings(agent, defaultCapacity);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("An object is required.");
            JsonElement vEl = default, capEl = default, skillsEl = default;
            var mask = 0;
            foreach (var prop in root.EnumerateObject())
            {
                int bit;
                if (prop.NameEquals("v"u8))
                {
                    bit = 1;
                    vEl = prop.Value;
                }
                else if (prop.NameEquals("capacities"u8))
                {
                    bit = 2;
                    capEl = prop.Value;
                }
                else if (prop.NameEquals("skills"u8))
                {
                    bit = 4;
                    skillsEl = prop.Value;
                }
                else
                    throw new FormatException("Unknown property.");
                if ((mask & bit) != 0)
                    throw new FormatException("Duplicate property.");
                mask |= bit;
            }
            if (mask != 7)
                throw new FormatException("A required property is missing.");
            if (vEl.GetInt32() != Version)
                throw new FormatException("Unsupported agent settings version.");
            if (capEl.ValueKind != JsonValueKind.Array || skillsEl.ValueKind != JsonValueKind.Array)
                throw new FormatException("Expected arrays.");

            List<KeyValuePair<ChannelKind, int>>? capacities = null;
            var capLen = capEl.GetArrayLength();
            if (capLen > 0)
            {
                capacities = new List<KeyValuePair<ChannelKind, int>>(capLen);
                foreach (var item in capEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new FormatException("An object is required.");
                    JsonElement kindEl = default, maxEl = default;
                    var itemMask = 0;
                    foreach (var prop in item.EnumerateObject())
                    {
                        int bit;
                        if (prop.NameEquals("kind"u8))
                        {
                            bit = 1;
                            kindEl = prop.Value;
                        }
                        else if (prop.NameEquals("max"u8))
                        {
                            bit = 2;
                            maxEl = prop.Value;
                        }
                        else
                            throw new FormatException("Unknown property.");
                        if ((itemMask & bit) != 0)
                            throw new FormatException("Duplicate property.");
                        itemMask |= bit;
                    }
                    if (itemMask != 3)
                        throw new FormatException("A required property is missing.");
                    capacities.Add(new KeyValuePair<ChannelKind, int>(new ChannelKind(kindEl.GetString()!), maxEl.GetInt32()));
                }
            }

            List<AgentSkill>? skills = null;
            var skillLen = skillsEl.GetArrayLength();
            if (skillLen > 0)
            {
                skills = new List<AgentSkill>(skillLen);
                foreach (var item in skillsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        throw new FormatException("An object is required.");
                    JsonElement nameEl = default, levelEl = default;
                    var itemMask = 0;
                    foreach (var prop in item.EnumerateObject())
                    {
                        int bit;
                        if (prop.NameEquals("name"u8))
                        {
                            bit = 1;
                            nameEl = prop.Value;
                        }
                        else if (prop.NameEquals("level"u8))
                        {
                            bit = 2;
                            levelEl = prop.Value;
                        }
                        else
                            throw new FormatException("Unknown property.");
                        if ((itemMask & bit) != 0)
                            throw new FormatException("Duplicate property.");
                        itemMask |= bit;
                    }
                    if (itemMask != 3)
                        throw new FormatException("A required property is missing.");
                    skills.Add(new AgentSkill(nameEl.GetString()!, levelEl.GetInt32()));
                }
            }
            return new AgentRoutingSettings(agent, defaultCapacity, capacities, skills);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The agent settings document is invalid.", error);
        }
    }
}
