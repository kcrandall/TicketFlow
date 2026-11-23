using System.Buffers;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>
/// Persisted JSON for <see cref="RoutingPolicy"/> (the identifier is stored separately). Envelope <c>{"v":1,...}</c>; strategies, skill
/// matches and overflow targets carry an explicit <c>"k"</c> discriminator (<c>round-robin</c>, <c>least-busy</c>, <c>skills</c>,
/// <c>sticky</c>, <c>manual</c>; <c>label</c>, <c>attribute</c>; <c>team</c>, <c>inbox</c>). Durations are whole seconds. Decoding
/// re-validates every value and rejects unknown versions, discriminators and properties. Unions are never serialized directly.
/// </summary>
public static class RoutingPolicyCodec
{
    public const int Version = 1;
    private const int MaximumLength = 64 * 1024;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    public static string Serialize(RoutingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var buffer = sharedBuffer;
        var writer = sharedWriter;
        sharedBuffer = null;
        sharedWriter = null;
        if (buffer is null)
            buffer = new ArrayBufferWriter<byte>(512);
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
            writer.WriteString("name"u8, policy.Name);
            writer.WritePropertyName("strategy"u8);
            WriteStrategy(writer, policy.Strategy);
            if (policy.Team is { } team)
                writer.WriteString("team"u8, team.Value);
            writer.WriteStartArray("overflow"u8);
            for (var i = 0; i < policy.Overflow.Count; i++)
            {
                var rule = policy.Overflow[i];
                writer.WriteStartObject();
                writer.WriteNumber("after"u8, (long)rule.After.TotalSeconds);
                writer.WritePropertyName("target"u8);
                writer.WriteStartObject();
                switch (rule.Target)
                {
                    case OverflowToTeam toTeam:
                        writer.WriteString("k"u8, "team"u8);
                        writer.WriteString("team"u8, toTeam.Team.Value);
                        break;
                    case OverflowToInbox toInbox:
                        writer.WriteString("k"u8, "inbox"u8);
                        writer.WriteString("inbox"u8, toInbox.Inbox.Value);
                        break;
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (policy.MaximumWait is { } wait)
                writer.WriteNumber("maxWait"u8, (long)wait.TotalSeconds);
            writer.WriteNumber("offline"u8, (int)policy.OnAgentOffline);
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

    public static RoutingPolicy Deserialize(RoutingPolicyId id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumLength)
            throw new FormatException("The routing policy document is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("An object is required.");
            JsonElement vEl = default, nameEl = default, strategyEl = default, overflowEl = default, offlineEl = default, teamEl = default, maxWaitEl = default;
            var mask = 0;
            foreach (var prop in root.EnumerateObject())
            {
                int bit;
                if (prop.NameEquals("v"u8)) { bit = 1; vEl = prop.Value; }
                else if (prop.NameEquals("name"u8)) { bit = 2; nameEl = prop.Value; }
                else if (prop.NameEquals("strategy"u8)) { bit = 4; strategyEl = prop.Value; }
                else if (prop.NameEquals("overflow"u8)) { bit = 8; overflowEl = prop.Value; }
                else if (prop.NameEquals("offline"u8)) { bit = 16; offlineEl = prop.Value; }
                else if (prop.NameEquals("team"u8)) { bit = 32; teamEl = prop.Value; }
                else if (prop.NameEquals("maxWait"u8)) { bit = 64; maxWaitEl = prop.Value; }
                else throw new FormatException("Unknown property.");
                if ((mask & bit) != 0)
                    throw new FormatException("Duplicate property.");
                mask |= bit;
            }
            if ((mask & 31) != 31)
                throw new FormatException("A required property is missing.");
            if (vEl.GetInt32() != Version)
                throw new FormatException("Unsupported routing policy version.");
            var offline = (OfflineReassignment)offlineEl.GetInt32();
            if (!Enum.IsDefined(offline))
                throw new FormatException("Unknown offline reassignment.");
            if (overflowEl.ValueKind != JsonValueKind.Array)
                throw new FormatException("An array is required.");
            var overflow = new List<OverflowRule>(overflowEl.GetArrayLength());
            foreach (var element in overflowEl.EnumerateArray())
                overflow.Add(ReadOverflowRule(element));
            return new RoutingPolicy(id, String(nameEl), ReadStrategy(strategyEl, 0),
                (mask & 32) != 0 ? new TeamId(String(teamEl)) : null, overflow,
                (mask & 64) != 0 ? Seconds(maxWaitEl) : null, offline);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The routing policy document is invalid.", error);
        }
    }

    private static OverflowRule ReadOverflowRule(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An object is required.");
        JsonElement afterEl = default, targetEl = default;
        var mask = 0;
        foreach (var prop in element.EnumerateObject())
        {
            int bit;
            if (prop.NameEquals("after"u8)) { bit = 1; afterEl = prop.Value; }
            else if (prop.NameEquals("target"u8)) { bit = 2; targetEl = prop.Value; }
            else throw new FormatException("Unknown property.");
            if ((mask & bit) != 0)
                throw new FormatException("Duplicate property.");
            mask |= bit;
        }
        if (mask != 3 || targetEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("A required property is missing.");
        JsonElement kEl = default, teamEl = default, inboxEl = default;
        var tMask = 0;
        foreach (var prop in targetEl.EnumerateObject())
        {
            int bit;
            if (prop.NameEquals("k"u8)) { bit = 1; kEl = prop.Value; }
            else if (prop.NameEquals("team"u8)) { bit = 2; teamEl = prop.Value; }
            else if (prop.NameEquals("inbox"u8)) { bit = 4; inboxEl = prop.Value; }
            else throw new FormatException("Unknown property.");
            if ((tMask & bit) != 0)
                throw new FormatException("Duplicate property.");
            tMask |= bit;
        }
        if ((tMask & 1) == 0)
            throw new FormatException("A required property is missing.");
        OverflowTarget decoded = String(kEl) switch
        {
            "team" when tMask == 3 => new OverflowToTeam(new TeamId(String(teamEl))),
            "inbox" when tMask == 5 => new OverflowToInbox(new InboxId(String(inboxEl))),
            _ => throw new FormatException("Unknown overflow target."),
        };
        return new OverflowRule(Seconds(afterEl), decoded);
    }

    private static void WriteStrategy(Utf8JsonWriter writer, RoutingStrategy strategy)
    {
        writer.WriteStartObject();
        switch (strategy)
        {
            case RoundRobinStrategy:
                writer.WriteString("k"u8, "round-robin"u8);
                break;
            case LeastBusyStrategy:
                writer.WriteString("k"u8, "least-busy"u8);
                break;
            case SkillsBasedStrategy skills:
                writer.WriteString("k"u8, "skills"u8);
                writer.WriteStartArray("rules"u8);
                for (var i = 0; i < skills.Rules.Count; i++)
                {
                    var rule = skills.Rules[i];
                    writer.WriteStartObject();
                    writer.WritePropertyName("match"u8);
                    writer.WriteStartObject();
                    switch (rule.Match)
                    {
                        case LabelSkillMatch label:
                            writer.WriteString("k"u8, "label"u8);
                            writer.WriteString("label"u8, label.Label.Value);
                            break;
                        case AttributeSkillMatch attribute:
                            writer.WriteString("k"u8, "attribute"u8);
                            writer.WriteString("key"u8, attribute.Key);
                            writer.WriteString("value"u8, attribute.Value);
                            break;
                    }
                    writer.WriteEndObject();
                    writer.WriteString("skill"u8, rule.Skill);
                    writer.WriteNumber("level"u8, rule.MinimumLevel);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            case StickyOwnerStrategy sticky:
                writer.WriteString("k"u8, "sticky"u8);
                writer.WriteNumber("window"u8, (long)sticky.Window.TotalSeconds);
                writer.WritePropertyName("fallback"u8);
                WriteStrategy(writer, sticky.Fallback);
                break;
            case ManualStrategy:
                writer.WriteString("k"u8, "manual"u8);
                break;
        }
        writer.WriteEndObject();
    }

    private static RoutingStrategy ReadStrategy(JsonElement element, int depth)
    {
        if (depth > 1)
            throw new FormatException("Strategies nest at most once.");
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An object is required.");
        JsonElement kEl = default, rulesEl = default, windowEl = default, fallbackEl = default;
        var mask = 0;
        foreach (var prop in element.EnumerateObject())
        {
            int bit;
            if (prop.NameEquals("k"u8)) { bit = 1; kEl = prop.Value; }
            else if (prop.NameEquals("rules"u8)) { bit = 2; rulesEl = prop.Value; }
            else if (prop.NameEquals("window"u8)) { bit = 4; windowEl = prop.Value; }
            else if (prop.NameEquals("fallback"u8)) { bit = 8; fallbackEl = prop.Value; }
            else throw new FormatException("Unknown property.");
            if ((mask & bit) != 0)
                throw new FormatException("Duplicate property.");
            mask |= bit;
        }
        if ((mask & 1) == 0)
            throw new FormatException("A required property is missing.");
        return String(kEl) switch
        {
            "round-robin" when mask == 1 => RoutingStrategy.RoundRobin,
            "least-busy" when mask == 1 => RoutingStrategy.LeastBusy,
            "manual" when mask == 1 => RoutingStrategy.Manual,
            "skills" when mask == 3 => ReadSkillsStrategy(rulesEl),
            "sticky" when mask == 13 => new StickyOwnerStrategy(Seconds(windowEl), ReadStrategy(fallbackEl, depth + 1)),
            _ => throw new FormatException("Unknown routing strategy."),
        };
    }

    private static SkillsBasedStrategy ReadSkillsStrategy(JsonElement rulesEl)
    {
        if (rulesEl.ValueKind != JsonValueKind.Array)
            throw new FormatException("An array is required.");
        var rules = new List<SkillRule>(rulesEl.GetArrayLength());
        foreach (var item in rulesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new FormatException("An object is required.");
            JsonElement matchEl = default, skillEl = default, levelEl = default;
            var rMask = 0;
            foreach (var prop in item.EnumerateObject())
            {
                int bit;
                if (prop.NameEquals("match"u8)) { bit = 1; matchEl = prop.Value; }
                else if (prop.NameEquals("skill"u8)) { bit = 2; skillEl = prop.Value; }
                else if (prop.NameEquals("level"u8)) { bit = 4; levelEl = prop.Value; }
                else throw new FormatException("Unknown property.");
                if ((rMask & bit) != 0)
                    throw new FormatException("Duplicate property.");
                rMask |= bit;
            }
            if (rMask != 7 || matchEl.ValueKind != JsonValueKind.Object)
                throw new FormatException("A required property is missing.");
            JsonElement mkEl = default, labelEl = default, keyEl = default, valEl = default;
            var mMask = 0;
            foreach (var prop in matchEl.EnumerateObject())
            {
                int bit;
                if (prop.NameEquals("k"u8)) { bit = 1; mkEl = prop.Value; }
                else if (prop.NameEquals("label"u8)) { bit = 2; labelEl = prop.Value; }
                else if (prop.NameEquals("key"u8)) { bit = 4; keyEl = prop.Value; }
                else if (prop.NameEquals("value"u8)) { bit = 8; valEl = prop.Value; }
                else throw new FormatException("Unknown property.");
                if ((mMask & bit) != 0)
                    throw new FormatException("Duplicate property.");
                mMask |= bit;
            }
            if ((mMask & 1) == 0)
                throw new FormatException("A required property is missing.");
            SkillMatch decoded = String(mkEl) switch
            {
                "label" when mMask == 3 => new LabelSkillMatch(new LabelId(String(labelEl))),
                "attribute" when mMask == 13 => new AttributeSkillMatch(String(keyEl), String(valEl)),
                _ => throw new FormatException("Unknown skill match."),
            };
            rules.Add(new SkillRule(decoded, String(skillEl), levelEl.GetInt32()));
        }
        return new SkillsBasedStrategy(rules);
    }

    private static string String(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException("A string is required.");

    private static TimeSpan Seconds(JsonElement element)
    {
        var seconds = element.GetInt64();
        if (seconds is < 1 or > 400L * 24 * 3600)
            throw new FormatException("Durations are positive seconds.");
        return TimeSpan.FromSeconds(seconds);
    }
}
