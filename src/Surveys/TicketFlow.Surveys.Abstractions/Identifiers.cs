using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>Validated opaque identifier of a survey definition, unique within a workspace.</summary>
public readonly record struct SurveyId : IComparable<SurveyId>
{
    private readonly string? value;

    public SurveyId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The SurveyId is uninitialized.");

    public bool IsInitialized => value is not null;

    public int CompareTo(SurveyId other) => string.CompareOrdinal(value, other.value);

    public static bool operator <(SurveyId left, SurveyId right) => left.CompareTo(right) < 0;
    public static bool operator <=(SurveyId left, SurveyId right) => left.CompareTo(right) <= 0;
    public static bool operator >(SurveyId left, SurveyId right) => left.CompareTo(right) > 0;
    public static bool operator >=(SurveyId left, SurveyId right) => left.CompareTo(right) >= 0;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Identifier of a survey invitation: a non-empty GUID.</summary>
public readonly record struct SurveyInvitationId : IComparable<SurveyInvitationId>
{
    public SurveyInvitationId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("An invitation identifier cannot be empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public bool IsInitialized => Value != Guid.Empty;

    public static SurveyInvitationId New() => new(Guid.CreateVersion7());

    public int CompareTo(SurveyInvitationId other) => Value.CompareTo(other.Value);

    public static bool operator <(SurveyInvitationId left, SurveyInvitationId right) => left.CompareTo(right) < 0;
    public static bool operator <=(SurveyInvitationId left, SurveyInvitationId right) => left.CompareTo(right) <= 0;
    public static bool operator >(SurveyInvitationId left, SurveyInvitationId right) => left.CompareTo(right) > 0;
    public static bool operator >=(SurveyInvitationId left, SurveyInvitationId right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString("D");
}

/// <summary>
/// A locale tag such as <c>en</c> or <c>pt-BR</c>: a 2-3 letter language plus up to three 2-8 character alphanumeric subtags, at
/// most 35 characters, canonicalized (language lowercase, script title-case, region uppercase).
/// </summary>
public readonly record struct SurveyLocale : IComparable<SurveyLocale>
{
    public const int MaximumLength = 35;

    private readonly string? value;

    public SurveyLocale(string value) => this.value = Canonicalize(value) ?? throw new ArgumentException("Locales must be BCP 47 style tags such as 'en' or 'pt-BR'.", nameof(value));

    /// <exception cref="InvalidOperationException">The locale is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The SurveyLocale is uninitialized.");

    public bool IsInitialized => value is not null;

    public static bool TryParse(string? value, out SurveyLocale locale)
    {
        var canonical = value is null ? null : Canonicalize(value);
        locale = canonical is null ? default : new SurveyLocale(canonical, true);
        return canonical is not null;
    }

    private SurveyLocale(string canonical, bool _) => value = canonical;

    public int CompareTo(SurveyLocale other) => string.CompareOrdinal(value, other.value);

    public static bool operator <(SurveyLocale left, SurveyLocale right) => left.CompareTo(right) < 0;
    public static bool operator <=(SurveyLocale left, SurveyLocale right) => left.CompareTo(right) <= 0;
    public static bool operator >(SurveyLocale left, SurveyLocale right) => left.CompareTo(right) > 0;
    public static bool operator >=(SurveyLocale left, SurveyLocale right) => left.CompareTo(right) >= 0;

    public override string ToString() => value ?? string.Empty;

    private static string? Canonicalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
            return null;
        var span = value.AsSpan();
        Span<char> dest = stackalloc char[span.Length];
        var pos = 0;
        var partIndex = 0;
        var changed = false;
        while (true)
        {
            if (partIndex >= 4)
                return null;
            var dash = span[pos..].IndexOf('-');
            var partLen = dash < 0 ? span.Length - pos : dash;
            var part = span.Slice(pos, partLen);
            if (partIndex == 0)
            {
                if (partLen is < 2 or > 3)
                    return null;
                for (var i = 0; i < partLen; i++)
                {
                    var c = part[i];
                    if (!char.IsAsciiLetter(c))
                        return null;
                    var lower = char.ToLowerInvariant(c);
                    dest[pos + i] = lower;
                    if (lower != c)
                        changed = true;
                }
            }
            else
            {
                if (partLen is < 2 or > 8)
                    return null;
                var allLetters = true;
                for (var i = 0; i < partLen; i++)
                {
                    var c = part[i];
                    if (!char.IsAsciiLetterOrDigit(c))
                        return null;
                    if (!char.IsAsciiLetter(c))
                        allLetters = false;
                }
                if (partLen == 2 && allLetters)
                {
                    for (var i = 0; i < partLen; i++)
                    {
                        var upper = char.ToUpperInvariant(part[i]);
                        dest[pos + i] = upper;
                        if (upper != part[i])
                            changed = true;
                    }
                }
                else if (partLen == 4 && allLetters)
                {
                    var first = char.ToUpperInvariant(part[0]);
                    dest[pos] = first;
                    if (first != part[0])
                        changed = true;
                    for (var i = 1; i < partLen; i++)
                    {
                        var lower = char.ToLowerInvariant(part[i]);
                        dest[pos + i] = lower;
                        if (lower != part[i])
                            changed = true;
                    }
                }
                else
                {
                    for (var i = 0; i < partLen; i++)
                    {
                        var lower = char.ToLowerInvariant(part[i]);
                        dest[pos + i] = lower;
                        if (lower != part[i])
                            changed = true;
                    }
                }
            }
            partIndex++;
            if (dash < 0)
                break;
            dest[pos + partLen] = '-';
            pos += partLen + 1;
        }
        return changed ? new string(dest) : value;
    }
}

/// <summary>
/// A raw invitation token: <c>tfs1.</c> followed by 43 base64url characters encoding 256 random bits. Raw tokens exist only in
/// memory between issue and delivery and in the recipient's link; stores keep <see cref="SurveyTokenHash"/> only. Never log tokens.
/// </summary>
public readonly record struct SurveyToken
{
    public const string Prefix = "tfs1.";
    public const int Length = 48;

    private readonly string? value;

    private SurveyToken(string value) => this.value = value;

    /// <exception cref="InvalidOperationException">The token is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The SurveyToken is uninitialized.");

    public static SurveyToken Create()
    {
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        return FromBytes(random);
    }

    /// <summary>Minimum length of a derivation key in bytes.</summary>
    public const int MinimumKeyLength = 32;

    /// <summary>
    /// The deterministic token of one invitation: HMAC-SHA256 under an application-held secret <paramref name="key"/> (at least 32
    /// bytes) over the JSON array ["tf-survey-token-v1", tenant, project, environment, app, workspace, invitation]. Re-deriving on a retry
    /// yields the same token, so a link the contact already received stays valid, while the database alone (which keeps only the hash)
    /// cannot reproduce it.
    /// </summary>
    public static SurveyToken Derive(ReadOnlySpan<byte> key, TicketScope scope, SurveyInvitationId invitation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (key.Length < MinimumKeyLength)
            throw new ArgumentException($"Token keys must contain at least {MinimumKeyLength} bytes.", nameof(key));
        if (!invitation.IsInitialized)
            throw new ArgumentException("An invitation identifier is required.", nameof(invitation));
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("tf-survey-token-v1"u8);
            writer.WriteStringValue(scope.TenantId);
            writer.WriteStringValue(scope.ProjectId);
            writer.WriteStringValue(scope.EnvironmentId);
            writer.WriteStringValue(scope.AppId);
            writer.WriteStringValue(scope.WorkspaceId.Value);
            Span<char> guid = stackalloc char[36];
            invitation.Value.TryFormat(guid, out _, "D");
            writer.WriteStringValue(guid);
            writer.WriteEndArray();
        }
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, buffer.WrittenSpan, mac);
        return FromBytes(mac);
    }

    /// <summary>Parses untrusted input; false for anything that is not a well-formed version-1 token.</summary>
    public static bool TryParse(string? text, out SurveyToken token)
    {
        token = default;
        if (text is null || text.Length != Length || !text.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        foreach (var character in text.AsSpan(Prefix.Length))
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
                return false;
        token = new SurveyToken(text);
        return true;
    }

    public SurveyTokenHash Hash() => SurveyTokenHash.Of(this);

    public override string ToString() => "SurveyToken { <redacted> }";

    private static SurveyToken FromBytes(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[Length];
        Prefix.AsSpan().CopyTo(chars);
        Base64Url.EncodeToChars(bytes, chars[Prefix.Length..]);
        return new(new string(chars));
    }
}

/// <summary>The SHA-256 digest of a <see cref="SurveyToken"/> (32 bytes), the only token form stores persist and look up.</summary>
public readonly record struct SurveyTokenHash
{
    private readonly string? hex;

    private SurveyTokenHash(string hex) => this.hex = hex;

    /// <summary>64 lowercase hex characters.</summary>
    /// <exception cref="InvalidOperationException">The hash is <c>default</c>.</exception>
    public string Hex => hex ?? throw new InvalidOperationException("The SurveyTokenHash is uninitialized.");

    public bool IsInitialized => hex is not null;

    public byte[] ToBytes() => Convert.FromHexString(Hex);

    public static SurveyTokenHash Of(SurveyToken token)
    {
        Span<byte> ascii = stackalloc byte[SurveyToken.Length];
        Encoding.ASCII.GetBytes(token.Value, ascii);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(ascii, digest);
        return new(Convert.ToHexStringLower(digest));
    }

    public static SurveyTokenHash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
            throw new FormatException("A token hash must be 32 bytes.");
        return new(Convert.ToHexStringLower(bytes));
    }

    public override string ToString() => "SurveyTokenHash { <redacted> }";
}
