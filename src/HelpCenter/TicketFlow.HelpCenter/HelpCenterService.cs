using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.HelpCenter.HelpCenterGuards;

namespace TicketFlow.HelpCenter;

/// <summary>
/// Default <see cref="IHelpCenterService"/>. Every call authorizes through Access before touching the store (lists use the access
/// query planner). Lifecycle changes are revision-fenced on the loaded article and written with one store call, which appends the
/// revision and rebuilds the published read model atomically.
/// </summary>
public sealed class HelpCenterService : IHelpCenterService
{
    private readonly IHelpCenterStore store;
    private readonly IArticleFeedbackStore feedback;
    private readonly IArticleRenderer renderer;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;
    private readonly TimeProvider clock;

    public HelpCenterService(IHelpCenterStore store, IArticleFeedbackStore feedback, IArticleRenderer renderer, IAccessEvaluator evaluator, IAccessQueryPlanner planner, TimeProvider clock)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.feedback = feedback ?? throw new ArgumentNullException(nameof(feedback));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    // ---------------------------------------------------------------- portals

    public async Task<TicketResult<StoredPortal>> GetPortalAsync(PrincipalRef actor, PortalId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Portal(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await store.GetPortalAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } portal ? new TicketValue<StoredPortal>(portal) : NotFound;
    }

