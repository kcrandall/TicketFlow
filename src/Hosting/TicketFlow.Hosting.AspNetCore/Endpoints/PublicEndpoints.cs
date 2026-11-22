using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.HelpCenter;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Surveys;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

/// <summary>
/// Anonymous endpoints. The scope comes from the host partition resolver plus the route workspace; readers enforce portal visibility and
/// token validity. Responses are uniform for unknown and private resources (404) and never reveal internal identifiers beyond the public model.
/// </summary>
internal static class PublicEndpoints
{
    public static RouteGroupBuilder MapPortal(RouteGroupBuilder group)
    {
        group.MapGet("/portals/{portal}", async (TicketFlowPublicScope scope, string portal, [FromServices] IHelpCenterPublicReader reader, HttpContext http) =>
            await reader.GetPortalAsync(scope.Scope, new HelpCenterSlug(portal), http.RequestAborted) is { } found
                ? TicketFlowHttp.Ok(http, new PublicPortalDto(found.Id.Value, found.Slug.Value, found.Name, found.DefaultLocale.Value, [.. found.SupportedLocales.Select(locale => locale.Value)]))
                : TicketFlowHttp.NotFound())
            .WithName("GetPublicPortal").Produces<PublicPortalDto>();
        group.MapGet("/portals/{portal}/{locale}/collections", async (TicketFlowPublicScope scope, string portal, string locale, [FromServices] IHelpCenterPublicReader reader, HttpContext http) =>
            TicketFlowHttp.Ok(http, (await reader.ListCollectionsAsync(scope.Scope, new HelpCenterSlug(portal), new HelpCenterLocale(locale), http.RequestAborted))
                .Select(collection => new PublicCollectionDto(collection.Id.Value, collection.Parent?.Value, collection.Position, collection.Locale.Value, collection.Name, collection.Slug.Value,
                    collection.Description)).ToList()))
            .WithName("ListPublicCollections").Produces<List<PublicCollectionDto>>();
        group.MapGet("/portals/{portal}/{locale}/articles", async (TicketFlowPublicScope scope, string portal, string locale, string? collection, int? limit, string? cursor,
            IHelpCenterPublicReader reader, HttpContext http) =>
        {
            PublishedArticleCursor? after = null;
            if (cursor is not null)
            {
                var parts = TicketFlowHttp.DecodeCursor(cursor, 2);
                after = new PublishedArticleCursor(int.Parse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture), new ArticleId(parts[1]));
            }
            var page = await reader.ListArticlesAsync(scope.Scope, new HelpCenterSlug(portal), new HelpCenterLocale(locale), collection is null ? null : new CollectionId(collection),
                limit ?? 50, after, http.RequestAborted);
            return TicketFlowHttp.Ok(http, new PageDto<PublicArticleSummaryDto>(
                [.. page.Items.Select(item => new PublicArticleSummaryDto(item.Id.Value, item.Collection?.Value, item.Position, item.Title, item.Slug.Value, item.Excerpt, item.PublishedAt))],
                page.Next is { } next ? TicketFlowHttp.EncodeCursor(next.Position.ToString(CultureInfo.InvariantCulture), next.Article.Value) : null));
        }).WithName("ListPublicArticles").Produces<PageDto<PublicArticleSummaryDto>>();
        group.MapGet("/portals/{portal}/{locale}/articles/{slug}", async (TicketFlowPublicScope scope, string portal, string locale, string slug, [FromServices] IHelpCenterPublicReader reader, HttpContext http) =>
            await reader.GetArticleAsync(scope.Scope, new HelpCenterSlug(portal), new HelpCenterLocale(locale), new HelpCenterSlug(slug), http.RequestAborted) is { } article
                ? TicketFlowHttp.Ok(http, new PublicArticleDto(article.Id.Value, article.Portal.Value, article.Collection?.Value, article.Locale.Value, article.Title, article.Slug.Value,
                    article.Excerpt, article.Tags, article.Html, article.RevisionNumber, article.PublishedAt))
                : TicketFlowHttp.NotFound())
            .WithName("GetPublicArticle").Produces<PublicArticleDto>();
        group.MapGet("/portals/{portal}/{locale}/search", async (TicketFlowPublicScope scope, string portal, string locale, string q, int? limit, [FromServices] IHelpCenterPublicReader reader, HttpContext http) =>
            TicketFlowHttp.Ok(http, (await reader.SearchAsync(scope.Scope, new HelpCenterSlug(portal), new HelpCenterLocale(locale), q, limit ?? 10, http.RequestAborted))
                .Select(HelpCenterEndpoints.Dto).ToList()))
            .WithName("SearchPublicArticles").Produces<List<ArticleSnippetDto>>();
        group.MapPost("/portals/{portal}/articles/{articleId}/views", async (TicketFlowPublicScope scope, string portal, string articleId, ArticleVisitRequest request,
            IHelpCenterPublicReader reader, HttpContext http) =>
            Feedback(await reader.RecordViewAsync(scope.Scope, new HelpCenterSlug(portal), new ArticleId(articleId), request.VisitorKey, http.RequestAborted)))
            .WithName("RecordArticleView").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/portals/{portal}/articles/{articleId}/feedback", async (TicketFlowPublicScope scope, string portal, string articleId, ArticleVisitRequest request,
            IHelpCenterPublicReader reader, HttpContext http) =>
            Feedback(await reader.RecordFeedbackAsync(scope.Scope, new HelpCenterSlug(portal), new ArticleId(articleId), request.VisitorKey,
                request.Helpful ?? throw new ArgumentException("'helpful' is required.", nameof(request.Helpful)), http.RequestAborted)))
            .WithName("RecordArticleFeedback").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    public static RouteGroupBuilder MapSurveys(RouteGroupBuilder group)
    {
        group.MapPost("/surveys/invitation", async (TicketFlowPublicScope scope, SurveyTokenRequest request, [FromServices] ISurveyResponder responder, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await responder.GetInvitationAsync(scope.Scope, request.Token ?? string.Empty, http.RequestAborted) is { } view
                ? TicketFlowHttp.Ok(http, new SurveyInvitationDto(TicketFlowHttp.Token(view.Kind), view.MinimumScore, view.MaximumScore, view.Locale.Value, view.Question, view.CommentPrompt,
                    view.ExpiresAt, view.Responded))
                : TicketFlowHttp.NotFound();
        }).WithName("GetSurveyInvitation").Produces<SurveyInvitationDto>();
        group.MapPost("/surveys/respond", async (TicketFlowPublicScope scope, SurveyRespondRequest request, [FromServices] ISurveyResponder responder, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await responder.RespondAsync(scope.Scope, request.Token ?? string.Empty, request.Score, request.Comment, http.RequestAborted) switch
            {
                SurveyResponseAccepted accepted => TicketFlowHttp.Ok(http, new SurveyRespondResultDto("accepted", accepted.Response.Score, accepted.Response.RespondedAt)),
                SurveyResponseDuplicate duplicate => TicketFlowHttp.Ok(http, new SurveyRespondResultDto("duplicate", duplicate.Response.Score, duplicate.Response.RespondedAt)),
                SurveyResponseRejected rejected => rejected.Reason switch
                {
                    SurveyResponseRejection.InvalidToken => TicketFlowHttp.NotFound(),
                    SurveyResponseRejection.Expired => TicketFlowHttp.Problem(StatusCodes.Status410Gone, "expired"),
                    SurveyResponseRejection.AlreadyResponded => TicketFlowHttp.Problem(StatusCodes.Status409Conflict, "already-responded"),
                    _ => TicketFlowHttp.Problem(StatusCodes.Status422UnprocessableEntity, TicketFlowHttp.Token(rejected.Reason) switch
                    {
                        "invalidScore" => "invalid-score",
                        _ => "invalid-comment",
                    }),
                },
            };
        }).WithName("RespondToSurvey").Produces<SurveyRespondResultDto>();
        return group;
    }

    private static IResult Feedback(ArticleFeedbackResult result) => result switch
    {
        FeedbackCounted => TypedResults.NoContent(),
        FeedbackUnchanged => TypedResults.NoContent(),
        FeedbackArticleNotFound => TicketFlowHttp.NotFound(),
    };
}
