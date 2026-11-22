using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>
/// Durable help center content. Contract:
/// <list type="bullet">
/// <item>Every operation is confined to one <see cref="TicketScope"/>; creates start at revision 1; updates and deletes are
/// compare-and-set on revision and distinguish not-found from conflict.</item>
/// <item>Portal slugs are unique per scope; at most <see cref="HelpCenterLimits.MaximumPortalsPerWorkspace"/> portals per scope.
/// A portal with collections or articles cannot be deleted (<see cref="HelpCenterWriteRejection.NotEmpty"/>).</item>
/// <item>Collections reference an existing portal and optional parent in the same portal; depth is the parent's depth + 1 and at
/// most <see cref="HelpCenterLimits.MaximumCollectionDepth"/>; portal and parent never change on update; at most
/// <see cref="HelpCenterLimits.MaximumCollectionsPerPortal"/> per portal; a collection with child collections or articles cannot
/// be deleted.</item>
/// <item>Articles reference an existing portal (fixed) and optional collection of that portal; at most
/// <see cref="HelpCenterLimits.MaximumArticlesPerPortal"/> per portal. <see cref="UpdateArticleAsync"/> may append exactly one
/// revision whose number is the article's current maximum + 1 (at most <see cref="HelpCenterLimits.MaximumRevisionsPerArticle"/>);
/// revisions are immutable.</item>
/// <item>In the same atomic write, the published read model is rebuilt for the article: when the record is
/// <see cref="ArticleState.Published"/>, one row per translation of revision <see cref="ArticleRecord.PublishedRevision"/> (which
/// must exist, else <see cref="HelpCenterWriteRejection.InvalidRevision"/>) with the record's current collection and position;
/// otherwise no rows. Published slugs are unique per (portal, locale) (<see cref="HelpCenterWriteRejection.SlugConflict"/>).</item>
/// <item>Deleting an article removes its revisions, read-model rows and feedback.</item>
/// <item>Search is ranked, bounded by <see cref="HelpCenterSearchQuery"/>, and reads only the published read model.</item>
/// </list>
/// </summary>
public interface IHelpCenterStore
{
    Task<StoredPortal?> GetPortalAsync(TicketScope scope, PortalId id, CancellationToken cancellationToken = default);

    Task<StoredPortal?> FindPortalBySlugAsync(TicketScope scope, HelpCenterSlug slug, CancellationToken cancellationToken = default);

    /// <summary>Every portal in the scope (bounded), ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredPortal>> ListPortalsAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredPortal>> CreatePortalAsync(TicketScope scope, Portal portal, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredPortal>> UpdatePortalAsync(TicketScope scope, Portal portal, long expectedRevision, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredPortal>> DeletePortalAsync(TicketScope scope, PortalId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<StoredCollection?> GetCollectionAsync(TicketScope scope, CollectionId id, CancellationToken cancellationToken = default);

    /// <summary>Every collection of the portal (bounded), ordered by (depth, position, identifier).</summary>
    Task<IReadOnlyList<StoredCollection>> ListCollectionsAsync(TicketScope scope, PortalId portal, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredCollection>> CreateCollectionAsync(TicketScope scope, HelpCollection collection, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredCollection>> UpdateCollectionAsync(TicketScope scope, HelpCollection collection, long expectedRevision, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredCollection>> DeleteCollectionAsync(TicketScope scope, CollectionId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<StoredArticle?> GetArticleAsync(TicketScope scope, ArticleId id, CancellationToken cancellationToken = default);

    Task<HelpCenterPage<StoredArticle>> ListArticlesAsync(TicketScope scope, ArticleListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Creates an article. A record that is <see cref="ArticleState.Published"/> or references a revision is rejected (<see cref="HelpCenterWriteRejection.InvalidRevision"/>).</summary>
    Task<HelpCenterWriteResult<StoredArticle>> CreateArticleAsync(TicketScope scope, ArticleRecord record, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredArticle>> UpdateArticleAsync(TicketScope scope, ArticleRecord record, long expectedRevision, ArticleRevision? appendRevision = null, CancellationToken cancellationToken = default);

    Task<HelpCenterWriteResult<StoredArticle>> DeleteArticleAsync(TicketScope scope, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<ArticleRevision?> GetRevisionAsync(TicketScope scope, ArticleId article, int number, CancellationToken cancellationToken = default);

    /// <summary>Revisions newest first, at most <paramref name="limit"/> (1-100), with numbers below <paramref name="before"/> when supplied.</summary>
    Task<HelpCenterPage<ArticleRevision>> ListRevisionsAsync(TicketScope scope, ArticleId article, int limit, int? before = null, CancellationToken cancellationToken = default);

    Task<PublishedArticle?> GetPublishedAsync(TicketScope scope, PortalId portal, HelpCenterLocale locale, HelpCenterSlug slug, CancellationToken cancellationToken = default);

    Task<PublishedArticle?> GetPublishedByIdAsync(TicketScope scope, ArticleId article, HelpCenterLocale locale, CancellationToken cancellationToken = default);

    Task<PublishedArticlePage> ListPublishedAsync(TicketScope scope, PublishedArticleQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArticleSnippet>> SearchAsync(TicketScope scope, HelpCenterSearchQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Abuse-resistant article counters. Contract: one view per (article, visitor hash, UTC day) and one vote per (article, visitor
/// hash) — repeating a vote is <see cref="FeedbackUnchanged"/>, changing it moves the count; counters change atomically with the
/// dedupe record; missing articles are <see cref="FeedbackArticleNotFound"/>. View markers older than a day can be purged.
/// </summary>
public interface IArticleFeedbackStore
{
    Task<ArticleFeedbackCounts?> GetCountsAsync(TicketScope scope, ArticleId article, CancellationToken cancellationToken = default);

    Task<ArticleFeedbackResult> RecordViewAsync(TicketScope scope, ArticleId article, VisitorHash visitor, DateOnly day, CancellationToken cancellationToken = default);

    Task<ArticleFeedbackResult> RecordVoteAsync(TicketScope scope, ArticleId article, VisitorHash visitor, bool helpful, CancellationToken cancellationToken = default);

    /// <summary>Removes at most <paramref name="limit"/> (1-10000) view markers of days before <paramref name="before"/>; returns the number removed.</summary>
    Task<int> PurgeViewMarkersAsync(TicketScope scope, DateOnly before, int limit, CancellationToken cancellationToken = default);
}
