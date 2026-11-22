using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>The store write was applied; <see cref="Value"/> is the stored state (for deletes, the removed state).</summary>
public sealed record HelpCenterWriteApplied<T>(T Value);

/// <summary>No record exists for the key in the requested scope.</summary>
public sealed record HelpCenterWriteNotFound
{
    private HelpCenterWriteNotFound()
    {
    }

    public static HelpCenterWriteNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match, or a create found an existing record. Nothing was written.</summary>
public sealed record HelpCenterWriteConflict(long CurrentRevision);

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record HelpCenterWriteRejected(HelpCenterWriteRejection Reason);

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

/// <summary>Outcome of a revision-fenced store write. Switch over every case without a discard arm.</summary>
public union HelpCenterWriteResult<T>(HelpCenterWriteApplied<T>, HelpCenterWriteNotFound, HelpCenterWriteConflict, HelpCenterWriteRejected);

/// <summary>The authorized read or change succeeded.</summary>
public sealed record HelpCenterSucceeded<T>(T Value);

/// <summary>The request was refused. <see cref="Errors"/> are non-sensitive messages; <see cref="CurrentRevision"/> accompanies conflicts.</summary>
public sealed record HelpCenterRefused(HelpCenterRefusal Reason, IReadOnlyList<string> Errors, long? CurrentRevision = null);

public enum HelpCenterRefusal
{
    /// <summary>Access control denied the request. Checked before the store is consulted.</summary>
    Forbidden = 1,

    /// <summary>The input failed contextual validation (unsupported locale, missing default translation, ...).</summary>
    Invalid = 2,

    NotFound = 3,

    /// <summary>The expected revision did not match, or the record already exists.</summary>
    Conflict = 4,

    /// <summary>A store rule refused the write (see the error text).</summary>
    Rejected = 5,
}

/// <summary>Outcome of a help center service call. Switch over every case without a discard arm.</summary>
public union HelpCenterResult<T>(HelpCenterSucceeded<T>, HelpCenterRefused);

/// <summary>One keyset page. <see cref="HasMore"/> means a further page exists.</summary>
public sealed record HelpCenterPage<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>Agent article listing: one portal, optionally one collection and state, keyset-paged by identifier (ordinal).</summary>
public sealed record ArticleListQuery
{
    public ArticleListQuery(PortalId portal, CollectionId? collection = null, ArticleState? state = null, int limit = 50, ArticleId? after = null)
    {
        if (!portal.IsInitialized || collection is { IsInitialized: false } || after is { IsInitialized: false })
            throw new ArgumentException("Portal, collection and cursor identifiers must be initialized.");
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
            throw new ArgumentException("Portal, locale and collection must be initialized.");
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
