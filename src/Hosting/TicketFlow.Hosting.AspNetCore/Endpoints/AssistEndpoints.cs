using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Assist;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Hosting.AspNetCore.Contracts;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class AssistEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapPost("/conversations/{conversationId}/assist/summary", async (TicketFlowActor actor, string conversationId, [FromServices] IAssistService service, HttpContext http) =>
            Map(http, await service.SummarizeAsync(actor.Principal, new AssistSummaryRequest(new ConversationId(conversationId)), http.RequestAborted),
                summary => new AssistSummaryDto(summary.Summary, summary.KeyPoints, summary.NextStep)))
            .WithName("AssistSummary").Produces<AssistResultDto<AssistSummaryDto>>();
        group.MapPost("/conversations/{conversationId}/assist/draft", async (TicketFlowActor actor, string conversationId, AssistDraftRequestDto? request, [FromServices] IAssistService service, HttpContext http) =>
        {
            var grounding = request?.Grounding is { } value ? new AssistGrounding(new PortalId(value.PortalId), new HelpCenterLocale(value.Locale), value.Query) : null;
            return Map(http, await service.DraftReplyAsync(actor.Principal, new AssistDraftRequest(new ConversationId(conversationId), grounding, request?.Instructions), http.RequestAborted),
                draft => new AssistDraftDto(draft.Body, [.. draft.Citations.Select(citation => new AssistCitationDto(citation.Article.Value, citation.Portal.Value, citation.Locale.Value,
                    citation.Title, citation.Slug.Value, citation.RevisionNumber))], draft.Grounded));
        }).WithName("AssistDraft").Produces<AssistResultDto<AssistDraftDto>>();
        group.MapPost("/conversations/{conversationId}/assist/triage", async (TicketFlowActor actor, string conversationId, AssistTriageRequestDto? request, [FromServices] IAssistService service, HttpContext http) =>
        {
            var triage = new AssistTriageRequest(new ConversationId(conversationId),
                request?.Labels?.Select(option => new AssistLabelOption(new LabelId(option.Id), option.Description)),
                request?.Teams?.Select(option => new AssistTeamOption(new TeamId(option.Id), option.Description)), request?.SuggestPriority ?? true);
            return Map(http, await service.TriageAsync(actor.Principal, triage, http.RequestAborted), result => new AssistTriageDto(
                [.. result.Labels.Select(label => new AssistSuggestionDto(label.Label.Value, label.Confidence))],
                result.Team is { } team ? new AssistSuggestionDto(team.Team.Value, team.Confidence) : null,
                result.Priority is { } priority ? new AssistSuggestionDto(TicketFlowHttp.Token(priority.Priority), priority.Confidence) : null, result.Rationale));
        }).WithName("AssistTriage").Produces<AssistResultDto<AssistTriageDto>>();
        group.MapPost("/conversations/{conversationId}/assist/sentiment", async (TicketFlowActor actor, string conversationId, AssistSentimentRequestDto? request, [FromServices] IAssistService service, HttpContext http) =>
            Map(http, await service.AnalyzeSentimentAsync(actor.Principal, new AssistSentimentRequest(new ConversationId(conversationId), request?.Intents), http.RequestAborted),
                result => new AssistSentimentDto(TicketFlowHttp.Token(result.Sentiment), result.SentimentConfidence, result.Intent, result.IntentConfidence)))
            .WithName("AssistSentiment").Produces<AssistResultDto<AssistSentimentDto>>();
        group.MapPost("/conversations/{conversationId}/assist/translate", async (TicketFlowActor actor, string conversationId, AssistTranslateRequestDto request, [FromServices] IAssistService service, HttpContext http) =>
            Map(http, await service.TranslateAsync(actor.Principal, new AssistTranslateRequest(new ConversationId(conversationId), new HelpCenterLocale(request.TargetLocale), request.Text,
                request.MessageId is null ? null : new MessageId(request.MessageId)), http.RequestAborted),
                result => new AssistTranslationDto(result.Text, result.TargetLocale.Value, result.SourceLocale?.Value)))
            .WithName("AssistTranslate").Produces<AssistResultDto<AssistTranslationDto>>();
        group.MapPost("/conversations/{conversationId}/assist/rewrite", async (TicketFlowActor actor, string conversationId, AssistRewriteRequestDto request, [FromServices] IAssistService service, HttpContext http) =>
            Map(http, await service.RewriteToneAsync(actor.Principal, new AssistToneRewriteRequest(new ConversationId(conversationId), request.Text,
                TicketFlowHttp.Parse<AssistTone>(request.Tone, "tone")), http.RequestAborted), result => new AssistRewriteDto(result.Text, TicketFlowHttp.Token(result.Tone))))
            .WithName("AssistRewrite").Produces<AssistResultDto<AssistRewriteDto>>();

        group.MapGet("/assist/settings", async (TicketFlowActor actor, [FromServices] IAssistAdministration administration, HttpContext http) =>
            Map(http, await administration.GetSettingsAsync(actor.Principal, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Settings(stored), stored.Revision)))
            .WithName("GetAssistSettings").Produces<AssistSettingsDto>();
        group.MapPut("/assist/settings", async (TicketFlowActor actor, AssistSettingsRequest request, [FromServices] IAssistAdministration administration, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administration.UpdateSettingsAsync(actor.Principal, AssistJsonCodec.DecodeSettings(Json.Raw(request.Settings, "settings")), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Settings(stored), stored.Revision));
        }).WithName("UpdateAssistSettings").Produces<AssistSettingsDto>();
        group.MapGet("/assist/usage", async (TicketFlowActor actor, DateTimeOffset from, DateTimeOffset to, [FromServices] IAssistAdministration administration, HttpContext http) =>
            Map(http, await administration.GetUsageAsync(actor.Principal, from, to, http.RequestAborted), summary => TicketFlowHttp.Ok(http, new AssistUsageSummaryDto(summary.From, summary.To,
                [.. summary.Totals.Select(total => new AssistUsageTotalDto(TicketFlowHttp.Token(total.Feature), total.Model, total.Requests, total.Failed, total.InputTokens, total.OutputTokens,
                    total.CacheReadInputTokens, total.CacheCreationInputTokens))]))))
            .WithName("GetAssistUsage").Produces<AssistUsageSummaryDto>();
        return group;
    }

    private static IResult Map<T, TDto>(HttpContext http, AssistResult<T> result, Func<T, TDto> project) => result switch
    {
        AssistSucceeded<T> succeeded => TicketFlowHttp.Ok(http, new AssistResultDto<TDto>(project(succeeded.Value), new AssistUsageDto(succeeded.Usage.InputTokens, succeeded.Usage.OutputTokens,
            succeeded.Usage.CacheReadInputTokens, succeeded.Usage.CacheCreationInputTokens, succeeded.Model, succeeded.UsageId, TicketFlowHttp.Token(succeeded.Recording)))),
        AssistRefused refused => Refused(http, refused),
        AssistProviderFailed failed => Failed(http, failed),
    };

    private static IResult Map<T>(HttpContext http, AssistAdminResult<T> result, Func<T, IResult> success) => result switch
    {
        AssistValue<T> value => success(value.Value),
        AssistRefused refused => Refused(http, refused),
    };

    private static IResult Refused(HttpContext http, AssistRefused refused)
    {
        RetryAfter(http, refused.RetryAfter);
        return refused.Reason switch
        {
            AssistRefusal.Forbidden => TicketFlowHttp.Refused(http, RefusalKind.Forbidden, refused.Errors),
            AssistRefusal.NotFound => TicketFlowHttp.Refused(http, RefusalKind.NotFound, refused.Errors),
            AssistRefusal.Invalid => TicketFlowHttp.Refused(http, RefusalKind.Invalid, refused.Errors),
            AssistRefusal.Disabled => TicketFlowHttp.Problem(StatusCodes.Status409Conflict, "assist-disabled", refused.Errors),
            AssistRefusal.QuotaExceeded => TicketFlowHttp.Problem(StatusCodes.Status429TooManyRequests, "quota-exceeded", refused.Errors),
            AssistRefusal.RateLimited => TicketFlowHttp.Problem(StatusCodes.Status429TooManyRequests, "rate-limited", refused.Errors),
            AssistRefusal.ModelRefused => TicketFlowHttp.Problem(StatusCodes.Status422UnprocessableEntity, "model-refused", refused.Errors),
            AssistRefusal.Conflict => refused.CurrentRevision is { } revision ? TicketFlowHttp.Conflict(http, revision, refused.Errors)
                : TicketFlowHttp.Problem(StatusCodes.Status409Conflict, "conflict", refused.Errors),
            _ => TicketFlowHttp.Refused(http, RefusalKind.Rejected, refused.Errors),
        };
    }

    private static IResult Failed(HttpContext http, AssistProviderFailed failed)
    {
        RetryAfter(http, failed.RetryAfter);
        var code = "provider-" + TicketFlowHttp.Token(failed.Failure);
        return failed.Failure is AssistProviderFailure.RateLimited or AssistProviderFailure.Overloaded or AssistProviderFailure.Timeout || failed.Retryable
            ? TicketFlowHttp.Problem(StatusCodes.Status503ServiceUnavailable, code, extensions: new Dictionary<string, object?> { ["retryable"] = failed.Retryable })
            : TicketFlowHttp.Problem(StatusCodes.Status502BadGateway, code, extensions: new Dictionary<string, object?> { ["retryable"] = failed.Retryable });
    }

    private static void RetryAfter(HttpContext http, TimeSpan? retryAfter)
    {
        if (retryAfter is { } delay && delay > TimeSpan.Zero)
            http.Response.Headers.RetryAfter = Math.Ceiling(delay.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

    private static AssistSettingsDto Settings(StoredAssistSettings stored) => new(Json.Parse(AssistJsonCodec.EncodeSettings(stored.Settings)), stored.Revision);
}
