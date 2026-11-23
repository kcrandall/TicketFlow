using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// Versioned persisted JSON for contact attribute maps, contact snapshots and merge contributions. Every envelope is
/// <c>{"v":1,...}</c>; attribute values carry an explicit <c>"t"</c> discriminator (<c>text</c>, <c>number</c>, <c>bool</c>,
/// <c>date</c>, <c>list</c>) and verifications an explicit <c>"s"</c> (<c>verified</c>, <c>claimed</c>). Unions are never
/// serialized directly. Decoding re-validates every value and rejects unknown versions, discriminators and properties.
/// </summary>
public static class ContactJsonCodec
{
    public const int FormatVersion = 1;

    /// <summary>Maximum encoded size in UTF-8 bytes.</summary>
    public const int MaximumEncodedBytes = 4 * 1024 * 1024;

    private const string EmptyAttributesJson = "{\"v\":1,\"a\":{}}";
    private const int MaxPooledBufferCapacity = 64 * 1024;
    private static readonly IReadOnlyDictionary<string, ContactAttributeValue> EmptyAttributes = FrozenDictionary<string, ContactAttributeValue>.Empty;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    public static string SerializeAttributes(IReadOnlyDictionary<string, ContactAttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count == 0)
            return EmptyAttributesJson;
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            writer.WritePropertyName("a"u8);
            WriteAttributes(writer, attributes);
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, oversized or of an unknown version.</exception>
    public static IReadOnlyDictionary<string, ContactAttributeValue> DeserializeAttributes(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json == EmptyAttributesJson)
            return EmptyAttributes;
        return Read(json, static root =>
        {
            RequireProperties(root, "v", "a");
            RequireVersion(root);
            return ReadAttributes(root.GetProperty("a"u8));
        });
    }

    public static string SerializeContact(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            writer.WritePropertyName("c"u8);
            WriteContact(writer, contact);
            writer.WriteEndObject();
        });
    }

    public static Contact DeserializeContact(string json) =>
        Read(json, static root =>
        {
            RequireProperties(root, "v", "c");
            RequireVersion(root);
            return ReadContact(root.GetProperty("c"u8));
        });

    /// <summary>The merge record payload: snapshot and contributions. Identifiers, time and actor are stored by the caller.</summary>
    public static string SerializeMergeContributions(ContactMergeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            writer.WritePropertyName("snapshot"u8);
            WriteContact(writer, record.SourceSnapshot);
            writer.WriteStartArray("moved"u8);
            foreach (var address in record.MovedIdentities)
                WriteAddress(writer, address);
            writer.WriteEndArray();
            writer.WriteStartArray("replaced"u8);
            foreach (var identity in record.ReplacedIdentities)
                WriteIdentity(writer, identity);
            writer.WriteEndArray();
            writer.WriteStartArray("attributes"u8);
            foreach (var change in record.AttributeChanges)
            {
                writer.WriteStartObject();
                writer.WriteString("k"u8, change.Key);
                writer.WritePropertyName("prev"u8);
                if (change.Previous is { } previous)
                    WriteValue(writer, previous);
                else
                    writer.WriteNullValue();
                writer.WritePropertyName("merged"u8);
                WriteValue(writer, change.Merged);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("labels"u8);
            foreach (var label in record.AddedLabels)
                writer.WriteStringValue(label.Value);
            writer.WriteEndArray();
            writer.WriteStartArray("profile"u8);
            foreach (var field in record.FilledProfileFields)
                writer.WriteNumberValue((int)field);
            writer.WriteEndArray();
            writer.WriteBoolean("blocked"u8, record.BlockedChanged);
            writer.WriteEndObject();
        });
    }

    public static ContactMergeRecord DeserializeMergeRecord(string json, Guid id, ContactId target, ContactId source, DateTimeOffset mergedAt, Access.AccessActor actor) =>
        Read(json, root =>
        {
            RequireProperties(root, "v", "snapshot", "moved", "replaced", "attributes", "labels", "profile", "blocked");
            RequireVersion(root);
            var changes = new List<ContactMergeAttributeChange>();
            foreach (var change in root.GetProperty("attributes"u8).EnumerateArray())
            {
                RequireProperties(change, "k", "prev", "merged");
                var previous = change.GetProperty("prev"u8);
                changes.Add(new ContactMergeAttributeChange(String(change.GetProperty("k"u8)),
                    previous.ValueKind == JsonValueKind.Null ? (ContactAttributeValue?)null : ReadValue(previous), ReadValue(change.GetProperty("merged"u8))));
            }
            var moved = new List<ContactIdentityAddress>();
            foreach (var item in root.GetProperty("moved"u8).EnumerateArray())
                moved.Add(ReadAddress(item));
            var replaced = new List<ContactIdentity>();
            foreach (var item in root.GetProperty("replaced"u8).EnumerateArray())
                replaced.Add(ReadIdentity(item));
            var labels = new List<LabelId>();
            foreach (var label in root.GetProperty("labels"u8).EnumerateArray())
                labels.Add(new LabelId(String(label)));
            var profile = new List<ContactProfileField>();
            foreach (var field in root.GetProperty("profile"u8).EnumerateArray())
                profile.Add((ContactProfileField)field.GetInt32());
            return new ContactMergeRecord(id, target, source, mergedAt, actor, ReadContact(root.GetProperty("snapshot"u8)),
                moved, replaced, changes, labels, profile, root.GetProperty("blocked"u8).GetBoolean());
        });

    /// <summary>Reconstitutes a stored normalized address, rejecting values that do not normalize to themselves.</summary>
    public static ContactIdentityAddress RestoreAddress(ContactIdentityKind kind, string @namespace, string value) =>
        ContactIdentityAddress.Normalize(kind, value, @namespace.Length == 0 ? null : @namespace) switch
        {
            ContactIdentityAddress address when address.Value == value => address,
            ContactIdentityAddress => throw new FormatException("A stored address is not normalized."),
            ContactAddressInvalid => throw new FormatException("A stored address is invalid."),
        };

    /// <summary>Builds a stored verification from its persisted parts (<paramref name="verified"/> discriminator).</summary>
    public static IdentityVerification RestoreVerification(bool verified, string source, DateTimeOffset at) =>
        verified ? new IdentityVerified(source, at) : new IdentityClaimed(source, at);

    private static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = sharedBuffer;
        if (buffer is null)
            sharedBuffer = buffer = new ArrayBufferWriter<byte>(512);
        else
            buffer.Clear();

        var writer = sharedWriter;
        if (writer is null)
            sharedWriter = writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);

        try
        {
            write(writer);
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            if (buffer.Capacity > MaxPooledBufferCapacity)
            {
                sharedBuffer = null;
                sharedWriter = null;
                writer.Dispose();
            }
            else
            {
                buffer.Clear();
                writer.Reset(buffer);
            }
        }
    }

    private static T Read<T>(string json, Func<JsonElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumEncodedBytes / 3 && Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
            throw new FormatException("The encoded contact payload is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return read(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new FormatException("The encoded contact payload is invalid.", error);
        }
    }

    private static void RequireVersion(JsonElement root)
    {
        if (root.GetProperty("v"u8).GetInt32() != FormatVersion)
            throw new FormatException("Unsupported contact payload version.");
    }

    private static void RequireProperties(JsonElement element, params ReadOnlySpan<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON object is required.");
        uint seen = 0;
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            var matched = -1;
            for (var i = 0; i < names.Length; i++)
            {
                if (property.NameEquals(names[i]))
                {
                    matched = i;
                    break;
                }
            }
            if (matched < 0)
                throw new FormatException("Unknown property in contact payload.");
            var bit = 1u << matched;
            if ((seen & bit) != 0)
                throw new FormatException("Missing or duplicate property in contact payload.");
            seen |= bit;
            count++;
        }
        if (count != names.Length)
            throw new FormatException("Missing or duplicate property in contact payload.");
    }

    private static string String(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException("A string is required.");

    private static string? OptionalString(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : String(element);

    private static void WriteAttributes(Utf8JsonWriter writer, IReadOnlyDictionary<string, ContactAttributeValue> attributes)
    {
        writer.WriteStartObject();
        if (attributes.Count > 0)
        {
            var keys = new string[attributes.Count];
            var index = 0;
            foreach (var key in attributes.Keys)
                keys[index++] = key;
            if (keys.Length > 1)
                Array.Sort(keys, StringComparer.Ordinal);
            foreach (var key in keys)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, attributes[key]);
            }
        }
        writer.WriteEndObject();
    }

    private static IReadOnlyDictionary<string, ContactAttributeValue> ReadAttributes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An attribute object is required.");
        Dictionary<string, ContactAttributeValue>? attributes = null;
        foreach (var property in element.EnumerateObject())
        {
            attributes ??= new Dictionary<string, ContactAttributeValue>(StringComparer.Ordinal);
            if (!ContactAttributeDefinition.IsValidKey(property.Name) || !attributes.TryAdd(property.Name, ReadValue(property.Value)))
                throw new FormatException("Invalid or duplicate attribute key.");
            if (attributes.Count > Contact.MaximumAttributes)
                throw new FormatException("Too many attributes.");
        }
        return attributes is null ? EmptyAttributes : attributes;
    }

    private static void WriteValue(Utf8JsonWriter writer, ContactAttributeValue value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TextAttributeValue text:
                writer.WriteString("t"u8, "text"u8);
                writer.WriteString("v"u8, text.Value);
                break;
            case NumberAttributeValue number:
                writer.WriteString("t"u8, "number"u8);
                Span<char> numBuf = stackalloc char[32];
                if (number.Value.TryFormat(numBuf, out var numLen, default, CultureInfo.InvariantCulture))
                    writer.WriteString("v"u8, numBuf[..numLen]);
                else
                    writer.WriteString("v"u8, number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case BooleanAttributeValue boolean:
                writer.WriteString("t"u8, "bool"u8);
                writer.WriteBoolean("v"u8, boolean.Value);
                break;
            case DateAttributeValue date:
                writer.WriteString("t"u8, "date"u8);
                Span<char> dateBuf = stackalloc char[10];
                date.Value.TryFormat(dateBuf, out var dateLen, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                writer.WriteString("v"u8, dateBuf[..dateLen]);
                break;
            case ListAttributeValue list:
                writer.WriteString("t"u8, "list"u8);
                writer.WriteStartArray("v"u8);
                foreach (var item in list.Items)
                    writer.WriteStringValue(item);
                writer.WriteEndArray();
                break;
        }
        writer.WriteEndObject();
    }

    private static ContactAttributeValue ReadValue(JsonElement element)
    {
        RequireProperties(element, "t", "v");
        var type = element.GetProperty("t"u8);
        if (type.ValueKind != JsonValueKind.String)
            throw new FormatException("A string is required.");
        var value = element.GetProperty("v"u8);
        if (type.ValueEquals("text"u8))
            return new TextAttributeValue(String(value));
        if (type.ValueEquals("number"u8))
            return new NumberAttributeValue(decimal.Parse(String(value), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture));
        if (type.ValueEquals("bool"u8))
            return new BooleanAttributeValue(value.GetBoolean());
        if (type.ValueEquals("date"u8))
            return new DateAttributeValue(DateOnly.ParseExact(String(value), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (type.ValueEquals("list"u8))
        {
            var items = new List<string>();
            foreach (var item in value.EnumerateArray())
                items.Add(String(item));
            return new ListAttributeValue(items);
        }
        throw new FormatException("Unknown attribute value discriminator.");
    }

    private static void WriteAddress(Utf8JsonWriter writer, ContactIdentityAddress address)
    {
        writer.WriteStartObject();
        writer.WriteNumber("k"u8, (int)address.Kind);
        writer.WriteString("ns"u8, address.Namespace);
        writer.WriteString("val"u8, address.Value);
        writer.WriteEndObject();
    }

    private static ContactIdentityAddress ReadAddress(JsonElement element)
    {
        RequireProperties(element, "k", "ns", "val");
        return RestoreAddress((ContactIdentityKind)element.GetProperty("k"u8).GetInt32(), String(element.GetProperty("ns"u8)), String(element.GetProperty("val"u8)));
    }

    private static void WriteInstant(Utf8JsonWriter writer, ReadOnlySpan<byte> propertyName, DateTimeOffset value)
    {
        Span<char> buffer = stackalloc char[33];
        value.TryFormat(buffer, out var written, "O", CultureInfo.InvariantCulture);
        writer.WriteString(propertyName, buffer[..written]);
    }

    private static void WriteIdentity(Utf8JsonWriter writer, ContactIdentity identity)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("a"u8);
        WriteAddress(writer, identity.Address);
        writer.WriteString("s"u8, identity.Verification.IsVerified ? "verified"u8 : "claimed"u8);
        writer.WriteString("src"u8, identity.Verification.Source);
        WriteInstant(writer, "at"u8, identity.Verification.At);
        writer.WriteEndObject();
    }

    private static ContactIdentity ReadIdentity(JsonElement element)
    {
        RequireProperties(element, "a", "s", "src", "at");
        var status = element.GetProperty("s"u8);
        if (status.ValueKind != JsonValueKind.String)
            throw new FormatException("A string is required.");
        bool verified;
        if (status.ValueEquals("verified"u8))
            verified = true;
        else if (status.ValueEquals("claimed"u8))
            verified = false;
        else
            throw new FormatException("Unknown verification discriminator.");
        return new(ReadAddress(element.GetProperty("a"u8)),
            RestoreVerification(verified, String(element.GetProperty("src"u8)), Instant(element.GetProperty("at"u8))));
    }

    private static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.ParseExact(String(element), "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static void WriteContact(Utf8JsonWriter writer, Contact contact)
    {
        writer.WriteStartObject();
        writer.WriteString("id"u8, contact.Id.Value);
        writer.WriteStartObject("p"u8);
        writer.WriteString("n"u8, contact.Profile.DisplayName);
        writer.WriteString("a"u8, contact.Profile.AvatarRef);
        writer.WriteString("l"u8, contact.Profile.Locale);
        writer.WriteString("z"u8, contact.Profile.TimeZone);
        writer.WriteString("c"u8, contact.Profile.Company);
        writer.WriteEndObject();
        writer.WriteStartArray("i"u8);
        foreach (var identity in contact.Identities)
            WriteIdentity(writer, identity);
        writer.WriteEndArray();
        writer.WritePropertyName("attrs"u8);
        WriteAttributes(writer, contact.Attributes);
        writer.WriteStartArray("labels"u8);
        foreach (var label in contact.Labels)
            writer.WriteStringValue(label.Value);
        writer.WriteEndArray();
        writer.WriteBoolean("b"u8, contact.Blocked);
        WriteInstant(writer, "ca"u8, contact.CreatedAt);
        WriteInstant(writer, "ua"u8, contact.UpdatedAt);
        writer.WriteString("m"u8, contact.MergedInto?.Value);
        writer.WriteEndObject();
    }

    private static Contact ReadContact(JsonElement element)
    {
        RequireProperties(element, "id", "p", "i", "attrs", "labels", "b", "ca", "ua", "m");
        var profile = element.GetProperty("p"u8);
        RequireProperties(profile, "n", "a", "l", "z", "c");
        var merged = OptionalString(element.GetProperty("m"u8));
        var identities = new List<ContactIdentity>();
        foreach (var item in element.GetProperty("i"u8).EnumerateArray())
            identities.Add(ReadIdentity(item));
        var labels = new List<LabelId>();
        foreach (var label in element.GetProperty("labels"u8).EnumerateArray())
            labels.Add(new LabelId(String(label)));
        return new Contact(
            new ContactId(String(element.GetProperty("id"u8))),
            new ContactProfile(OptionalString(profile.GetProperty("n"u8)), OptionalString(profile.GetProperty("a"u8)), OptionalString(profile.GetProperty("l"u8)),
                OptionalString(profile.GetProperty("z"u8)), OptionalString(profile.GetProperty("c"u8))),
            identities,
            ReadAttributes(element.GetProperty("attrs"u8)),
            labels,
            element.GetProperty("b"u8).GetBoolean(),
            Instant(element.GetProperty("ca"u8)),
            Instant(element.GetProperty("ua"u8)),
            merged is null ? null : new ContactId(merged));
    }
}
