using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Versioned persisted JSON for <see cref="AccessCondition"/> trees. The envelope is <c>{"v":1,"c":node}</c> and every node carries
/// an explicit <c>"op"</c> discriminator (<c>and</c>, <c>or</c>, <c>not</c>, <c>assignee-self</c>, <c>team-in-principal</c>,
/// <c>attr-eq</c>, <c>attr-in</c>, <c>label</c>, <c>time</c>). Decoding enforces the same bounds as construction and rejects unknown
/// versions, discriminators and properties.
/// </summary>
public static class AccessConditionCodec
{
    public const int FormatVersion = 1;

    /// <summary>Maximum encoded size in UTF-8 bytes.</summary>
    public const int MaximumEncodedBytes = 64 * 1024;

    public static string Serialize(AccessCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WritePropertyName("c");
            Write(writer, condition);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <exception cref="FormatException">The payload is malformed, oversized, of an unknown version or violates condition bounds.</exception>
    public static AccessCondition Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
            throw new FormatException("The encoded condition is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = AccessCondition.MaximumDepth * 2 + 4 });
            var root = document.RootElement;
            RequireProperties(root, "v", "c");
            if (root.GetProperty("v").GetInt32() != FormatVersion)
                throw new FormatException("Unsupported condition format version.");
            return Read(root.GetProperty("c"), 1);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new FormatException("The encoded condition is invalid.", error);
        }
    }

    private static void Write(Utf8JsonWriter writer, AccessCondition condition)
    {
        writer.WriteStartObject();
        switch (condition)
        {
            case AndCondition and:
                writer.WriteString("op", "and");
                WriteItems(writer, and.Operands);
                break;
            case OrCondition or:
                writer.WriteString("op", "or");
                WriteItems(writer, or.Operands);
                break;
            case NotCondition not:
                writer.WriteString("op", "not");
                writer.WritePropertyName("item");
                Write(writer, not.Operand);
                break;
            case AssigneeIsSelfCondition:
                writer.WriteString("op", "assignee-self");
                break;
            case TeamInPrincipalTeamsCondition:
                writer.WriteString("op", "team-in-principal");
                break;
            case AttributeEqualsCondition equals:
                writer.WriteString("op", "attr-eq");
                writer.WriteString("key", equals.Key);
                writer.WriteString("value", equals.Value);
                break;
            case AttributeInCondition @in:
                writer.WriteString("op", "attr-in");
                writer.WriteString("key", @in.Key);
                writer.WriteStartArray("values");
                foreach (var value in @in.Values)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
                break;
            case HasLabelCondition label:
                writer.WriteString("op", "label");
                writer.WriteString("label", label.Label.Value);
                break;
            case TimeWindowCondition window:
                writer.WriteString("op", "time");
                writer.WriteString("start", window.StartUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                writer.WriteString("end", window.EndUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteItems(Utf8JsonWriter writer, IReadOnlyList<AccessCondition> items)
    {
        writer.WriteStartArray("items");
        foreach (var item in items)
            Write(writer, item);
        writer.WriteEndArray();
    }

    private static AccessCondition Read(JsonElement node, int depth)
    {
        if (depth > AccessCondition.MaximumDepth)
            throw new FormatException("The encoded condition is too deep.");
        if (node.ValueKind != JsonValueKind.Object)
            throw new FormatException("Condition nodes must be objects.");
        var op = node.GetProperty("op").GetString();
        switch (op)
        {
            case "and":
                RequireProperties(node, "op", "items");
                return new AndCondition(ReadItems(node, depth));
            case "or":
                RequireProperties(node, "op", "items");
                return new OrCondition(ReadItems(node, depth));
            case "not":
                RequireProperties(node, "op", "item");
                return new NotCondition(Read(node.GetProperty("item"), depth + 1));
            case "assignee-self":
                RequireProperties(node, "op");
                return new AssigneeIsSelfCondition();
            case "team-in-principal":
                RequireProperties(node, "op");
                return new TeamInPrincipalTeamsCondition();
            case "attr-eq":
                RequireProperties(node, "op", "key", "value");
                return new AttributeEqualsCondition(String(node, "key"), String(node, "value"));
            case "attr-in":
                RequireProperties(node, "op", "key", "values");
                var values = node.GetProperty("values");
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > AccessCondition.MaximumValues)
                    throw new FormatException("Attribute value lists must be bounded arrays.");
                return new AttributeInCondition(String(node, "key"), values.EnumerateArray().Select(value => value.GetString() ?? throw new FormatException("Values must be strings.")).ToArray());
            case "label":
                RequireProperties(node, "op", "label");
                return new HasLabelCondition(new LabelId(String(node, "label")));
            case "time":
                RequireProperties(node, "op", "start", "end");
                return new TimeWindowCondition(Time(node, "start"), Time(node, "end"));
            default:
                throw new FormatException("Unknown condition discriminator.");
        }
    }

    private static List<AccessCondition> ReadItems(JsonElement node, int depth)
    {
        var items = node.GetProperty("items");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > AccessCondition.MaximumNodes)
            throw new FormatException("Condition operands must be bounded arrays.");
        return [.. items.EnumerateArray().Select(item => Read(item, depth + 1))];
    }

    private static string String(JsonElement node, string name)
    {
        var value = node.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("Expected a string.");
    }

    private static TimeOnly Time(JsonElement node, string name) =>
        TimeOnly.ParseExact(String(node, name), "HH:mm:ss", CultureInfo.InvariantCulture);

    private static void RequireProperties(JsonElement node, params string[] names)
    {
        if (node.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected an object.");
        var count = 0;
        foreach (var property in node.EnumerateObject())
        {
            if (Array.IndexOf(names, property.Name) < 0)
                throw new FormatException("Unexpected condition property.");
            count++;
        }
        if (count != names.Length)
            throw new FormatException("Missing or duplicate condition property.");
    }
}
