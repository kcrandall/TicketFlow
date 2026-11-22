using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.HelpCenter.HelpCenterGuards;

namespace TicketFlow.HelpCenter;

/// <summary>
/// Default <see cref="IHelpCenterPublicReader"/> and <see cref="IHelpCenterSearch"/>. Anonymous paths resolve the portal first and
/// stop unless it is public and supports the locale; they read only the published read model, so drafts and archived articles are
/// never reachable. Rendered HTML always passes through <see cref="IArticleRenderer"/>.
/// </summary>
public sealed class HelpCenterPublicReader : IHelpCenterPublicReader, IHelpCenterSearch
{
    private readonly IHelpCenterStore store;
    private readonly IArticleFeedbackStore feedback;
    private readonly IArticleRenderer renderer;
    private readonly IAccessEvaluator evaluator;
    private readonly TimeProvider clock;

    public HelpCenterPublicReader(IHelpCenterStore store, IArticleFeedbackStore feedback, IArticleRenderer renderer, IAccessEvaluator evaluator, TimeProvider clock)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<PublicPortal?> GetPortalAsync(TicketScope scope, HelpCenterSlug portal, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(scope, portal, null, cancellationToken).ConfigureAwait(false);
        return resolved is null ? null : new PublicPortal(resolved.Id, resolved.Slug, resolved.Name, resolved.DefaultLocale, resolved.SupportedLocales);
    }

    public async Task<IReadOnlyList<PublicCollection>> ListCollectionsAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, CancellationToken cancellationToken = default)
    {
        if (await ResolveAsync(scope, portal, locale, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return [];
        var collections = await store.ListCollectionsAsync(scope, resolved.Id, cancellationToken).ConfigureAwait(false);
        var result = new List<PublicCollection>();
        foreach (var stored in collections)
        {
            var collection = stored.Collection;
            if ((collection.Translation(locale) ?? collection.Translation(resolved.DefaultLocale)) is { } translation)
                result.Add(new PublicCollection(collection.Id, collection.Parent, collection.Position, translation.Locale, translation.Name, translation.Slug, translation.Description));
        }
        return result;
    }

    public async Task<PublicArticlePage> ListArticlesAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, CollectionId? collection = null, int limit = 50, PublishedArticleCursor? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, HelpCenterLimits.MaximumPageSize);
        if (await ResolveAsync(scope, portal, locale, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return new PublicArticlePage([], null);
        var page = await store.ListPublishedAsync(scope, new PublishedArticleQuery(resolved.Id, locale, collection, limit, after), cancellationToken).ConfigureAwait(false);
        return new PublicArticlePage([.. page.Items.Select(item => new PublicArticleSummary(item.Article, item.Collection, item.Position, item.Content.Title, item.Content.Slug, item.Content.Excerpt, item.PublishedAt))], page.Next);
    }

    public async Task<PublicArticle?> GetArticleAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, HelpCenterSlug article, CancellationToken cancellationToken = default)
    {
        if (!article.IsInitialized)
            throw new ArgumentException("An article slug is required.", nameof(article));
        if (await ResolveAsync(scope, portal, locale, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return null;
        var published = await store.GetPublishedAsync(scope, resolved.Id, locale, article, cancellationToken).ConfigureAwait(false);
        if (published is null)
            return null;
        var content = published.Content;
        return new PublicArticle(published.Article, published.Portal, published.Collection, content.Locale, content.Title, content.Slug, content.Excerpt, content.Tags,
            renderer.Render(content.Body).Html, published.RevisionNumber, published.PublishedAt);
    }

    public async Task<IReadOnlyList<ArticleSnippet>> SearchAsync(TicketScope scope, HelpCenterSlug portal, HelpCenterLocale locale, string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!TryQuery(query, limit, out var text, out var bounded) || await ResolveAsync(scope, portal, locale, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return [];
        return await store.SearchAsync(scope, new HelpCenterSearchQuery(resolved.Id, locale, text, bounded), cancellationToken).ConfigureAwait(false);
    }

    public Task<ArticleFeedbackResult> RecordViewAsync(TicketScope scope, HelpCenterSlug portal, ArticleId article, string visitorKey, CancellationToken cancellationToken = default) =>
        RecordAsync(scope, portal, article, visitorKey, (visitor, token) => feedback.RecordViewAsync(scope, article, visitor, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), token), cancellationToken);

    public Task<ArticleFeedbackResult> RecordFeedbackAsync(TicketScope scope, HelpCenterSlug portal, ArticleId article, string visitorKey, bool helpful, CancellationToken cancellationToken = default) =>
        RecordAsync(scope, portal, article, visitorKey, (visitor, token) => feedback.RecordVoteAsync(scope, article, visitor, helpful, token), cancellationToken);

    public async Task<HelpCenterResult<IReadOnlyList<ArticleSnippet>>> SearchAsync(PrincipalRef actor, HelpCenterSearchQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Portal(actor.Scope, query.Portal), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (await store.GetPortalAsync(actor.Scope, query.Portal, cancellationToken).ConfigureAwait(false) is null)
            return NotFound;
        return new HelpCenterSucceeded<IReadOnlyList<ArticleSnippet>>(await store.SearchAsync(actor.Scope, query, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ArticleSnippet>> SearchPublicAsync(TicketScope scope, HelpCenterSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        var portal = await store.GetPortalAsync(scope, query.Portal, cancellationToken).ConfigureAwait(false);
        if (portal is null || portal.Portal.Visibility != PortalVisibility.Public || !portal.Portal.Supports(query.Locale))
            return [];
        return await store.SearchAsync(scope, query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ArticleFeedbackResult> RecordAsync(TicketScope scope, HelpCenterSlug portal, ArticleId article, string visitorKey, Func<VisitorHash, CancellationToken, Task<ArticleFeedbackResult>> record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!article.IsInitialized)
            throw new ArgumentException("An article identifier is required.", nameof(article));
        var visitor = VisitorHash.Create(scope, visitorKey);
        if (await ResolveAsync(scope, portal, null, cancellationToken).ConfigureAwait(false) is not { } resolved)
            return FeedbackArticleNotFound.Instance;
        var stored = await store.GetArticleAsync(scope, article, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Record.State != ArticleState.Published || stored.Record.Article.Portal != resolved.Id)
            return FeedbackArticleNotFound.Instance;
        return await record(visitor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The portal when it exists, is public and (when given) supports <paramref name="locale"/>; otherwise null.</summary>
    private async Task<Portal?> ResolveAsync(TicketScope scope, HelpCenterSlug slug, HelpCenterLocale? locale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!slug.IsInitialized || locale is { IsInitialized: false })
            throw new ArgumentException("Portal slug and locale must be initialized.");
        var stored = await store.FindPortalBySlugAsync(scope, slug, cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.Portal.Visibility != PortalVisibility.Public || (locale is { } wanted && !stored.Portal.Supports(wanted)))
            return null;
        return stored.Portal;
    }

    /// <summary>Visitor queries that are blank, malformed or oversized yield no hits instead of throwing.</summary>
    private static bool TryQuery(string query, int limit, out string text, out int bounded)
    {
        text = query.Trim();
        bounded = Math.Clamp(limit, 1, HelpCenterSearchQuery.MaximumLimit);
        if (text.Length is 0 or > HelpCenterSearchQuery.MaximumQueryLength)
            return false;
        try
        {
            HelpCenterText.RequireLine(text, HelpCenterSearchQuery.MaximumQueryLength, nameof(query));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
