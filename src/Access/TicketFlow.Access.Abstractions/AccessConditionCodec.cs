using System.Buffers;
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

    private const int MaximumPooledBufferCapacity = 16 * 1024;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? cachedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? cachedWriter;

    public static string Serialize(AccessCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var buffer = cachedBuffer;
        if (buffer is null)
            cachedBuffer = buffer = new ArrayBufferWriter<byte>(256);
        else
            buffer.ResetWrittenCount();

        var writer = cachedWriter;
        if (writer is null)
            cachedWriter = writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);

        try
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            writer.WritePropertyName("c"u8);
            Write(writer, condition);
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            if (buffer.Capacity > MaximumPooledBufferCapacity)
                cachedBuffer = null;
            else
                buffer.ResetWrittenCount();
        }
    }

    /// <exception cref="FormatException">The payload is malformed, oversized, of an unknown version or violates condition bounds.</exception>
    public static AccessCondition Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumEncodedBytes / 3 && Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
            throw new FormatException("The encoded condition is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = AccessCondition.MaximumDepth * 2 + 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Expected an object.");
            JsonElement vProp = default, cProp = default;
            var seen = 0u;
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("v"u8))
                {
                    if ((seen & 1u) != 0) throw new FormatException("Missing or duplicate condition property.");
                    seen |= 1u;
                    vProp = property.Value;
                }
                else if (property.NameEquals("c"u8))
                {
                    if ((seen & 2u) != 0) throw new FormatException("Missing or duplicate condition property.");
                    seen |= 2u;
                    cProp = property.Value;
                }
                else
                    throw new FormatException("Unexpected condition property.");
            }
            if (seen != 3u)
                throw new FormatException("Missing or duplicate condition property.");
            if (vProp.GetInt32() != FormatVersion)
                throw new FormatException("Unsupported condition format version.");
            return Read(cProp, 1);
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
                writer.WriteString("op"u8, "and"u8);
                WriteItems(writer, and.Operands);
                break;
            case OrCondition or:
                writer.WriteString("op"u8, "or"u8);
                WriteItems(writer, or.Operands);
                break;
            case NotCondition not:
                writer.WriteString("op"u8, "not"u8);
                writer.WritePropertyName("item"u8);
                Write(writer, not.Operand);
                break;
            case AssigneeIsSelfCondition:
                writer.WriteString("op"u8, "assignee-self"u8);
                break;
            case TeamInPrincipalTeamsCondition:
                writer.WriteString("op"u8, "team-in-principal"u8);
                break;
            case AttributeEqualsCondition equals:
                writer.WriteString("op"u8, "attr-eq"u8);
                writer.WriteString("key"u8, equals.Key);
                writer.WriteString("value"u8, equals.Value);
                break;
            case AttributeInCondition @in:
                writer.WriteString("op"u8, "attr-in"u8);
                writer.WriteString("key"u8, @in.Key);
                writer.WriteStartArray("values"u8);
                foreach (var value in @in.Values)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
                break;
            case HasLabelCondition label:
                writer.WriteString("op"u8, "label"u8);
                writer.WriteString("label"u8, label.Label.Value);
                break;
            case TimeWindowCondition window:
                writer.WriteString("op"u8, "time"u8);
                WriteTime(writer, "start"u8, window.StartUtc);
                WriteTime(writer, "end"u8, window.EndUtc);
                break;
        }
        writer.WriteEndObject();
    }

    private static void WriteTime(Utf8JsonWriter writer, ReadOnlySpan<byte> propertyName, TimeOnly value)
    {
        Span<char> chars = stackalloc char[8];
        value.TryFormat(chars, out _, "HH:mm:ss", CultureInfo.InvariantCulture);
        writer.WriteString(propertyName, chars);
    }

    private static void WriteItems(Utf8JsonWriter writer, IReadOnlyList<AccessCondition> items)
    {
        writer.WriteStartArray("items"u8);
        for (var i = 0; i < items.Count; i++)
            Write(writer, items[i]);
        writer.WriteEndArray();
    }

    private static AccessCondition Read(JsonElement node, int depth)
    {
        if (depth > AccessCondition.MaximumDepth)
            throw new FormatException("The encoded condition is too deep.");
        if (node.ValueKind != JsonValueKind.Object)
            throw new FormatException("Condition nodes must be objects.");
        if (!node.TryGetProperty("op"u8, out var opProp))
            throw new FormatException("Missing or duplicate condition property.");

        if (opProp.ValueEquals("and"u8))
        {
            RequireProperties(node, "op", "items");
            return new AndCondition(ReadItems(node, depth));
        }
        if (opProp.ValueEquals("or"u8))
        {
            RequireProperties(node, "op", "items");
            return new OrCondition(ReadItems(node, depth));
        }
        if (opProp.ValueEquals("not"u8))
        {
            RequireProperties(node, "op", "item");
            return new NotCondition(Read(node.GetProperty("item"u8), depth + 1));
        }
        if (opProp.ValueEquals("assignee-self"u8))
        {
            RequireProperties(node, "op");
            return new AssigneeIsSelfCondition();
        }
        if (opProp.ValueEquals("team-in-principal"u8))
        {
            RequireProperties(node, "op");
            return new TeamInPrincipalTeamsCondition();
        }
        if (opProp.ValueEquals("attr-eq"u8))
        {
            RequireProperties(node, "op", "key", "value");
            return new AttributeEqualsCondition(String(node, "key"u8), String(node, "value"u8));
        }
        if (opProp.ValueEquals("attr-in"u8))
        {
            RequireProperties(node, "op", "key", "values");
            var values = node.GetProperty("values"u8);
            if (values.ValueKind != JsonValueKind.Array)
                throw new FormatException("Attribute value lists must be bounded arrays.");
            var count = values.GetArrayLength();
            if (count > AccessCondition.MaximumValues)
                throw new FormatException("Attribute value lists must be bounded arrays.");
            var items = new string[count];
            var index = 0;
            foreach (var value in values.EnumerateArray())
                items[index++] = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("Values must be strings.");
            return new AttributeInCondition(String(node, "key"u8), items);
        }
        if (opProp.ValueEquals("label"u8))
        {
            RequireProperties(node, "op", "label");
            return new HasLabelCondition(new LabelId(String(node, "label"u8)));
        }
        if (opProp.ValueEquals("time"u8))
        {
            RequireProperties(node, "op", "start", "end");
            return new TimeWindowCondition(Time(node, "start"u8), Time(node, "end"u8));
        }
        throw new FormatException("Unknown condition discriminator.");
    }

    private static AccessCondition[] ReadItems(JsonElement node, int depth)
    {
        var items = node.GetProperty("items"u8);
        if (items.ValueKind != JsonValueKind.Array)
            throw new FormatException("Condition operands must be bounded arrays.");
        var count = items.GetArrayLength();
        if (count > AccessCondition.MaximumNodes)
            throw new FormatException("Condition operands must be bounded arrays.");
        var result = new AccessCondition[count];
        var index = 0;
        foreach (var item in items.EnumerateArray())
            result[index++] = Read(item, depth + 1);
        return result;
    }

    private static string String(JsonElement node, ReadOnlySpan<byte> name)
    {
        var value = node.GetProperty(name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException("Expected a string.");
    }

    private static TimeOnly Time(JsonElement node, ReadOnlySpan<byte> name) =>
        TimeOnly.ParseExact(String(node, name), "HH:mm:ss", CultureInfo.InvariantCulture);

    private static void RequireProperties(JsonElement node, params ReadOnlySpan<string> names)
    {
        if (node.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected an object.");
        var seen = 0u;
        foreach (var property in node.EnumerateObject())
        {
            var matched = -1;
            for (var i = 0; i < names.Length; i++)
                if (property.NameEquals(names[i]))
                {
                    matched = i;
                    break;
                }
            if (matched < 0)
                throw new FormatException("Unexpected condition property.");
            var bit = 1u << matched;
            if ((seen & bit) != 0)
                throw new FormatException("Missing or duplicate condition property.");
            seen |= bit;
        }
        if (seen != (1u << names.Length) - 1u)
            throw new FormatException("Missing or duplicate condition property.");
    }
}
