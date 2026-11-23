using System.Buffers;
using System.Text;
using System.Text.Json;

namespace TicketFlow.Contacts;

/// <summary>
/// Versioned persisted JSON for <see cref="ContactLinkingPolicy"/>: <c>{"v":1,"k":"off"}</c>, <c>{"v":1,"k":"suggest"}</c> or
/// <c>{"v":1,"k":"auto-merge","kinds":[1,2],"min":1,"unverified":true}</c> (kinds are <see cref="ContactIdentityKind"/> numbers), with an
/// optional <c>"claimed":true</c> on suggest/auto-merge when claimed evidence may produce suggestions.
/// The union is never serialized directly; decoding rejects unknown versions, discriminators and properties.
/// </summary>
public static class ContactLinkingCodec
{
    public const int FormatVersion = 1;
    private const int MaximumEncodedLength = 4096;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? sharedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? sharedWriter;

    public static string SerializePolicy(ContactLinkingPolicy policy)
    {
        if (policy.Value is null)
            throw new ArgumentException("A policy is required.", nameof(policy));
        var buffer = sharedBuffer;
        var writer = sharedWriter;
        sharedBuffer = null;
        sharedWriter = null;
        if (buffer is null)
            buffer = new ArrayBufferWriter<byte>(128);
        else
            buffer.Clear();
        if (writer is null)
            writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);
        try
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, FormatVersion);
            switch (policy)
            {
                case LinkingOff:
                    writer.WriteString("k"u8, "off"u8);
                    break;
                case LinkingSuggest suggest:
                    writer.WriteString("k"u8, "suggest"u8);
                    if (suggest.IncludeClaimedEvidence)
                        writer.WriteBoolean("claimed"u8, true);
                    break;
                case LinkingAutoMerge auto:
                    writer.WriteString("k"u8, "auto-merge"u8);
                    writer.WriteStartArray("kinds"u8);
                    foreach (var kind in auto.Rules.Kinds)
                        writer.WriteNumberValue((int)kind);
                    writer.WriteEndArray();
                    writer.WriteNumber("min"u8, auto.Rules.MinimumMatchingIdentities);
                    writer.WriteBoolean("unverified"u8, auto.Rules.RequireUnverifiedCandidate);
                    if (auto.IncludeClaimedEvidence)
                        writer.WriteBoolean("claimed"u8, true);
                    break;
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Clear();
            if (buffer.Capacity <= 4096)
            {
                sharedBuffer = buffer;
                sharedWriter = writer;
            }
            else
                writer.Dispose();
        }
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
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("Unsupported linking policy version.");

            uint seen = 0;
            var hasVersion = false;
            JsonElement kindEl = default, kindsEl = default, minEl = default, unverifiedEl = default;
            var claimed = false;

            foreach (var property in root.EnumerateObject())
            {
                int bit;
                if (property.NameEquals("v"u8))
                {
                    bit = 0;
                    if (property.Value.GetInt32() != FormatVersion)
                        throw new FormatException("Unsupported linking policy version.");
                    hasVersion = true;
                }
                else if (property.NameEquals("k"u8))
                {
                    bit = 1;
                    kindEl = property.Value;
                }
                else if (property.NameEquals("claimed"u8))
                {
                    bit = 2;
                    if (property.Value.ValueKind != JsonValueKind.True)
                        throw new FormatException("The claimed-evidence flag must be true when present.");
                    claimed = true;
                }
                else if (property.NameEquals("kinds"u8))
                {
                    bit = 3;
                    kindsEl = property.Value;
                }
                else if (property.NameEquals("min"u8))
                {
                    bit = 4;
                    minEl = property.Value;
                }
                else if (property.NameEquals("unverified"u8))
                {
                    bit = 5;
                    unverifiedEl = property.Value;
                }
                else
                    throw new FormatException("Unknown linking policy kind or properties.");

                var mask = 1u << bit;
                if ((seen & mask) != 0)
                    throw new FormatException("Duplicate linking policy properties.");
                seen |= mask;
            }

            if (!hasVersion || (seen & 0b10u) == 0 || kindEl.ValueKind != JsonValueKind.String)
                throw new FormatException("Unsupported linking policy version.");

            if (kindEl.ValueEquals("off"u8) && seen == 0b000011u)
                return LinkingOff.Instance;
            if (kindEl.ValueEquals("suggest"u8) && (seen & ~0b000100u) == 0b000011u)
                return claimed ? new LinkingSuggest(true) : LinkingSuggest.Instance;
            if (kindEl.ValueEquals("auto-merge"u8) && (seen & ~0b000100u) == 0b111011u)
            {
                var kinds = new List<ContactIdentityKind>();
                foreach (var item in kindsEl.EnumerateArray())
                    kinds.Add((ContactIdentityKind)item.GetInt32());
                return new LinkingAutoMerge(new ContactAutoMergeRules(kinds, minEl.GetInt32(), unverifiedEl.GetBoolean()), claimed);
            }
            throw new FormatException("Unknown linking policy kind or properties.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("The linking policy payload is invalid.", error);
        }
    }
}
