using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>Validated opaque identifier of a help center portal, unique within a workspace.</summary>
public readonly record struct PortalId : IComparable<PortalId>
{
    private readonly string? value;

    public PortalId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The PortalId is uninitialized.");

    public bool IsInitialized => value is not null;

    public int CompareTo(PortalId other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Validated opaque identifier of a help center collection, unique within a workspace.</summary>
public readonly record struct CollectionId : IComparable<CollectionId>
{
    private readonly string? value;

    public CollectionId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The CollectionId is uninitialized.");

    public bool IsInitialized => value is not null;

    public int CompareTo(CollectionId other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;
}

/// <summary>Validated opaque identifier of a help center article, unique within a workspace.</summary>
public readonly record struct ArticleId : IComparable<ArticleId>
{
    private readonly string? value;

    public ArticleId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The ArticleId is uninitialized.");

    public bool IsInitialized => value is not null;

    public int CompareTo(ArticleId other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// URL slug: 1-128 characters of lowercase ASCII letters and digits in hyphen-separated groups (<c>reset-your-password</c>).
/// <c>default</c> is uninitialized.
/// </summary>
public readonly record struct HelpCenterSlug : IComparable<HelpCenterSlug>
{
    public const int MaximumLength = 128;

    private readonly string? value;

    public HelpCenterSlug(string value)
    {
        if (!IsValid(value))
            throw new ArgumentException("Slugs must be 1-128 lowercase ASCII letters or digits in hyphen-separated groups.", nameof(value));
        this.value = value;
    }

    /// <exception cref="InvalidOperationException">The slug is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The HelpCenterSlug is uninitialized.");

    public bool IsInitialized => value is not null;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength || value[0] == '-' || value[^1] == '-' || value.Contains("--", StringComparison.Ordinal))
            return false;
        foreach (var character in value)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        return true;
    }

    public int CompareTo(HelpCenterSlug other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// A BCP 47 style locale tag of at most 35 characters: a 2-3 letter language followed by up to three 2-8 character alphanumeric
/// subtags (<c>en</c>, <c>pt-BR</c>, <c>zh-Hant-TW</c>). Canonicalized on construction: language lowercase, 4-letter script
/// title-case, 2-letter region uppercase, other subtags lowercase. <c>default</c> is uninitialized.
/// </summary>
public readonly record struct HelpCenterLocale : IComparable<HelpCenterLocale>
{
    public const int MaximumLength = 35;

    private readonly string? value;

    public HelpCenterLocale(string value) => this.value = Canonicalize(value) ?? throw new ArgumentException("Locales must be BCP 47 style tags such as 'en' or 'pt-BR'.", nameof(value));

    /// <exception cref="InvalidOperationException">The locale is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The HelpCenterLocale is uninitialized.");

    public bool IsInitialized => value is not null;

    /// <summary>The lowercase primary language subtag (<c>pt</c> for <c>pt-BR</c>).</summary>
    public string Language => Value.Split('-')[0];

    public static bool TryParse(string? value, out HelpCenterLocale locale)
    {
        var canonical = value is null ? null : Canonicalize(value);
        locale = canonical is null ? default : new HelpCenterLocale(canonical);
        return canonical is not null;
    }

    public int CompareTo(HelpCenterLocale other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;

    private static string? Canonicalize(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaximumLength)
            return null;
        var parts = value.Split('-');
        if (parts.Length > 4 || parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter))
            return null;
        var result = new string[parts.Length];
        result[0] = parts[0].ToLowerInvariant();
        for (var index = 1; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length is < 2 or > 8 || !part.All(char.IsAsciiLetterOrDigit))
                return null;
            result[index] = part.Length switch
            {
                2 when part.All(char.IsAsciiLetter) => part.ToUpperInvariant(),
                4 when part.All(char.IsAsciiLetter) => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant(),
                _ => part.ToLowerInvariant(),
            };
        }
        return string.Join('-', result);
    }
}
