using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.HelpCenter;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Core;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class HelpCenterEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/help-center/portals", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.ListPortalsAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Portal.Id.Value, Dto, limit, cursor))))
            .WithName("ListPortals").Produces<PageResponse<PortalResponse>>();
        group.MapGet("/help-center/portals/{portalId}", async (TicketFlowActor actor, string portalId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetPortalAsync(actor.Principal, new PortalId(portalId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetPortal").Produces<PortalResponse>();
        group.MapPost("/help-center/portals", async (TicketFlowActor actor, PortalRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.CreatePortalAsync(actor.Principal, Portal(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"help-center/portals/{stored.Portal.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreatePortal").Produces<PortalResponse>(StatusCodes.Status201Created);
        group.MapPut("/help-center/portals/{portalId}", async (TicketFlowActor actor, string portalId, PortalRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdatePortalAsync(actor.Principal, Portal(portalId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdatePortal").Produces<PortalResponse>();
        group.MapDelete("/help-center/portals/{portalId}", async (TicketFlowActor actor, string portalId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeletePortalAsync(actor.Principal, new PortalId(portalId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeletePortal").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/help-center/portals/{portalId}/collections", async (TicketFlowActor actor, string portalId, int? limit, string? cursor, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.ListCollectionsAsync(actor.Principal, new PortalId(portalId), http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Collection.Id.Value, Dto, limit, cursor))))
            .WithName("ListCollections").Produces<PageResponse<CollectionResponse>>();
        group.MapGet("/help-center/collections/{collectionId}", async (TicketFlowActor actor, string collectionId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetCollectionAsync(actor.Principal, new CollectionId(collectionId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetCollection").Produces<CollectionResponse>();
        group.MapPost("/help-center/collections", async (TicketFlowActor actor, CollectionRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.CreateCollectionAsync(actor.Principal, Collection(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"help-center/collections/{stored.Collection.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateCollection").Produces<CollectionResponse>(StatusCodes.Status201Created);
        group.MapPut("/help-center/collections/{collectionId}", async (TicketFlowActor actor, string collectionId, CollectionRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateCollectionAsync(actor.Principal, Collection(collectionId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateCollection").Produces<CollectionResponse>();
        group.MapDelete("/help-center/collections/{collectionId}", async (TicketFlowActor actor, string collectionId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteCollectionAsync(actor.Principal, new CollectionId(collectionId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteCollection").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/help-center/portals/{portalId}/articles", async (TicketFlowActor actor, string portalId, string? collection, string? state, int? limit, string? cursor,
            [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            var query = new ArticleListQuery(new PortalId(portalId), collection is null ? null : new CollectionId(collection), TicketFlowHttp.ParseOptional<ArticleState>(state, "state"),
                Math.Min(TicketFlowHttp.PageSize(limit), HelpCenterLimits.MaximumPageSize), cursor is null ? null : new ArticleId(TicketFlowHttp.DecodeCursor(cursor, 1)[0]));
            return Map(http, await service.ListArticlesAsync(actor.Principal, query, http.RequestAborted), page => TicketFlowHttp.Ok(http,
                new PageResponse<ArticleResponse>([.. page.Items.Select(Dto)], page.HasMore ? TicketFlowHttp.EncodeCursor(page.Items[^1].Id.Value) : null)));
        }).WithName("ListArticles").Produces<PageResponse<ArticleResponse>>();
        group.MapGet("/help-center/articles/{articleId}", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetArticleAsync(actor.Principal, new ArticleId(articleId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetArticle").Produces<ArticleResponse>();
        group.MapPost("/help-center/articles", async (TicketFlowActor actor, ArticleRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.CreateArticleAsync(actor.Principal, Article(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"help-center/articles/{stored.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateArticle").Produces<ArticleResponse>(StatusCodes.Status201Created);
        group.MapPut("/help-center/articles/{articleId}", async (TicketFlowActor actor, string articleId, ArticleRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateArticleAsync(actor.Principal, Article(articleId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateArticle").Produces<ArticleResponse>();
        Transition(group, "publish", "PublishArticle", (service, actor, id, revision, ct) => service.PublishArticleAsync(actor.Principal, id, revision, ct));
        Transition(group, "unpublish", "UnpublishArticle", (service, actor, id, revision, ct) => service.UnpublishArticleAsync(actor.Principal, id, revision, ct));
        Transition(group, "archive", "ArchiveArticle", (service, actor, id, revision, ct) => service.ArchiveArticleAsync(actor.Principal, id, revision, ct));
        Transition(group, "restore", "RestoreArticle", (service, actor, id, revision, ct) => service.RestoreArticleAsync(actor.Principal, id, revision, ct));
        group.MapPost("/help-center/articles/{articleId}/rollback", async (TicketFlowActor actor, string articleId, RollbackRequest request, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.RollbackArticleAsync(actor.Principal, new ArticleId(articleId), request.Revision, revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("RollbackArticle").Produces<ArticleResponse>();
        group.MapDelete("/help-center/articles/{articleId}", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteArticleAsync(actor.Principal, new ArticleId(articleId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteArticle").Produces(StatusCodes.Status204NoContent);
        group.MapGet("/help-center/articles/{articleId}/revisions", async (TicketFlowActor actor, string articleId, int? limit, string? cursor, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.ListRevisionsAsync(actor.Principal, new ArticleId(articleId), Math.Min(TicketFlowHttp.PageSize(limit), HelpCenterLimits.MaximumPageSize),
                cursor is null ? null : int.Parse(TicketFlowHttp.DecodeCursor(cursor, 1)[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture), http.RequestAborted), page => TicketFlowHttp.Ok(http,
                new PageResponse<ArticleRevisionResponse>([.. page.Items.Select(Dto)], page.HasMore ? TicketFlowHttp.EncodeCursor(page.Items[^1].Number.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null))))
            .WithName("ListArticleRevisions").Produces<PageResponse<ArticleRevisionResponse>>();
        group.MapGet("/help-center/articles/{articleId}/revisions/{number:int}", async (TicketFlowActor actor, string articleId, int number, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetRevisionAsync(actor.Principal, new ArticleId(articleId), number, http.RequestAborted), revision => TicketFlowHttp.Ok(http, Dto(revision))))
            .WithName("GetArticleRevision").Produces<ArticleRevisionResponse>();
        group.MapGet("/help-center/articles/{articleId}/preview", async (TicketFlowActor actor, string articleId, string locale, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.PreviewArticleAsync(actor.Principal, new ArticleId(articleId), new HelpCenterLocale(locale), http.RequestAborted),
                preview => TicketFlowHttp.Ok(http, new ArticlePreviewResponse(preview.Article.Value, preview.Locale.Value, preview.Title, preview.Html))))
            .WithName("PreviewArticle").Produces<ArticlePreviewResponse>();
        group.MapGet("/help-center/articles/{articleId}/feedback", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
            Map(http, await service.GetFeedbackAsync(actor.Principal, new ArticleId(articleId), http.RequestAborted),
                counts => TicketFlowHttp.Ok(http, new ArticleFeedbackResponse(counts.Views, counts.Helpful, counts.NotHelpful))))
            .WithName("GetArticleFeedback").Produces<ArticleFeedbackResponse>();
        group.MapGet("/help-center/search", async (TicketFlowActor actor, string portal, string locale, string q, int? limit, int? offset, [FromServices] IHelpCenterSearch search, HttpContext http) =>
            Map(http, await search.SearchAsync(actor.Principal, new HelpCenterSearchQuery(new PortalId(portal), new HelpCenterLocale(locale), q, limit ?? 10, offset ?? 0), http.RequestAborted),
                snippets => TicketFlowHttp.Ok(http, snippets.Select(Dto).ToList())))
            .WithName("SearchArticles").Produces<List<ArticleSnippetResponse>>();
        return group;
    }

    private delegate Task<TicketResult<StoredArticle>> TransitionHandler(IHelpCenterService service, TicketFlowActor actor, ArticleId article, long expectedRevision, CancellationToken cancellationToken);

    private static void Transition(RouteGroupBuilder group, string action, string name, TransitionHandler handler) =>
        group.MapPost($"/help-center/articles/{{articleId}}/{action}", async (TicketFlowActor actor, string articleId, [FromServices] IHelpCenterService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await handler(service, actor, new ArticleId(articleId), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName(name).Produces<ArticleResponse>();

    internal static IResult Map<T>(HttpContext http, TicketResult<T> result, Func<T, IResult> success) => TicketFlowHttp.Map(http, result, success);

    private static Portal Portal(string id, PortalRequest request) => new(new PortalId(id), new HelpCenterSlug(request.Slug), request.Name, new HelpCenterLocale(request.DefaultLocale),
        request.SupportedLocales?.Select(locale => new HelpCenterLocale(locale)),
        request.Visibility is null ? PortalVisibility.Private : TicketFlowHttp.Parse<PortalVisibility>(request.Visibility, "visibility"), request.CustomDomain);

    private static PortalResponse Dto(StoredPortal stored) => new(stored.Portal.Id.Value, stored.Portal.Slug.Value, stored.Portal.Name, stored.Portal.DefaultLocale.Value,
        [.. stored.Portal.SupportedLocales.Select(locale => locale.Value)], TicketFlowHttp.Token(stored.Portal.Visibility), stored.Portal.CustomDomain, stored.Revision);

    private static HelpCollection Collection(string id, CollectionRequest request) => new(new CollectionId(id), new PortalId(request.PortalId),
        (request.Translations ?? throw new ArgumentException("Translations are required.", nameof(request.Translations)))
            .Select(translation => new CollectionTranslation(new HelpCenterLocale(translation.Locale), translation.Name, new HelpCenterSlug(translation.Slug), translation.Description)),
        request.ParentId is null ? null : new CollectionId(request.ParentId), request.Position);

    private static CollectionResponse Dto(StoredCollection stored) => new(stored.Collection.Id.Value, stored.Collection.Portal.Value, stored.Collection.Parent?.Value, stored.Collection.Position,
        stored.Depth, [.. stored.Collection.Translations.Select(translation => new CollectionTranslationModel(translation.Locale.Value, translation.Name, translation.Slug.Value, translation.Description))],
        stored.Revision);

    private static Article Article(string id, ArticleRequest request) => new(new ArticleId(id), new PortalId(request.PortalId),
        (request.Translations ?? throw new ArgumentException("Translations are required.", nameof(request.Translations))).Select(Translation),
        request.CollectionId is null ? null : new CollectionId(request.CollectionId), request.Position);

    private static ArticleTranslation Translation(ArticleTranslationModel dto) =>
        new(new HelpCenterLocale(dto.Locale), dto.Title, new HelpCenterSlug(dto.Slug), dto.Body, dto.Excerpt, dto.Tags);

    private static ArticleTranslationModel Dto(ArticleTranslation translation) =>
        new(translation.Locale.Value, translation.Title, translation.Slug.Value, translation.Body, translation.Excerpt, translation.Tags);

    private static ArticleResponse Dto(StoredArticle stored)
    {
        var record = stored.Record;
        return new ArticleResponse(record.Article.Id.Value, record.Article.Portal.Value, record.Article.Collection?.Value, record.Article.Position,
            [.. record.Article.Translations.Select(Dto)], TicketFlowHttp.Token(record.State), record.PublishedRevision, ConversationEndpoints.Actor(record.CreatedBy), record.CreatedAt,
            ConversationEndpoints.Actor(record.UpdatedBy), record.UpdatedAt, stored.Revision);
    }

    private static ArticleRevisionResponse Dto(ArticleRevision revision) => new(revision.Article.Value, revision.Number, [.. revision.Translations.Select(translation => Dto(translation.Content))],
        ConversationEndpoints.Actor(revision.PublishedBy), revision.PublishedAt);

    internal static ArticleSnippetResponse Dto(ArticleSnippet snippet) => new(snippet.Article.Value, snippet.Portal.Value, snippet.Locale.Value, snippet.Title, snippet.Slug.Value, snippet.Excerpt,
        snippet.Snippet, snippet.Rank, snippet.RevisionNumber);
}
