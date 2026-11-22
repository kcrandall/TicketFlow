using System.Globalization;
using System.Text;
using TicketFlow.Access;

namespace TicketFlow.Contacts;

/// <summary>Channel kinds of contact identity. Values are persisted; never renumber.</summary>
public enum ContactIdentityKind
{
    /// <summary>Email address; domain IDN-encoded, whole address lowercased.</summary>
    Email = 1,

    /// <summary>E.164 phone number.</summary>
    Phone = 2,

    /// <summary>WhatsApp number (E.164; bare international digits accepted).</summary>
    WhatsApp = 3,

    /// <summary>Opaque live-chat widget visitor identifier; optional namespace (for example a widget or inbox key).</summary>
    WidgetVisitor = 4,

    /// <summary>Opaque application user identifier (for example an IdentityFlow subject); namespace (issuer) required.</summary>
    ExternalUser = 5,

    /// <summary>Social handle; namespace (network such as <c>instagram</c>) required; leading '@' removed, lowercased.</summary>
    SocialHandle = 6,
}

public enum ContactAddressError
{
    UnknownKind = 1,
    Empty = 2,
    TooLong = 3,
    InvalidCharacters = 4,
    InvalidEmail = 5,
    InvalidPhone = 6,
    NamespaceRequired = 7,
    NamespaceNotAllowed = 8,
    InvalidNamespace = 9,
    InvalidCallingCode = 10,
}

/// <summary>The raw address could not be normalized. Carries no part of the input.</summary>
public sealed record ContactAddressInvalid(ContactAddressError Error);

/// <summary>Outcome of normalizing a raw channel address. Switch over every case without a discard arm.</summary>
public union ContactAddressNormalization(ContactIdentityAddress, ContactAddressInvalid);

/// <summary>
/// A normalized channel address: correlation evidence, never authentication. Equality is ordinal over
/// (<see cref="Kind"/>, <see cref="Namespace"/>, <see cref="Value"/>). <see cref="ToString"/> redacts the value.
/// </summary>
public sealed record ContactIdentityAddress
{
    public const int MaximumValueLength = 256;
    public const int MaximumNamespaceLength = 64;
    public const int MaximumEmailLength = 254;
    public const int MaximumRawPhoneLength = 64;
    public const int MinimumPhoneDigits = 7;
    public const int MaximumPhoneDigits = 15;

    private ContactIdentityAddress(ContactIdentityKind kind, string @namespace, string value)
    {
        Kind = kind;
        Namespace = @namespace;
        Value = value;
    }

    public ContactIdentityKind Kind { get; }

    /// <summary>Lowercase token or empty when the kind has none.</summary>
    public string Namespace { get; }

    /// <summary>The normalized value. Personal data: never log it.</summary>
    public string Value { get; }

    /// <summary>The Access field path that masks this address.</summary>
    public FieldPath Field => ContactFieldPaths.ForIdentity(Kind);

    public override string ToString() => $"ContactIdentityAddress {{ Kind = {Kind}, <redacted> }}";

    /// <summary>Normalizes and throws <see cref="ArgumentException"/> (without the value) when invalid.</summary>
    public static ContactIdentityAddress Parse(ContactIdentityKind kind, string value, string? @namespace = null, string? defaultCallingCode = null) =>
        Normalize(kind, value, @namespace, defaultCallingCode) switch
        {
            ContactIdentityAddress address => address,
            ContactAddressInvalid invalid => throw new ArgumentException($"The {kind} address is invalid: {invalid.Error}.", nameof(value)),
        };

    /// <summary>
    /// Normalizes <paramref name="value"/> for <paramref name="kind"/>. <paramref name="defaultCallingCode"/> (1-3 digits) lets phone
    /// numbers without '+'/'00' be read as national numbers, dropping one leading trunk '0'. Normalization is idempotent.
    /// </summary>
    public static ContactAddressNormalization Normalize(ContactIdentityKind kind, string value, string? @namespace = null, string? defaultCallingCode = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(kind))
            return Invalid(ContactAddressError.UnknownKind);
        var ns = @namespace ?? string.Empty;
        var namespaceRule = kind switch
        {
            ContactIdentityKind.Email or ContactIdentityKind.Phone or ContactIdentityKind.WhatsApp => NamespaceRule.Forbidden,
            ContactIdentityKind.WidgetVisitor => NamespaceRule.Optional,
            _ => NamespaceRule.Required,
        };
        if (ns.Length == 0 && namespaceRule == NamespaceRule.Required)
            return Invalid(ContactAddressError.NamespaceRequired);
        if (ns.Length > 0 && namespaceRule == NamespaceRule.Forbidden)
            return Invalid(ContactAddressError.NamespaceNotAllowed);
        if (ns.Length > 0 && !ContactText.IsToken(ns, MaximumNamespaceLength))
            return Invalid(ContactAddressError.InvalidNamespace);
        if (value.Length > (kind is ContactIdentityKind.Phone or ContactIdentityKind.WhatsApp ? MaximumRawPhoneLength : MaximumValueLength + 2))
            return Invalid(ContactAddressError.TooLong);
        if (!ContactText.IsClean(value))
            return Invalid(ContactAddressError.InvalidCharacters);

