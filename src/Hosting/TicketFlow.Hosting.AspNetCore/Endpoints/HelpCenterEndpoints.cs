using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.HelpCenter;
using TicketFlow.Hosting.AspNetCore.Contracts;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class HelpCenterEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/helpcenter/portals", async (TicketFlowActor actor, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.ListPortalsAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, list.Select(Dto).ToList())))
            .WithName("ListPortals").Produces<List<PortalDto>>();
        group.MapGet("/helpcenter/portals/{portalId}", async (TicketFlowActor actor, string portalId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetPortalAsync(actor.Principal, new PortalId(portalId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetPortal").Produces<PortalDto>();
        group.MapPost("/helpcenter/portals", async (TicketFlowActor actor, PortalRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.CreatePortalAsync(actor.Principal, Portal(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"helpcenter/portals/{stored.Portal.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreatePortal").Produces<PortalDto>(StatusCodes.Status201Created);
        group.MapPut("/helpcenter/portals/{portalId}", async (TicketFlowActor actor, string portalId, PortalRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdatePortalAsync(actor.Principal, Portal(portalId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdatePortal").Produces<PortalDto>();
        group.MapDelete("/helpcenter/portals/{portalId}", async (TicketFlowActor actor, string portalId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeletePortalAsync(actor.Principal, new PortalId(portalId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeletePortal").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/helpcenter/portals/{portalId}/collections", async (TicketFlowActor actor, string portalId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.ListCollectionsAsync(actor.Principal, new PortalId(portalId), http.RequestAborted), list => TicketFlowHttp.Ok(http, list.Select(Dto).ToList())))
            .WithName("ListCollections").Produces<List<CollectionDto>>();
        group.MapGet("/helpcenter/collections/{collectionId}", async (TicketFlowActor actor, string collectionId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetCollectionAsync(actor.Principal, new CollectionId(collectionId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetCollection").Produces<CollectionDto>();
        group.MapPost("/helpcenter/collections", async (TicketFlowActor actor, CollectionRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.CreateCollectionAsync(actor.Principal, Collection(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"helpcenter/collections/{stored.Collection.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateCollection").Produces<CollectionDto>(StatusCodes.Status201Created);
        group.MapPut("/helpcenter/collections/{collectionId}", async (TicketFlowActor actor, string collectionId, CollectionRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateCollectionAsync(actor.Principal, Collection(collectionId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateCollection").Produces<CollectionDto>();
        group.MapDelete("/helpcenter/collections/{collectionId}", async (TicketFlowActor actor, string collectionId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteCollectionAsync(actor.Principal, new CollectionId(collectionId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteCollection").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/helpcenter/portals/{portalId}/articles", async (TicketFlowActor actor, string portalId, string? collection, string? state, int? limit, string? after,
            IHelpCenterService service, HttpContext http) =>
        {
            var query = new ArticleListQuery(new PortalId(portalId), collection is null ? null : new CollectionId(collection), TicketFlowHttp.ParseOptional<ArticleState>(state, "state"),
                limit ?? 50, after is null ? null : new ArticleId(after));
            return Map(http, await service.ListArticlesAsync(actor.Principal, query, http.RequestAborted), page => TicketFlowHttp.Ok(http,
                new PageDto<ArticleDto>([.. page.Items.Select(Dto)], page.HasMore ? page.Items[^1].Id.Value : null)));
        }).WithName("ListArticles").Produces<PageDto<ArticleDto>>();
        group.MapGet("/helpcenter/articles/{articleId}", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetArticleAsync(actor.Principal, new ArticleId(articleId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetArticle").Produces<ArticleDto>();
        group.MapPost("/helpcenter/articles", async (TicketFlowActor actor, ArticleRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.CreateArticleAsync(actor.Principal, Article(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"helpcenter/articles/{stored.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateArticle").Produces<ArticleDto>(StatusCodes.Status201Created);
        group.MapPut("/helpcenter/articles/{articleId}", async (TicketFlowActor actor, string articleId, ArticleRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateArticleAsync(actor.Principal, Article(articleId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateArticle").Produces<ArticleDto>();
        Transition(group, "publish", "PublishArticle", (service, actor, id, revision, ct) => service.PublishArticleAsync(actor.Principal, id, revision, ct));
        Transition(group, "unpublish", "UnpublishArticle", (service, actor, id, revision, ct) => service.UnpublishArticleAsync(actor.Principal, id, revision, ct));
        Transition(group, "archive", "ArchiveArticle", (service, actor, id, revision, ct) => service.ArchiveArticleAsync(actor.Principal, id, revision, ct));
        Transition(group, "restore", "RestoreArticle", (service, actor, id, revision, ct) => service.RestoreArticleAsync(actor.Principal, id, revision, ct));
        group.MapPost("/helpcenter/articles/{articleId}/rollback", async (TicketFlowActor actor, string articleId, RollbackRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.RollbackArticleAsync(actor.Principal, new ArticleId(articleId), request.Revision, revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("RollbackArticle").Produces<ArticleDto>();
        group.MapDelete("/helpcenter/articles/{articleId}", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteArticleAsync(actor.Principal, new ArticleId(articleId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteArticle").Produces(StatusCodes.Status204NoContent);
        group.MapGet("/helpcenter/articles/{articleId}/revisions", async (TicketFlowActor actor, string articleId, int? limit, int? before, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.ListRevisionsAsync(actor.Principal, new ArticleId(articleId), limit ?? 20, before, http.RequestAborted), page => TicketFlowHttp.Ok(http,
                new PageDto<ArticleRevisionDto>([.. page.Items.Select(Dto)], page.HasMore ? page.Items[^1].Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null))))
            .WithName("ListArticleRevisions").Produces<PageDto<ArticleRevisionDto>>();
        group.MapGet("/helpcenter/articles/{articleId}/revisions/{number:int}", async (TicketFlowActor actor, string articleId, int number, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetRevisionAsync(actor.Principal, new ArticleId(articleId), number, http.RequestAborted), revision => TicketFlowHttp.Ok(http, Dto(revision))))
            .WithName("GetArticleRevision").Produces<ArticleRevisionDto>();
        group.MapGet("/helpcenter/articles/{articleId}/preview", async (TicketFlowActor actor, string articleId, string locale, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.PreviewArticleAsync(actor.Principal, new ArticleId(articleId), new HelpCenterLocale(locale), http.RequestAborted),
                preview => TicketFlowHttp.Ok(http, new ArticlePreviewDto(preview.Article.Value, preview.Locale.Value, preview.Title, preview.Html))))
            .WithName("PreviewArticle").Produces<ArticlePreviewDto>();
        group.MapGet("/helpcenter/articles/{articleId}/feedback", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetFeedbackAsync(actor.Principal, new ArticleId(articleId), http.RequestAborted),
                counts => TicketFlowHttp.Ok(http, new ArticleFeedbackDto(counts.Views, counts.Helpful, counts.NotHelpful))))
            .WithName("GetArticleFeedback").Produces<ArticleFeedbackDto>();
        group.MapGet("/helpcenter/search", async (TicketFlowActor actor, string portal, string locale, string q, int? limit, int? offset, [FromServices] IHelpCenterSearch search, HttpContext http) =>
            Map(http, await search.SearchAsync(actor.Principal, new HelpCenterSearchQuery(new PortalId(portal), new HelpCenterLocale(locale), q, limit ?? 10, offset ?? 0), http.RequestAborted),
                snippets => TicketFlowHttp.Ok(http, snippets.Select(Dto).ToList())))
            .WithName("SearchArticles").Produces<List<ArticleSnippetDto>>();
        return group;
    }

    private delegate Task<HelpCenterResult<StoredArticle>> TransitionHandler(IHelpCenterService service, TicketFlowActor actor, ArticleId article, long expectedRevision, CancellationToken cancellationToken);

    private static void Transition(RouteGroupBuilder group, string action, string name, TransitionHandler handler) =>
        group.MapPost($"/helpcenter/articles/{{articleId}}/{action}", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await handler(service, actor, new ArticleId(articleId), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName(name).Produces<ArticleDto>();

    internal static IResult Map<T>(HttpContext http, HelpCenterResult<T> result, Func<T, IResult> success) => result switch
    {
        HelpCenterSucceeded<T> succeeded => success(succeeded.Value),
        HelpCenterRefused refused => TicketFlowHttp.Refused(http, refused.Reason switch
        {
            HelpCenterRefusal.Forbidden => RefusalKind.Forbidden,
            HelpCenterRefusal.Invalid => RefusalKind.Invalid,
            HelpCenterRefusal.NotFound => RefusalKind.NotFound,
            HelpCenterRefusal.Conflict => RefusalKind.Conflict,
            _ => RefusalKind.Rejected,
        }, refused.Errors, refused.CurrentRevision),
    };

    private static Portal Portal(string id, PortalRequest request) => new(new PortalId(id), new HelpCenterSlug(request.Slug), request.Name, new HelpCenterLocale(request.DefaultLocale),
        request.SupportedLocales?.Select(locale => new HelpCenterLocale(locale)),
        request.Visibility is null ? PortalVisibility.Private : TicketFlowHttp.Parse<PortalVisibility>(request.Visibility, "visibility"), request.CustomDomain);

    private static PortalDto Dto(StoredPortal stored) => new(stored.Portal.Id.Value, stored.Portal.Slug.Value, stored.Portal.Name, stored.Portal.DefaultLocale.Value,
        [.. stored.Portal.SupportedLocales.Select(locale => locale.Value)], TicketFlowHttp.Token(stored.Portal.Visibility), stored.Portal.CustomDomain, stored.Revision);

    private static HelpCollection Collection(string id, CollectionRequest request) => new(new CollectionId(id), new PortalId(request.PortalId),
        (request.Translations ?? throw new ArgumentException("Translations are required.", nameof(request.Translations)))
            .Select(translation => new CollectionTranslation(new HelpCenterLocale(translation.Locale), translation.Name, new HelpCenterSlug(translation.Slug), translation.Description)),
        request.ParentId is null ? null : new CollectionId(request.ParentId), request.Position);

    private static CollectionDto Dto(StoredCollection stored) => new(stored.Collection.Id.Value, stored.Collection.Portal.Value, stored.Collection.Parent?.Value, stored.Collection.Position,
        stored.Depth, [.. stored.Collection.Translations.Select(translation => new CollectionTranslationDto(translation.Locale.Value, translation.Name, translation.Slug.Value, translation.Description))],
        stored.Revision);

    private static Article Article(string id, ArticleRequest request) => new(new ArticleId(id), new PortalId(request.PortalId),
        (request.Translations ?? throw new ArgumentException("Translations are required.", nameof(request.Translations))).Select(Translation),
        request.CollectionId is null ? null : new CollectionId(request.CollectionId), request.Position);

    private static ArticleTranslation Translation(ArticleTranslationDto dto) =>
        new(new HelpCenterLocale(dto.Locale), dto.Title, new HelpCenterSlug(dto.Slug), dto.Body, dto.Excerpt, dto.Tags);

    private static ArticleTranslationDto Dto(ArticleTranslation translation) =>
        new(translation.Locale.Value, translation.Title, translation.Slug.Value, translation.Body, translation.Excerpt, translation.Tags);

    private static ArticleDto Dto(StoredArticle stored)
    {
        var record = stored.Record;
        return new ArticleDto(record.Article.Id.Value, record.Article.Portal.Value, record.Article.Collection?.Value, record.Article.Position,
            [.. record.Article.Translations.Select(Dto)], TicketFlowHttp.Token(record.State), record.PublishedRevision, ConversationEndpoints.Actor(record.CreatedBy), record.CreatedAt,
            ConversationEndpoints.Actor(record.UpdatedBy), record.UpdatedAt, stored.Revision);
    }

    private static ArticleRevisionDto Dto(ArticleRevision revision) => new(revision.Article.Value, revision.Number, [.. revision.Translations.Select(translation => Dto(translation.Content))],
        ConversationEndpoints.Actor(revision.PublishedBy), revision.PublishedAt);

    internal static ArticleSnippetDto Dto(ArticleSnippet snippet) => new(snippet.Article.Value, snippet.Portal.Value, snippet.Locale.Value, snippet.Title, snippet.Slug.Value, snippet.Excerpt,
        snippet.Snippet, snippet.Rank, snippet.RevisionNumber);
}
