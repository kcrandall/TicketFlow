using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>A collection's name, slug and optional description in one locale.</summary>
public sealed record CollectionTranslation
{
    public const int MaximumNameLength = 128;
    public const int MaximumDescriptionLength = 1024;

    public CollectionTranslation(HelpCenterLocale locale, string name, HelpCenterSlug slug, string? description = null)
    {
        if (!locale.IsInitialized || !slug.IsInitialized)
            throw new ArgumentException("Translation locale and slug are required.", !locale.IsInitialized ? nameof(locale) : nameof(slug));
        Locale = locale;
        Name = HelpCenterText.RequireLine(name, MaximumNameLength, nameof(name));
        Slug = slug;
        Description = description is null ? null : HelpCenterText.RequireMultiline(description, MaximumDescriptionLength, nameof(description));
    }

    public HelpCenterLocale Locale { get; }
    public string Name { get; }
    public HelpCenterSlug Slug { get; }
    public string? Description { get; }
}

/// <summary>
/// An ordered collection of articles inside a portal. Collections nest up to <see cref="HelpCenterLimits.MaximumCollectionDepth"/>
/// levels; the parent is fixed at creation, so cycles cannot form. Siblings are ordered by (<see cref="Position"/>, identifier).
/// </summary>
public sealed record HelpCollection
{
    public HelpCollection(CollectionId id, PortalId portal, IEnumerable<CollectionTranslation> translations, CollectionId? parent = null, int position = 0)
    {
        if (!id.IsInitialized || !portal.IsInitialized || parent is { IsInitialized: false })
            throw new ArgumentException("Collection, portal and parent identifiers must be initialized.", !id.IsInitialized ? nameof(id) : !portal.IsInitialized ? nameof(portal) : nameof(parent));
        if (parent == id)
            throw new ArgumentException("A collection cannot be its own parent.", nameof(parent));
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, HelpCenterLimits.MaximumPosition);
        Id = id;
        Portal = portal;
        Parent = parent;
        Position = position;
        Translations = HelpCenterTranslations.Snapshot(translations, translation => translation.Locale, nameof(translations));
    }

    public CollectionId Id { get; }
    public PortalId Portal { get; }
    public CollectionId? Parent { get; }
    public int Position { get; }

    /// <summary>One translation per locale (at least one), ordered by locale.</summary>
    public IReadOnlyList<CollectionTranslation> Translations { get; }

    public CollectionTranslation? Translation(HelpCenterLocale locale) => Translations.FirstOrDefault(translation => translation.Locale == locale);

    public bool Equals(HelpCollection? other) =>
        other is not null && Id == other.Id && Portal == other.Portal && Parent == other.Parent && Position == other.Position
        && Translations.SequenceEqual(other.Translations);

    public override int GetHashCode() => HashCode.Combine(Id, Portal, Parent, Position, Translations.Count);
}

/// <summary>A collection with its scope, depth (1 for a root collection) and revision.</summary>
public sealed record StoredCollection(TicketScope Scope, HelpCollection Collection, int Depth, long Revision);

internal static class HelpCenterTranslations
{
    public static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> translations, Func<T, HelpCenterLocale> locale, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(translations, parameterName);
        var byLocale = new SortedDictionary<HelpCenterLocale, T>();
        foreach (var translation in translations)
        {
            ArgumentNullException.ThrowIfNull(translation, parameterName);
            if (!byLocale.TryAdd(locale(translation), translation))
                throw new ArgumentException("Translations must have distinct locales.", parameterName);
            if (byLocale.Count > Portal.MaximumLocales)
                throw new ArgumentException($"At most {Portal.MaximumLocales} translations are allowed.", parameterName);
        }
        if (byLocale.Count == 0)
            throw new ArgumentException("At least one translation is required.", parameterName);
        return [.. byLocale.Values];
    }
}
