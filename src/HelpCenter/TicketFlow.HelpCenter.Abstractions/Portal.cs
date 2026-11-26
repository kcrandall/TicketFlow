using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

public enum PortalVisibility
{
    /// <summary>Only agents with <c>helpcenter:read</c> can read the portal; anonymous visitors see nothing.</summary>
    Private = 1,

    /// <summary>Anonymous visitors can read published articles.</summary>
    Public = 2,
}

/// <summary>
/// A help center portal within a workspace: slug (unique per workspace), display name, default and supported locales, visibility and
/// an optional custom domain reference. The domain is a host name the application routes to the portal; TicketFlow neither verifies
/// nor serves it.
/// </summary>
public sealed record Portal
{
    public const int MaximumNameLength = 128;
    public const int MaximumLocales = 32;
    public const int MaximumDomainLength = 253;

    public Portal(
        PortalId id,
        HelpCenterSlug slug,
        string name,
        HelpCenterLocale defaultLocale,
        IEnumerable<HelpCenterLocale>? supportedLocales = null,
        PortalVisibility visibility = PortalVisibility.Private,
        string? customDomain = null)
    {
        if (!id.IsInitialized || !slug.IsInitialized || !defaultLocale.IsInitialized)
            throw new ArgumentException("Portal identifier, slug and default locale are required.", !id.IsInitialized ? nameof(id) : !slug.IsInitialized ? nameof(slug) : nameof(defaultLocale));
        if (!Enum.IsDefined(visibility))
            throw new ArgumentOutOfRangeException(nameof(visibility));
        Id = id;
        Slug = slug;
        Name = HelpCenterText.RequireLine(name, MaximumNameLength, nameof(name));
        DefaultLocale = defaultLocale;
        var locales = new SortedSet<HelpCenterLocale> { defaultLocale };
        foreach (var locale in supportedLocales ?? [])
        {
            if (!locale.IsInitialized)
                throw new ArgumentException("Supported locales must be initialized.", nameof(supportedLocales));
            if (locales.Add(locale) && locales.Count > MaximumLocales)
                throw new ArgumentException($"A portal supports at most {MaximumLocales} locales.", nameof(supportedLocales));
        }
        SupportedLocales = [.. locales];
        Visibility = visibility;
        CustomDomain = customDomain is null ? null : RequireDomain(customDomain);
    }

    public PortalId Id { get; }
    public HelpCenterSlug Slug { get; }
    public string Name { get; }
    public HelpCenterLocale DefaultLocale { get; }

    /// <summary>Supported locales ordered ordinally; always contains <see cref="DefaultLocale"/>.</summary>
    public IReadOnlyList<HelpCenterLocale> SupportedLocales { get; }

    public PortalVisibility Visibility { get; }

    /// <summary>Lowercase host name reference such as <c>help.example.com</c>, or <see langword="null"/>.</summary>
    public string? CustomDomain { get; }

    public bool Supports(HelpCenterLocale locale) => locale.IsInitialized && SupportedLocales.Contains(locale);

    public bool Equals(Portal? other) =>
        other is not null && Id == other.Id && Slug == other.Slug && string.Equals(Name, other.Name, StringComparison.Ordinal) && DefaultLocale == other.DefaultLocale
        && SupportedLocales.SequenceEqual(other.SupportedLocales) && Visibility == other.Visibility && string.Equals(CustomDomain, other.CustomDomain, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Id, Slug, Name, DefaultLocale, SupportedLocales.Count, Visibility, CustomDomain);

    /// <summary>Validates a lowercase DNS host name with at least two labels of 1-63 characters (a-z, 0-9, inner '-').</summary>
    public static string RequireDomain(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var labels = value.Split('.');
        var valid = value.Length <= MaximumDomainLength && labels.Length >= 2 && labels.All(label =>
            label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-' && label.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'));
        if (!valid)
            throw new ArgumentException("Custom domains must be lowercase host names such as 'help.example.com'.", nameof(value));
        return value;
    }
}

/// <summary>A portal with its scope and revision (first revision is 1).</summary>
public sealed record StoredPortal(TicketScope Scope, Portal Portal, long Revision);
