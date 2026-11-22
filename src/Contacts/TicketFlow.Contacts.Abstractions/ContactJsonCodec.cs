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

    public static string SerializeAttributes(IReadOnlyDictionary<string, ContactAttributeValue> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WritePropertyName("a");
            WriteAttributes(writer, attributes);
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, oversized or of an unknown version.</exception>
    public static IReadOnlyDictionary<string, ContactAttributeValue> DeserializeAttributes(string json) =>
        Read(json, root =>
        {
            RequireProperties(root, "v", "a");
            RequireVersion(root);
            return ReadAttributes(root.GetProperty("a"));
        });

    public static string SerializeContact(Contact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WritePropertyName("c");
            WriteContact(writer, contact);
            writer.WriteEndObject();
        });
    }

    public static Contact DeserializeContact(string json) =>
        Read(json, root =>
        {
            RequireProperties(root, "v", "c");
            RequireVersion(root);
            return ReadContact(root.GetProperty("c"));
        });

    /// <summary>The merge record payload: snapshot and contributions. Identifiers, time and actor are stored by the caller.</summary>
    public static string SerializeMergeContributions(ContactMergeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WritePropertyName("snapshot");
            WriteContact(writer, record.SourceSnapshot);
            writer.WriteStartArray("moved");
            foreach (var address in record.MovedIdentities)
                WriteAddress(writer, address);
            writer.WriteEndArray();
            writer.WriteStartArray("replaced");
            foreach (var identity in record.ReplacedIdentities)
                WriteIdentity(writer, identity);
            writer.WriteEndArray();
            writer.WriteStartArray("attributes");
            foreach (var change in record.AttributeChanges)
            {
                writer.WriteStartObject();
                writer.WriteString("k", change.Key);
                writer.WritePropertyName("prev");
                if (change.Previous is { } previous)
                    WriteValue(writer, previous);
                else
                    writer.WriteNullValue();
                writer.WritePropertyName("merged");
                WriteValue(writer, change.Merged);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("labels");
            foreach (var label in record.AddedLabels)
                writer.WriteStringValue(label.Value);
            writer.WriteEndArray();
            writer.WriteStartArray("profile");
            foreach (var field in record.FilledProfileFields)
                writer.WriteNumberValue((int)field);
            writer.WriteEndArray();
            writer.WriteBoolean("blocked", record.BlockedChanged);
            writer.WriteEndObject();
        });
    }

    public static ContactMergeRecord DeserializeMergeRecord(string json, Guid id, ContactId target, ContactId source, DateTimeOffset mergedAt, Access.AccessActor actor) =>
        Read(json, root =>
        {
            RequireProperties(root, "v", "snapshot", "moved", "replaced", "attributes", "labels", "profile", "blocked");
            RequireVersion(root);
            var changes = root.GetProperty("attributes").EnumerateArray().Select(change =>
            {
                RequireProperties(change, "k", "prev", "merged");
                var previous = change.GetProperty("prev");
                return new ContactMergeAttributeChange(String(change.GetProperty("k")),
                    previous.ValueKind == JsonValueKind.Null ? (ContactAttributeValue?)null : ReadValue(previous), ReadValue(change.GetProperty("merged")));
            }).ToList();
            return new ContactMergeRecord(id, target, source, mergedAt, actor, ReadContact(root.GetProperty("snapshot")),
                root.GetProperty("moved").EnumerateArray().Select(ReadAddress).ToList(),
                root.GetProperty("replaced").EnumerateArray().Select(ReadIdentity).ToList(),
                changes,
                root.GetProperty("labels").EnumerateArray().Select(label => new LabelId(String(label))).ToList(),
                root.GetProperty("profile").EnumerateArray().Select(field => (ContactProfileField)field.GetInt32()).ToList(),
                root.GetProperty("blocked").GetBoolean());
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
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static T Read<T>(string json, Func<JsonElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEncodedBytes)
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
        if (root.GetProperty("v").GetInt32() != FormatVersion)
            throw new FormatException("Unsupported contact payload version.");
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("A JSON object is required.");
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name, StringComparer.Ordinal))
                throw new FormatException("Unknown property in contact payload.");
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
        foreach (var key in attributes.Keys.Order(StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            WriteValue(writer, attributes[key]);
        }
        writer.WriteEndObject();
    }

    private static Dictionary<string, ContactAttributeValue> ReadAttributes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("An attribute object is required.");
        var attributes = new Dictionary<string, ContactAttributeValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!ContactAttributeDefinition.IsValidKey(property.Name) || !attributes.TryAdd(property.Name, ReadValue(property.Value)))
                throw new FormatException("Invalid or duplicate attribute key.");
            if (attributes.Count > Contact.MaximumAttributes)
                throw new FormatException("Too many attributes.");
        }
        return attributes;
    }

    private static void WriteValue(Utf8JsonWriter writer, ContactAttributeValue value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TextAttributeValue text:
                writer.WriteString("t", "text");
                writer.WriteString("v", text.Value);
                break;
            case NumberAttributeValue number:
                writer.WriteString("t", "number");
                writer.WriteString("v", number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case BooleanAttributeValue boolean:
                writer.WriteString("t", "bool");
                writer.WriteBoolean("v", boolean.Value);
                break;
            case DateAttributeValue date:
                writer.WriteString("t", "date");
                writer.WriteString("v", date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case ListAttributeValue list:
                writer.WriteString("t", "list");
                writer.WriteStartArray("v");
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
        var value = element.GetProperty("v");
        return String(element.GetProperty("t")) switch
        {
            "text" => new TextAttributeValue(String(value)),
            "number" => new NumberAttributeValue(decimal.Parse(String(value), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)),
            "bool" => new BooleanAttributeValue(value.GetBoolean()),
            "date" => new DateAttributeValue(DateOnly.ParseExact(String(value), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "list" => new ListAttributeValue(value.EnumerateArray().Select(String).ToList()),
            _ => throw new FormatException("Unknown attribute value discriminator."),
        };
    }

    private static void WriteAddress(Utf8JsonWriter writer, ContactIdentityAddress address)
    {
        writer.WriteStartObject();
        writer.WriteNumber("k", (int)address.Kind);
        writer.WriteString("ns", address.Namespace);
        writer.WriteString("val", address.Value);
        writer.WriteEndObject();
    }

    private static ContactIdentityAddress ReadAddress(JsonElement element)
    {
        RequireProperties(element, "k", "ns", "val");
        return RestoreAddress((ContactIdentityKind)element.GetProperty("k").GetInt32(), String(element.GetProperty("ns")), String(element.GetProperty("val")));
    }

    private static void WriteIdentity(Utf8JsonWriter writer, ContactIdentity identity)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("a");
        WriteAddress(writer, identity.Address);
        writer.WriteString("s", identity.Verification.IsVerified ? "verified" : "claimed");
        writer.WriteString("src", identity.Verification.Source);
        writer.WriteString("at", identity.Verification.At.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static ContactIdentity ReadIdentity(JsonElement element)
    {
        RequireProperties(element, "a", "s", "src", "at");
        var verified = String(element.GetProperty("s")) switch
        {
            "verified" => true,
            "claimed" => false,
            _ => throw new FormatException("Unknown verification discriminator."),
        };
        return new(ReadAddress(element.GetProperty("a")),
            RestoreVerification(verified, String(element.GetProperty("src")), Instant(element.GetProperty("at"))));
    }

    private static DateTimeOffset Instant(JsonElement element) =>
        DateTimeOffset.ParseExact(String(element), "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static void WriteContact(Utf8JsonWriter writer, Contact contact)
    {
        writer.WriteStartObject();
        writer.WriteString("id", contact.Id.Value);
        writer.WriteStartObject("p");
        writer.WriteString("n", contact.Profile.DisplayName);
        writer.WriteString("a", contact.Profile.AvatarRef);
        writer.WriteString("l", contact.Profile.Locale);
        writer.WriteString("z", contact.Profile.TimeZone);
        writer.WriteString("c", contact.Profile.Company);
        writer.WriteEndObject();
        writer.WriteStartArray("i");
        foreach (var identity in contact.Identities)
            WriteIdentity(writer, identity);
        writer.WriteEndArray();
        writer.WritePropertyName("attrs");
        WriteAttributes(writer, contact.Attributes);
        writer.WriteStartArray("labels");
        foreach (var label in contact.Labels)
            writer.WriteStringValue(label.Value);
        writer.WriteEndArray();
        writer.WriteBoolean("b", contact.Blocked);
        writer.WriteString("ca", contact.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("ua", contact.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("m", contact.MergedInto?.Value);
        writer.WriteEndObject();
    }

    private static Contact ReadContact(JsonElement element)
    {
        RequireProperties(element, "id", "p", "i", "attrs", "labels", "b", "ca", "ua", "m");
        var profile = element.GetProperty("p");
        RequireProperties(profile, "n", "a", "l", "z", "c");
        var merged = OptionalString(element.GetProperty("m"));
        return new Contact(
            new ContactId(String(element.GetProperty("id"))),
            new ContactProfile(OptionalString(profile.GetProperty("n")), OptionalString(profile.GetProperty("a")), OptionalString(profile.GetProperty("l")),
                OptionalString(profile.GetProperty("z")), OptionalString(profile.GetProperty("c"))),
            element.GetProperty("i").EnumerateArray().Select(ReadIdentity).ToList(),
            ReadAttributes(element.GetProperty("attrs")),
            element.GetProperty("labels").EnumerateArray().Select(label => new LabelId(String(label))).ToList(),
            element.GetProperty("b").GetBoolean(),
            Instant(element.GetProperty("ca")),
            Instant(element.GetProperty("ua")),
            merged is null ? null : new ContactId(merged));
    }
}