        return kind switch
        {
            ContactIdentityKind.Email => NormalizeEmail(value),
            ContactIdentityKind.Phone => NormalizePhone(kind, value, defaultCallingCode, bareInternational: false),
            ContactIdentityKind.WhatsApp => NormalizePhone(kind, value, defaultCallingCode, bareInternational: true),
            ContactIdentityKind.WidgetVisitor or ContactIdentityKind.ExternalUser => NormalizeOpaque(kind, ns, value),
            _ => NormalizeHandle(ns, value),
        };
    }

    private enum NamespaceRule
    {
        Forbidden,
        Optional,
        Required,
    }

    private static ContactAddressInvalid Invalid(ContactAddressError error) => new(error);

    private static ContactAddressNormalization NormalizeOpaque(ContactIdentityKind kind, string ns, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Invalid(ContactAddressError.Empty);
        if (value.Length > MaximumValueLength)
            return Invalid(ContactAddressError.TooLong);
        return new ContactIdentityAddress(kind, ns, value);
    }

    private static ContactAddressNormalization NormalizeHandle(string ns, string value)
    {
        var handle = value.Trim();
        if (handle.StartsWith('@'))
            handle = handle[1..];
        if (handle.Length == 0)
            return Invalid(ContactAddressError.Empty);
        if (handle.Length > MaximumValueLength)
            return Invalid(ContactAddressError.TooLong);
        foreach (var character in handle)
            if (char.IsWhiteSpace(character) || character == '@')
                return Invalid(ContactAddressError.InvalidCharacters);
        return new ContactIdentityAddress(ContactIdentityKind.SocialHandle, ns, handle.ToLowerInvariant());
    }

    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    private static ContactAddressNormalization NormalizeEmail(string value)
    {
        var email = value.Trim();
        if (email.Length == 0)
            return Invalid(ContactAddressError.Empty);
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1)
            return Invalid(ContactAddressError.InvalidEmail);
        var local = email[..at];
        var domain = email[(at + 1)..].TrimEnd('.');
        if (local.Length > 64 || domain.Length == 0)
            return Invalid(ContactAddressError.InvalidEmail);
        foreach (var character in local)
            if (character <= ' ' || char.IsWhiteSpace(character) || character is '"' or '(' or ')' or ',' or ':' or ';' or '<' or '>' or '[' or ']' or '\\' or '@')
                return Invalid(ContactAddressError.InvalidEmail);
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
            return Invalid(ContactAddressError.InvalidEmail);

        string ascii;
        try
        {
            ascii = Idn.GetAscii(domain).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return Invalid(ContactAddressError.InvalidEmail);
        }
        if (ascii.Length > 253)
            return Invalid(ContactAddressError.InvalidEmail);
        var labels = ascii.Split('.');
        if (labels.Length < 2)
            return Invalid(ContactAddressError.InvalidEmail);
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
                return Invalid(ContactAddressError.InvalidEmail);
            foreach (var character in label)
                if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                    return Invalid(ContactAddressError.InvalidEmail);
        }
        var normalized = local.ToLowerInvariant() + "@" + ascii;
        if (normalized.Length > MaximumEmailLength)
            return Invalid(ContactAddressError.TooLong);
        return new ContactIdentityAddress(ContactIdentityKind.Email, string.Empty, normalized);
    }

    private static ContactAddressNormalization NormalizePhone(ContactIdentityKind kind, string value, string? defaultCallingCode, bool bareInternational)
    {
        if (defaultCallingCode is not null && (defaultCallingCode.Length is < 1 or > 3 || defaultCallingCode[0] is < '1' or > '9' || !defaultCallingCode.All(char.IsAsciiDigit)))
            return Invalid(ContactAddressError.InvalidCallingCode);
        var digits = new StringBuilder(MaximumRawPhoneLength);
        var plus = false;
        foreach (var character in value.Trim())
        {
            if (char.IsAsciiDigit(character))
                digits.Append(character);
            else if (character == '+' && digits.Length == 0 && !plus)
                plus = true;
            else if (character is not (' ' or '-' or '.' or '(' or ')' or '/'))
                return Invalid(ContactAddressError.InvalidPhone);
        }
        if (digits.Length == 0)
            return Invalid(plus ? ContactAddressError.InvalidPhone : ContactAddressError.Empty);

        string international;
        if (plus)
            international = digits.ToString();
        else if (digits.Length > 2 && digits[0] == '0' && digits[1] == '0')
            international = digits.ToString(2, digits.Length - 2);
        else if (bareInternational && defaultCallingCode is null)
            international = digits.ToString();
        else if (defaultCallingCode is not null)
            international = defaultCallingCode + (digits[0] == '0' ? digits.ToString(1, digits.Length - 1) : digits.ToString());
        else
            return Invalid(ContactAddressError.InvalidPhone);

        if (international.Length is < MinimumPhoneDigits or > MaximumPhoneDigits || international[0] == '0')
            return Invalid(ContactAddressError.InvalidPhone);
        return new ContactIdentityAddress(kind, string.Empty, "+" + international);
    }
}

