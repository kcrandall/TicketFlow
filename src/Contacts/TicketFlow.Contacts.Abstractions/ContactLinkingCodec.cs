using System.Text;
using System.Text.Json;

namespace TicketFlow.Contacts;

/// <summary>
/// Versioned persisted JSON for <see cref="ContactLinkingPolicy"/>: <c>{"v":1,"k":"off"}</c>, <c>{"v":1,"k":"suggest"}</c> or
/// <c>{"v":1,"k":"auto-merge","kinds":[1,2],"min":1,"unverified":true}</c> (kinds are <see cref="ContactIdentityKind"/> numbers).
/// The union is never serialized directly; decoding rejects unknown versions, discriminators and properties.
/// </summary>
public static class ContactLinkingCodec
{
    public const int FormatVersion = 1;
    private const int MaximumEncodedLength = 4096;

    public static string SerializePolicy(ContactLinkingPolicy policy)
    {
        if (policy.Value is null)
            throw new ArgumentException("A policy is required.", nameof(policy));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            switch (policy)
            {
                case LinkingOff:
                    writer.WriteString("k", "off");
                    break;
                case LinkingSuggest:
                    writer.WriteString("k", "suggest");
                    break;
                case LinkingAutoMerge auto:
                    writer.WriteString("k", "auto-merge");
                    writer.WriteStartArray("kinds");
                    foreach (var kind in auto.Rules.Kinds)
                        writer.WriteNumberValue((int)kind);
                    writer.WriteEndArray();
                    writer.WriteNumber("min", auto.Rules.MinimumMatchingIdentities);
                    writer.WriteBoolean("unverified", auto.Rules.RequireUnverifiedCandidate);
                    break;
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <exception cref="FormatException">The payload is malformed or of an unknown version or kind.</exception>
    public static ContactLinkingPolicy DeserializePolicy(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumEncodedLength)
            throw new FormatException("The linking policy payload is too large.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("v").GetInt32() != FormatVersion)
                throw new FormatException("Unsupported linking policy version.");
            var kind = root.GetProperty("k").GetString();
            var names = root.EnumerateObject().Select(property => property.Name).ToList();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
                throw new FormatException("Duplicate linking policy properties.");
            switch (kind)
            {
                case "off" when names.Count == 2:
                    return LinkingOff.Instance;
                case "suggest" when names.Count == 2:
                    return LinkingSuggest.Instance;
                case "auto-merge" when names.Count == 5:
                    var kinds = root.GetProperty("kinds").EnumerateArray().Select(item => (ContactIdentityKind)item.GetInt32()).ToList();
                    return new LinkingAutoMerge(new ContactAutoMergeRules(kinds, root.GetProperty("min").GetInt32(), root.GetProperty("unverified").GetBoolean()));
                default:
                    throw new FormatException("Unknown linking policy kind or properties.");
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("The linking policy payload is invalid.", error);
        }
    }
}
