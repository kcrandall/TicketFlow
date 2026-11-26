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

    public static bool operator <(PortalId left, PortalId right) => left.CompareTo(right) < 0;
    public static bool operator <=(PortalId left, PortalId right) => left.CompareTo(right) <= 0;
    public static bool operator >(PortalId left, PortalId right) => left.CompareTo(right) > 0;
    public static bool operator >=(PortalId left, PortalId right) => left.CompareTo(right) >= 0;

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

    public static bool operator <(CollectionId left, CollectionId right) => left.CompareTo(right) < 0;
    public static bool operator <=(CollectionId left, CollectionId right) => left.CompareTo(right) <= 0;
    public static bool operator >(CollectionId left, CollectionId right) => left.CompareTo(right) > 0;
    public static bool operator >=(CollectionId left, CollectionId right) => left.CompareTo(right) >= 0;

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

    public static bool operator <(ArticleId left, ArticleId right) => left.CompareTo(right) < 0;
    public static bool operator <=(ArticleId left, ArticleId right) => left.CompareTo(right) <= 0;
    public static bool operator >(ArticleId left, ArticleId right) => left.CompareTo(right) > 0;
    public static bool operator >=(ArticleId left, ArticleId right) => left.CompareTo(right) >= 0;

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

    public static bool operator <(HelpCenterSlug left, HelpCenterSlug right) => left.CompareTo(right) < 0;
    public static bool operator <=(HelpCenterSlug left, HelpCenterSlug right) => left.CompareTo(right) <= 0;
    public static bool operator >(HelpCenterSlug left, HelpCenterSlug right) => left.CompareTo(right) > 0;
    public static bool operator >=(HelpCenterSlug left, HelpCenterSlug right) => left.CompareTo(right) >= 0;

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
    public string Language
    {
        get
        {
            var v = Value;
            var dash = v.IndexOf('-');
            return dash < 0 ? v : v[..dash];
        }
    }

    public static bool TryParse(string? value, out HelpCenterLocale locale)
    {
        var canonical = value is null ? null : Canonicalize(value);
        locale = canonical is null ? default : new HelpCenterLocale(canonical, true);
        return canonical is not null;
    }

    private HelpCenterLocale(string canonical, bool _) => value = canonical;

    public int CompareTo(HelpCenterLocale other) => string.CompareOrdinal(value, other.value);

    public static bool operator <(HelpCenterLocale left, HelpCenterLocale right) => left.CompareTo(right) < 0;
    public static bool operator <=(HelpCenterLocale left, HelpCenterLocale right) => left.CompareTo(right) <= 0;
    public static bool operator >(HelpCenterLocale left, HelpCenterLocale right) => left.CompareTo(right) > 0;
    public static bool operator >=(HelpCenterLocale left, HelpCenterLocale right) => left.CompareTo(right) >= 0;

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