/// <summary>The identity was verified by <see cref="Source"/> (for example <c>dkim</c>, <c>otp</c>, <c>identityflow</c>).</summary>
public sealed record IdentityVerified
{
    public IdentityVerified(string source, DateTimeOffset at)
    {
        Source = ContactText.RequireToken(source, IdentityVerification.MaximumSourceLength, nameof(source), "Verification sources");
        At = ContactText.Instant(at);
    }

    public string Source { get; }
    public DateTimeOffset At { get; }
}

/// <summary>The identity was claimed (asserted without proof) through <see cref="Source"/> (for example <c>widget-form</c>, <c>agent</c>).</summary>
public sealed record IdentityClaimed
{
    public IdentityClaimed(string source, DateTimeOffset at)
    {
        Source = ContactText.RequireToken(source, IdentityVerification.MaximumSourceLength, nameof(source), "Verification sources");
        At = ContactText.Instant(at);
    }

    public string Source { get; }
    public DateTimeOffset At { get; }
}

/// <summary>
/// How strongly an identity is tied to a contact. Verified identities are unique per scope, kind, namespace and value; claimed ones
/// may repeat across contacts. Neither authenticates the contact. Switch over every case without a discard arm.
/// </summary>
public union IdentityVerification(IdentityVerified, IdentityClaimed)
{
    public const int MaximumSourceLength = 64;

    public bool IsVerified => this switch
    {
        IdentityVerified => true,
        IdentityClaimed => false,
    };

    public string Source => this switch
    {
        IdentityVerified verified => verified.Source,
        IdentityClaimed claimed => claimed.Source,
    };

    public DateTimeOffset At => this switch
    {
        IdentityVerified verified => verified.At,
        IdentityClaimed claimed => claimed.At,
    };
}

/// <summary>One identity in a contact's graph.</summary>
public sealed record ContactIdentity
{
    public ContactIdentity(ContactIdentityAddress address, IdentityVerification verification)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (verification.Value is null)
            throw new ArgumentException("A verification is required.", nameof(verification));
        Address = address;
        Verification = verification;
    }

    public ContactIdentityAddress Address { get; }
    public IdentityVerification Verification { get; }

    public override string ToString() => $"ContactIdentity {{ Kind = {Address.Kind}, Verified = {Verification.IsVerified} }}";
}

/// <summary>Inbound channel evidence for resolve-or-create. Correlation evidence only, never authentication.</summary>
public sealed record ContactIdentityEvidence
{
    public ContactIdentityEvidence(ContactIdentityAddress address, IdentityVerification verification)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (verification.Value is null)
            throw new ArgumentException("A verification is required.", nameof(verification));
        Address = address;
        Verification = verification;
    }

    public ContactIdentityAddress Address { get; }
    public IdentityVerification Verification { get; }

    public ContactIdentity ToIdentity() => new(Address, Verification);

    public override string ToString() => $"ContactIdentityEvidence {{ Kind = {Address.Kind}, Verified = {Verification.IsVerified} }}";
}
