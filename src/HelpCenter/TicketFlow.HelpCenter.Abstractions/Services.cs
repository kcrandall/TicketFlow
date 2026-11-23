using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>Sanitized HTML produced from article Markdown. Safe to embed in a page as-is.</summary>
public sealed record RenderedMarkdown(string Html)
{
    public override string ToString() => "RenderedMarkdown { <redacted> }";
}

/// <summary>
/// Converts article Markdown into allow-listed HTML and plain text. Implementations must never pass raw HTML through and must
/// restrict URLs to safe schemes.
/// </summary>
public interface IArticleRenderer
{
    RenderedMarkdown Render(string markdown);

    /// <summary>Markdown-stripped text for search indexing and snippets.</summary>
    string ToPlainText(string markdown);
}

/// <summary>An article draft rendered for preview in one locale.</summary>
public sealed record ArticlePreview(ArticleId Article, HelpCenterLocale Locale, string Title, string Html);

/// <summary>
/// Authorized help center administration in the actor's workspace (<see cref="PrincipalRef.Scope"/>). See
/// <see cref="HelpCenterPermissions"/> for the permission each operation needs. Translations must use the portal's supported
/// locales; publishing requires a translation in the portal's default locale.
/// </summary>
public interface IHelpCenterService
{
    Task<TicketResult<StoredPortal>> GetPortalAsync(PrincipalRef actor, PortalId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<StoredPortal>>> ListPortalsAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredPortal>> CreatePortalAsync(PrincipalRef actor, Portal portal, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredPortal>> UpdatePortalAsync(PrincipalRef actor, Portal portal, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredPortal>> DeletePortalAsync(PrincipalRef actor, PortalId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCollection>> GetCollectionAsync(PrincipalRef actor, CollectionId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<StoredCollection>>> ListCollectionsAsync(PrincipalRef actor, PortalId portal, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCollection>> CreateCollectionAsync(PrincipalRef actor, HelpCollection collection, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCollection>> UpdateCollectionAsync(PrincipalRef actor, HelpCollection collection, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCollection>> DeleteCollectionAsync(PrincipalRef actor, CollectionId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredArticle>> GetArticleAsync(PrincipalRef actor, ArticleId id, CancellationToken cancellationToken = default);

    Task<TicketResult<HelpCenterPage<StoredArticle>>> ListArticlesAsync(PrincipalRef actor, ArticleListQuery query, CancellationToken cancellationToken = default);

    /// <summary>Creates a draft article authored by <paramref name="actor"/>.</summary>
    Task<TicketResult<StoredArticle>> CreateArticleAsync(PrincipalRef actor, Article article, CancellationToken cancellationToken = default);

    /// <summary>Replaces the draft content, collection and position. The live revision of a published article is unchanged.</summary>
    Task<TicketResult<StoredArticle>> UpdateArticleAsync(PrincipalRef actor, Article article, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Snapshots the draft into a new immutable revision and serves it.</summary>
    Task<TicketResult<StoredArticle>> PublishArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Stops serving the article (state Draft); its revisions are kept.</summary>
    Task<TicketResult<StoredArticle>> UnpublishArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredArticle>> ArchiveArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Moves an archived article back to Draft.</summary>
    Task<TicketResult<StoredArticle>> RestoreArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Republishes the content of revision <paramref name="revision"/> as a new revision and resets the draft to it.</summary>
    Task<TicketResult<StoredArticle>> RollbackArticleAsync(PrincipalRef actor, ArticleId id, int revision, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredArticle>> DeleteArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<ArticleRevision>> GetRevisionAsync(PrincipalRef actor, ArticleId id, int revision, CancellationToken cancellationToken = default);

    Task<TicketResult<HelpCenterPage<ArticleRevision>>> ListRevisionsAsync(PrincipalRef actor, ArticleId id, int limit = 20, int? before = null, CancellationToken cancellationToken = default);

    /// <summary>Renders the draft translation in <paramref name="locale"/> to sanitized HTML.</summary>
    Task<TicketResult<ArticlePreview>> PreviewArticleAsync(PrincipalRef actor, ArticleId id, HelpCenterLocale locale, CancellationToken cancellationToken = default);

    Task<TicketResult<ArticleFeedbackCounts>> GetFeedbackAsync(PrincipalRef actor, ArticleId id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Help center search for widget deflection (E7) and assistant grounding (E13). Searches only published content.
/// </summary>
public interface IHelpCenterSearch
{
    /// <summary>Agent or assistant search in any portal of the actor's workspace; requires <c>helpcenter:read</c>.</summary>
    Task<TicketResult<IReadOnlyList<ArticleSnippet>>> SearchAsync(PrincipalRef actor, HelpCenterSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Anonymous search; returns no hits unless the portal is public and supports the locale.</summary>
    Task<IReadOnlyList<ArticleSnippet>> SearchPublicAsync(TicketScope scope, HelpCenterSearchQuery query, CancellationToken cancellationToken = default);
}

/// <summary>A public portal as visitors see it.</summary>
public sealed record PublicPortal(PortalId Id, HelpCenterSlug Slug, string Name, HelpCenterLocale DefaultLocale, IReadOnlyList<HelpCenterLocale> SupportedLocales);

/// <summary>A collection localized for visitors (falls back to the portal's default locale).</summary>
public sealed record PublicCollection(CollectionId Id, CollectionId? Parent, int Position, HelpCenterLocale Locale, string Name, HelpCenterSlug Slug, string? Description);

/// <summary>A published article for visitors, with sanitized HTML of the live revision.</summary>
public sealed record PublicArticle(
    ArticleId Id,
    PortalId Portal,
    CollectionId? Collection,
    HelpCenterLocale Locale,
    string Title,
    HelpCenterSlug Slug,
    string? Excerpt,
    IReadOnlyList<string> Tags,
    string Html,
    int RevisionNumber,
    DateTimeOffset PublishedAt)
{
    public override string ToString() => "PublicArticle { <redacted> }";
}

/// <summary>A published article summary for listings.</summary>
public sealed record PublicArticleSummary(ArticleId Id, CollectionId? Collection, int Position, string Title, HelpCenterSlug Slug, string? Excerpt, DateTimeOffset PublishedAt);

public sealed record PublicArticlePage(IReadOnlyList<PublicArticleSummary> Items, PublishedArticleCursor? Next);

/// <summary>
/// Anonymous visitor reads within a host-resolved <see cref="TicketScope"/>. Only published articles of public portals in supported
/// locales are visible; private portals, unsupported locales and unpublished articles are indistinguishable from missing
/// (<see langword="null"/> or empty). Visitor keys are hashed (<see cref="VisitorHash"/>) before reaching a store.
/// </summary>
public interface IHelpCenterPublicReader
{
    Task<PublicPortal?> GetPortalAsync(TicketScope scope, HelpCenterSlug portal, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicCollection>> ListCollectionsAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, CancellationToken cancellationToken = default);

    Task<PublicArticlePage> ListArticlesAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, CollectionId? collection = null, int limit = 50, PublishedArticleCursor? after = null, CancellationToken cancellationToken = default);

    Task<PublicArticle?> GetArticleAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, HelpCenterSlug article, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ArticleSnippet>> SearchAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, string query, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>Counts one view per visitor per UTC day.</summary>
    Task<ArticleFeedbackResult> RecordViewAsync(TicketScope scope, HelpCenterSlug portal, ArticleId article, string visitorKey, CancellationToken cancellationToken = default);

    /// <summary>Records the visitor's helpful / not helpful vote; repeating it is idempotent, changing it moves the count.</summary>
    Task<ArticleFeedbackResult> RecordFeedbackAsync(TicketScope scope, HelpCenterSlug portal, ArticleId article, string visitorKey, bool helpful, CancellationToken cancellationToken = default);
}
