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

    public static string Serialize(AgentRoutingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteStartArray("capacities");
            foreach (var (kind, capacity) in settings.Capacities)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", kind.Value);
                writer.WriteNumber("max", capacity);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("skills");
            foreach (var skill in settings.Skills)
            {
                writer.WriteStartObject();
                writer.WriteString("name", skill.Name);
                writer.WriteNumber("level", skill.Level);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static AgentRoutingSettings Deserialize(UserId agent, int defaultCapacity, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = Properties(document.RootElement, "v", "capacities", "skills");
            if (root["v"].GetInt32() != Version)
                throw new FormatException("Unsupported agent settings version.");
            var capacities = root["capacities"].EnumerateArray().Select(item =>
            {
                var entry = Properties(item, "kind", "max");
                return new KeyValuePair<ChannelKind, int>(new ChannelKind(entry["kind"].GetString()!), entry["max"].GetInt32());
            }).ToList();
            var skills = root["skills"].EnumerateArray().Select(item =>
            {
                var entry = Properties(item, "name", "level");
                return new AgentSkill(entry["name"].GetString()!, entry["level"].GetInt32());
            }).ToList();
            return new AgentRoutingSettings(agent, defaultCapacity, capacities, skills);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The agent settings document is invalid.", error);
        }
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An object is required.");
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name) || !properties.TryAdd(property.Name, property.Value))
                throw new FormatException("Unknown or duplicate property.");
        if (properties.Count != names.Length)
            throw new FormatException("A required property is missing.");
        return properties;
    }
}