    public async Task<TicketResult<IReadOnlyList<StoredPortal>>> ListPortalsAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (!await CanListAsync(planner, actor, HelpCenterPermissions.Read, cancellationToken).ConfigureAwait(false))
            return new TicketValue<IReadOnlyList<StoredPortal>>([]);
        return new TicketValue<IReadOnlyList<StoredPortal>>(await store.ListPortalsAsync(actor.Scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredPortal>> CreatePortalAsync(PrincipalRef actor, Portal portal, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(portal);
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Manage, HelpCenterResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return Map(await store.CreatePortalAsync(actor.Scope, portal, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredPortal>> UpdatePortalAsync(PrincipalRef actor, Portal portal, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(portal);
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Manage, HelpCenterResources.Portal(actor.Scope, portal.Id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        return Map(await store.UpdatePortalAsync(actor.Scope, portal, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredPortal>> DeletePortalAsync(PrincipalRef actor, PortalId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Manage, HelpCenterResources.Portal(actor.Scope, id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        return Map(await store.DeletePortalAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- collections

    public async Task<TicketResult<StoredCollection>> GetCollectionAsync(PrincipalRef actor, CollectionId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Collection(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await store.GetCollectionAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } collection ? new TicketValue<StoredCollection>(collection) : NotFound;
    }

    public async Task<TicketResult<IReadOnlyList<StoredCollection>>> ListCollectionsAsync(PrincipalRef actor, PortalId portal, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(portal.IsInitialized, nameof(portal));
        if (!await CanListAsync(planner, actor, HelpCenterPermissions.Read, cancellationToken).ConfigureAwait(false))
            return new TicketValue<IReadOnlyList<StoredCollection>>([]);
        return new TicketValue<IReadOnlyList<StoredCollection>>(await store.ListCollectionsAsync(actor.Scope, portal, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredCollection>> CreateCollectionAsync(PrincipalRef actor, HelpCollection collection, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(collection);
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Write, HelpCenterResources.Collection(actor.Scope, collection.Id), cancellationToken).ConfigureAwait(false)
            ?? await ValidateCollectionAsync(actor.Scope, collection, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        return Map(await store.CreateCollectionAsync(actor.Scope, collection, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredCollection>> UpdateCollectionAsync(PrincipalRef actor, HelpCollection collection, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(collection);
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Write, HelpCenterResources.Collection(actor.Scope, collection.Id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision)
            ?? await ValidateCollectionAsync(actor.Scope, collection, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        return Map(await store.UpdateCollectionAsync(actor.Scope, collection, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredCollection>> DeleteCollectionAsync(PrincipalRef actor, CollectionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Write, HelpCenterResources.Collection(actor.Scope, id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        return Map(await store.DeleteCollectionAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    // ---------------------------------------------------------------- articles

    public async Task<TicketResult<StoredArticle>> GetArticleAsync(PrincipalRef actor, ArticleId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Article(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await store.GetArticleAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } article ? new TicketValue<StoredArticle>(article) : NotFound;
    }

    public async Task<TicketResult<HelpCenterPage<StoredArticle>>> ListArticlesAsync(PrincipalRef actor, ArticleListQuery query, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        if (!await CanListAsync(planner, actor, HelpCenterPermissions.Read, cancellationToken).ConfigureAwait(false))
            return new TicketValue<HelpCenterPage<StoredArticle>>(new([], false));
        return new TicketValue<HelpCenterPage<StoredArticle>>(await store.ListArticlesAsync(actor.Scope, query, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredArticle>> CreateArticleAsync(PrincipalRef actor, Article article, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(article);
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Write, HelpCenterResources.Article(actor.Scope, article.Id), cancellationToken).ConfigureAwait(false)
            ?? await ValidateArticleAsync(actor.Scope, article, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        var who = AccessActor.From(actor);
        var now = clock.GetUtcNow();
        return Map(await store.CreateArticleAsync(actor.Scope, new ArticleRecord(article, ArticleState.Draft, null, who, now, who, now), cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredArticle>> UpdateArticleAsync(PrincipalRef actor, Article article, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(article);
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Write, HelpCenterResources.Article(actor.Scope, article.Id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        var existing = await store.GetArticleAsync(actor.Scope, article.Id, cancellationToken).ConfigureAwait(false);
        if (Fence(existing?.Revision, expectedRevision) is { } fenced)
            return fenced;
        if (existing!.Record.Article.Portal != article.Portal)
            return Invalid("An article cannot move to another portal.");
        if (await ValidateArticleAsync(actor.Scope, article, cancellationToken).ConfigureAwait(false) is { } invalid)
            return invalid;
        var record = existing.Record;
        var updated = new ArticleRecord(article, record.State, record.PublishedRevision, record.CreatedBy, record.CreatedAt, AccessActor.From(actor), clock.GetUtcNow());
        return Map(await store.UpdateArticleAsync(actor.Scope, updated, expectedRevision, null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredArticle>> PublishArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForLifecycleAsync(actor, id, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (loaded.Refused is { } refused)
            return refused;
        var record = loaded.Article!.Record;
        if (record.State == ArticleState.Archived)
            return Invalid("Restore the archived article before publishing it.");
        var portal = await store.GetPortalAsync(actor.Scope, record.Article.Portal, cancellationToken).ConfigureAwait(false);
        if (portal is null)
            return NotFound;
        if (record.Article.Translation(portal.Portal.DefaultLocale) is null)
            return Invalid("Publishing requires a translation in the portal's default locale.");
        if (ValidateLocales(portal.Portal, record.Article.Translations.Select(translation => translation.Locale)) is { } invalid)
            return invalid;
        var next = await NextRevisionAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (next > HelpCenterLimits.MaximumRevisionsPerArticle)
            return new TicketFailure(TicketFailureKind.Rejected, ["The article has reached its revision limit."]);
        var who = AccessActor.From(actor);
        var now = clock.GetUtcNow();
        var revision = new ArticleRevision(id, next, record.Article.Translations.Select(translation => new PublishedTranslation(translation, renderer.ToPlainText(translation.Body))), who, now);
        var updated = new ArticleRecord(record.Article, ArticleState.Published, next, record.CreatedBy, record.CreatedAt, who, now);
        return Map(await store.UpdateArticleAsync(actor.Scope, updated, expectedRevision, revision, cancellationToken).ConfigureAwait(false));
    }

    public Task<TicketResult<StoredArticle>> UnpublishArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default) =>
        TransitionAsync(actor, id, expectedRevision, ArticleState.Draft, state => state == ArticleState.Published ? null : "Only published articles can be unpublished.", cancellationToken);

    public Task<TicketResult<StoredArticle>> ArchiveArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default) =>
        TransitionAsync(actor, id, expectedRevision, ArticleState.Archived, state => state == ArticleState.Archived ? "The article is already archived." : null, cancellationToken);

    public Task<TicketResult<StoredArticle>> RestoreArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default) =>
        TransitionAsync(actor, id, expectedRevision, ArticleState.Draft, state => state == ArticleState.Archived ? null : "Only archived articles can be restored.", cancellationToken);

    public async Task<TicketResult<StoredArticle>> RollbackArticleAsync(PrincipalRef actor, ArticleId id, int revision, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var loaded = await LoadForLifecycleAsync(actor, id, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (loaded.Refused is { } refused)
            return refused;
        var record = loaded.Article!.Record;
        if (record.State == ArticleState.Archived)
            return Invalid("Restore the archived article before rolling it back.");
        var target = revision < 1 ? null : await store.GetRevisionAsync(actor.Scope, id, revision, cancellationToken).ConfigureAwait(false);
        if (target is null)
            return new TicketFailure(TicketFailureKind.NotFound, ["The revision does not exist."]);
        var next = await NextRevisionAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (next > HelpCenterLimits.MaximumRevisionsPerArticle)
            return new TicketFailure(TicketFailureKind.Rejected, ["The article has reached its revision limit."]);
        var who = AccessActor.From(actor);
        var now = clock.GetUtcNow();
        var republished = new ArticleRevision(id, next, target.Translations, who, now);
        var article = new Article(id, record.Article.Portal, target.Translations.Select(translation => translation.Content), record.Article.Collection, record.Article.Position);
        var updated = new ArticleRecord(article, ArticleState.Published, next, record.CreatedBy, record.CreatedAt, who, now);
        return Map(await store.UpdateArticleAsync(actor.Scope, updated, expectedRevision, republished, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredArticle>> DeleteArticleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Manage, HelpCenterResources.Article(actor.Scope, id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        return Map(await store.DeleteArticleAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<ArticleRevision>> GetRevisionAsync(PrincipalRef actor, ArticleId id, int revision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Article(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return revision >= 1 && await store.GetRevisionAsync(actor.Scope, id, revision, cancellationToken).ConfigureAwait(false) is { } found
            ? new TicketValue<ArticleRevision>(found)
            : NotFound;
    }

    public async Task<TicketResult<HelpCenterPage<ArticleRevision>>> ListRevisionsAsync(PrincipalRef actor, ArticleId id, int limit = 20, int? before = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, HelpCenterLimits.MaximumPageSize);
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Article(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return new TicketValue<HelpCenterPage<ArticleRevision>>(await store.ListRevisionsAsync(actor.Scope, id, limit, before, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<ArticlePreview>> PreviewArticleAsync(PrincipalRef actor, ArticleId id, HelpCenterLocale locale, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized && locale.IsInitialized, nameof(id));
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Article(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        var article = await store.GetArticleAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (article?.Record.Article.Translation(locale) is not { } translation)
            return NotFound;
        return new TicketValue<ArticlePreview>(new ArticlePreview(id, locale, translation.Title, renderer.Render(translation.Body).Html));
    }

    public async Task<TicketResult<ArticleFeedbackCounts>> GetFeedbackAsync(PrincipalRef actor, ArticleId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        if (await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Read, HelpCenterResources.Article(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await feedback.GetCountsAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } counts ? new TicketValue<ArticleFeedbackCounts>(counts) : NotFound;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<TicketResult<StoredArticle>> TransitionAsync(PrincipalRef actor, ArticleId id, long expectedRevision, ArticleState target, Func<ArticleState, string?> check, CancellationToken cancellationToken)
    {
        var loaded = await LoadForLifecycleAsync(actor, id, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (loaded.Refused is { } refused)
            return refused;
        var record = loaded.Article!.Record;
        if (check(record.State) is { } error)
            return Invalid(error);
        var updated = new ArticleRecord(record.Article, target, record.PublishedRevision, record.CreatedBy, record.CreatedAt, AccessActor.From(actor), clock.GetUtcNow());
        return Map(await store.UpdateArticleAsync(actor.Scope, updated, expectedRevision, null, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Authorizes <c>helpcenter:publish</c>, validates the revision and returns the fenced article or a refusal.</summary>
    private async Task<(StoredArticle? Article, TicketFailure? Refused)> LoadForLifecycleAsync(PrincipalRef actor, ArticleId id, long expectedRevision, CancellationToken cancellationToken)
    {
        RequireActor(actor);
        Require(id.IsInitialized, nameof(id));
        var refused = await AuthorizeAsync(evaluator, actor, HelpCenterPermissions.Publish, HelpCenterResources.Article(actor.Scope, id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return (null, refused);
        var existing = await store.GetArticleAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        return Fence(existing?.Revision, expectedRevision) is { } fenced ? (null, fenced) : (existing, null);
    }

    private async Task<int> NextRevisionAsync(TicketScope scope, ArticleId id, CancellationToken cancellationToken) =>
        await store.GetLatestRevisionNumberAsync(scope, id, cancellationToken).ConfigureAwait(false) + 1;

    private async Task<TicketFailure?> ValidateCollectionAsync(TicketScope scope, HelpCollection collection, CancellationToken cancellationToken)
    {
        var portal = await store.GetPortalAsync(scope, collection.Portal, cancellationToken).ConfigureAwait(false);
        if (portal is null)
            return Invalid("The portal does not exist.");
        return ValidateLocales(portal.Portal, collection.Translations.Select(translation => translation.Locale));
    }

    private async Task<TicketFailure?> ValidateArticleAsync(TicketScope scope, Article article, CancellationToken cancellationToken)
    {
        var portal = await store.GetPortalAsync(scope, article.Portal, cancellationToken).ConfigureAwait(false);
        if (portal is null)
            return Invalid("The portal does not exist.");
        return ValidateLocales(portal.Portal, article.Translations.Select(translation => translation.Locale));
    }

    private static void Require(bool initialized, string parameterName)
    {
        if (!initialized)
            throw new ArgumentException("Identifiers must be initialized.", parameterName);
    }
}
