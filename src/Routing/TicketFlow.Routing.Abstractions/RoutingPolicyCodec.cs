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

    public static string Serialize(RoutingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("name", policy.Name);
            writer.WritePropertyName("strategy");
            WriteStrategy(writer, policy.Strategy);
            if (policy.Team is { } team)
                writer.WriteString("team", team.Value);
            writer.WriteStartArray("overflow");
            foreach (var rule in policy.Overflow)
            {
                writer.WriteStartObject();
                writer.WriteNumber("after", (long)rule.After.TotalSeconds);
                writer.WritePropertyName("target");
                writer.WriteStartObject();
                switch (rule.Target)
                {
                    case OverflowToTeam toTeam:
                        writer.WriteString("k", "team");
                        writer.WriteString("team", toTeam.Team.Value);
                        break;
                    case OverflowToInbox toInbox:
                        writer.WriteString("k", "inbox");
                        writer.WriteString("inbox", toInbox.Inbox.Value);
                        break;
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (policy.MaximumWait is { } wait)
                writer.WriteNumber("maxWait", (long)wait.TotalSeconds);
            writer.WriteNumber("offline", (int)policy.OnAgentOffline);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static RoutingPolicy Deserialize(RoutingPolicyId id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumLength)
            throw new FormatException("The routing policy document is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = Object(document.RootElement, ["v", "name", "strategy", "overflow", "offline"], ["team", "maxWait"]);
            if (root["v"].GetInt32() != Version)
                throw new FormatException("Unsupported routing policy version.");
            var offline = (OfflineReassignment)root["offline"].GetInt32();
            if (!Enum.IsDefined(offline))
                throw new FormatException("Unknown offline reassignment.");
            var overflow = root["overflow"].EnumerateArray().Select(element =>
            {
                var rule = Object(element, ["after", "target"], []);
                var target = Object(rule["target"], ["k"], ["team", "inbox"]);
                OverflowTarget decoded = String(target["k"]) switch
                {
                    "team" when Only(target, "team") => new OverflowToTeam(new TeamId(String(target["team"]))),
                    "inbox" when Only(target, "inbox") => new OverflowToInbox(new InboxId(String(target["inbox"]))),
                    _ => throw new FormatException("Unknown overflow target."),
                };
                return new OverflowRule(Seconds(rule["after"]), decoded);
            }).ToList();
            return new RoutingPolicy(id, String(root["name"]), ReadStrategy(root["strategy"], 0),
                root.TryGetValue("team", out var team) ? new TeamId(String(team)) : null, overflow,
                root.TryGetValue("maxWait", out var wait) ? Seconds(wait) : null, offline);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The routing policy document is invalid.", error);
        }
    }

    private static void WriteStrategy(Utf8JsonWriter writer, RoutingStrategy strategy)
    {
        writer.WriteStartObject();
        switch (strategy)
        {
            case RoundRobinStrategy:
                writer.WriteString("k", "round-robin");
                break;
            case LeastBusyStrategy:
                writer.WriteString("k", "least-busy");
                break;
            case SkillsBasedStrategy skills:
                writer.WriteString("k", "skills");
                writer.WriteStartArray("rules");
                foreach (var rule in skills.Rules)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("match");
                    writer.WriteStartObject();
                    switch (rule.Match)
                    {
                        case LabelSkillMatch label:
                            writer.WriteString("k", "label");
                            writer.WriteString("label", label.Label.Value);
                            break;
                        case AttributeSkillMatch attribute:
                            writer.WriteString("k", "attribute");
                            writer.WriteString("key", attribute.Key);
                            writer.WriteString("value", attribute.Value);
                            break;
                    }
                    writer.WriteEndObject();
                    writer.WriteString("skill", rule.Skill);
                    writer.WriteNumber("level", rule.MinimumLevel);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                break;
            case StickyOwnerStrategy sticky:
                writer.WriteString("k", "sticky");
                writer.WriteNumber("window", (long)sticky.Window.TotalSeconds);
                writer.WritePropertyName("fallback");
                WriteStrategy(writer, sticky.Fallback);
                break;
            case ManualStrategy:
                writer.WriteString("k", "manual");
                break;
        }
        writer.WriteEndObject();
    }

    private static RoutingStrategy ReadStrategy(JsonElement element, int depth)
    {
        if (depth > 1)
            throw new FormatException("Strategies nest at most once.");
        var properties = Object(element, ["k"], ["rules", "window", "fallback"]);
        return String(properties["k"]) switch
        {
            "round-robin" when Only(properties) => RoutingStrategy.RoundRobin,
            "least-busy" when Only(properties) => RoutingStrategy.LeastBusy,
            "manual" when Only(properties) => RoutingStrategy.Manual,
            "skills" when Only(properties, "rules") => new SkillsBasedStrategy(properties["rules"].EnumerateArray().Select(item =>
            {
                var rule = Object(item, ["match", "skill", "level"], []);
                var match = Object(rule["match"], ["k"], ["label", "key", "value"]);
                SkillMatch decoded = String(match["k"]) switch
                {
                    "label" when Only(match, "label") => new LabelSkillMatch(new LabelId(String(match["label"]))),
                    "attribute" when Only(match, "key", "value") => new AttributeSkillMatch(String(match["key"]), String(match["value"])),
                    _ => throw new FormatException("Unknown skill match."),
                };
                return new SkillRule(decoded, String(rule["skill"]), rule["level"].GetInt32());
            }).ToList()),
            "sticky" when Only(properties, "window", "fallback") => new StickyOwnerStrategy(Seconds(properties["window"]), ReadStrategy(properties["fallback"], depth + 1)),
            _ => throw new FormatException("Unknown routing strategy."),
        };
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string[] required, string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An object is required.");
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!required.Contains(property.Name) && !optional.Contains(property.Name))
                throw new FormatException("Unknown property.");
            if (!properties.TryAdd(property.Name, property.Value))
                throw new FormatException("Duplicate property.");
        }
        if (required.Any(name => !properties.ContainsKey(name)))
            throw new FormatException("A required property is missing.");
        return properties;
    }

    /// <summary>True when the discriminated object has exactly <c>k</c> plus <paramref name="names"/>.</summary>
    private static bool Only(Dictionary<string, JsonElement> properties, params string[] names) =>
        properties.Count == names.Length + 1 && names.All(properties.ContainsKey);

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
