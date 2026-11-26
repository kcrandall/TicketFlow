using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

public enum HelpCenterWriteRejection
{
    /// <summary>A capacity in <see cref="HelpCenterLimits"/> would be exceeded.</summary>
    CapacityExceeded = 1,

    /// <summary>The portal slug is taken in the workspace, or a published article slug is taken in the portal and locale.</summary>
    SlugConflict = 2,

    /// <summary>A referenced portal, parent collection or collection does not exist, belongs to another portal, or a fixed reference changed.</summary>
    InvalidReference = 3,

    /// <summary>The collection would be nested deeper than <see cref="HelpCenterLimits.MaximumCollectionDepth"/>.</summary>
    DepthExceeded = 4,

    /// <summary>A portal or collection still contains collections or articles.</summary>
    NotEmpty = 5,

    /// <summary>An appended revision is not the next number, or a published record references a missing revision.</summary>
    InvalidRevision = 6,
}

/// <summary>One keyset page. <see cref="HasMore"/> means a further page exists.</summary>
public sealed record HelpCenterPage<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>Agent article listing: one portal, optionally one collection and state, keyset-paged by identifier (ordinal).</summary>
public sealed record ArticleListQuery
{
    public ArticleListQuery(PortalId portal, CollectionId? collection = null, ArticleState? state = null, int limit = 50, ArticleId? after = null)
    {
        if (!portal.IsInitialized || collection is { IsInitialized: false } || after is { IsInitialized: false })
            throw new ArgumentException("Portal, collection and cursor identifiers must be initialized.", !portal.IsInitialized ? nameof(portal) : collection is { IsInitialized: false } ? nameof(collection) : nameof(after));
        if (state is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, HelpCenterLimits.MaximumPageSize);
        Portal = portal;
        Collection = collection;
        State = state;
        Limit = limit;
        After = after;
    }

    public PortalId Portal { get; }
    public CollectionId? Collection { get; }
    public ArticleState? State { get; }
    public int Limit { get; }
    public ArticleId? After { get; }
}

/// <summary>Keyset cursor over published articles ordered by (position, article identifier).</summary>
public sealed record PublishedArticleCursor
{
    public PublishedArticleCursor(int position, ArticleId article)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        if (!article.IsInitialized)
            throw new ArgumentException("An article identifier is required.", nameof(article));
        Position = position;
        Article = article;
    }

    public int Position { get; }
    public ArticleId Article { get; }
}

/// <summary>Published listing within one portal and locale, optionally one collection, ordered by (position, identifier).</summary>
public sealed record PublishedArticleQuery
{
    public PublishedArticleQuery(PortalId portal, HelpCenterLocale locale, CollectionId? collection = null, int limit = 50, PublishedArticleCursor? after = null)
    {
        if (!portal.IsInitialized || !locale.IsInitialized || collection is { IsInitialized: false })
            throw new ArgumentException("Portal, locale and collection must be initialized.", !portal.IsInitialized ? nameof(portal) : !locale.IsInitialized ? nameof(locale) : nameof(collection));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, HelpCenterLimits.MaximumPageSize);
        Portal = portal;
        Locale = locale;
        Collection = collection;
        Limit = limit;
        After = after;
    }

    public PortalId Portal { get; }
    public HelpCenterLocale Locale { get; }
    public CollectionId? Collection { get; }
    public int Limit { get; }
    public PublishedArticleCursor? After { get; }
}

/// <summary>A page of published articles and the cursor of the next page, if any.</summary>
public sealed record PublishedArticlePage(IReadOnlyList<PublishedArticle> Items, PublishedArticleCursor? Next);
