using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Surveys;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class SurveyEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/surveys", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] ISurveyService service, HttpContext http) =>
            Map(http, await service.ListSurveysAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Survey.Id.Value, Dto, limit, cursor))))
            .WithName("ListSurveys").Produces<PageResponse<SurveyDefinitionResponse>>();
        group.MapGet("/surveys/{surveyId}", async (TicketFlowActor actor, string surveyId, [FromServices] ISurveyService service, HttpContext http) =>
            Map(http, await service.GetSurveyAsync(actor.Principal, new SurveyId(surveyId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetSurvey").Produces<SurveyDefinitionResponse>();
        group.MapPost("/surveys", async (TicketFlowActor actor, SurveyRequest request, [FromServices] ISurveyService service, HttpContext http) =>
            Map(http, await service.CreateSurveyAsync(actor.Principal, Survey(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"surveys/{stored.Survey.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateSurvey").Produces<SurveyDefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/surveys/{surveyId}", async (TicketFlowActor actor, string surveyId, SurveyRequest request, [FromServices] ISurveyService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateSurveyAsync(actor.Principal, Survey(surveyId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateSurvey").Produces<SurveyDefinitionResponse>();
        group.MapDelete("/surveys/{surveyId}", async (TicketFlowActor actor, string surveyId, [FromServices] ISurveyService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteSurveyAsync(actor.Principal, new SurveyId(surveyId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteSurvey").Produces(StatusCodes.Status204NoContent);
        group.MapGet("/surveys/responses", async (TicketFlowActor actor, DateTimeOffset from, DateTimeOffset to, string? survey, int? limit, string? cursor, [FromServices] ISurveyService service, HttpContext http) =>
        {
            SurveyResponseCursor? after = null;
            if (cursor is not null)
            {
                var parts = TicketFlowHttp.DecodeCursor(cursor, 2);
                if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var respondedAt)
                    || !Guid.TryParse(parts[1], out var invitation))
                    throw new ArgumentException("The cursor is invalid.", nameof(cursor));
                after = new SurveyResponseCursor(respondedAt, new SurveyInvitationId(invitation));
            }
            var query = new SurveyResponseQuery(from, to, survey is null ? null : new SurveyId(survey), limit ?? 50, after);
            return Map(http, await service.ListResponsesAsync(actor.Principal, query, http.RequestAborted), page => TicketFlowHttp.Ok(http, new PageResponse<SurveyAnswerResponse>(
                [.. page.Items.Select(Dto)],
                page.Next is { } next ? TicketFlowHttp.EncodeCursor(next.RespondedAt.ToString("O", CultureInfo.InvariantCulture), next.Invitation.Value.ToString("D")) : null)));
        }).WithName("ListSurveyResponses").Produces<PageResponse<SurveyAnswerResponse>>();
        group.MapGet("/surveys/aggregate", async (TicketFlowActor actor, string kind, DateTimeOffset from, DateTimeOffset to, string? bucket, string? groupBy, string? survey,
            [FromServices] ISurveyService service, HttpContext http) =>
        {
            var query = new SurveyAggregateQuery(TicketFlowHttp.Parse<SurveyKind>(kind, "kind"), from, to,
                bucket is null ? SurveyTimeBucket.None : TicketFlowHttp.Parse<SurveyTimeBucket>(bucket, "bucket"),
                groupBy is null ? SurveyDimension.None : TicketFlowHttp.Parse<SurveyDimension>(groupBy, "groupBy"), survey is null ? null : new SurveyId(survey));
            return Map(http, await service.AggregateAsync(actor.Principal, query, http.RequestAborted), result => TicketFlowHttp.Ok(http, new SurveyAggregateResponse(
                [.. result.Rows.Select(row => new SurveyAggregateRowResponse(TicketFlowHttp.Token(row.Kind), row.BucketStart, row.Group, row.Responses, row.Positive, row.Neutral, row.Negative, row.ScoreSum))],
                result.Truncated)));
        }).WithName("AggregateSurveys").Produces<SurveyAggregateResponse>();
        return group;
    }

    private static IResult Map<T>(HttpContext http, TicketResult<T> result, Func<T, IResult> success) => TicketFlowHttp.Map(http, result, success);

    private static SurveyDefinition Survey(string id, SurveyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Policy, nameof(request));
        var policy = new SurveyTriggerPolicy(
            Seconds(request.Policy.DelaySeconds, TimeSpan.Zero, SurveyTriggerPolicy.MaximumDelay, "delaySeconds"),
            Seconds(request.Policy.ContactCooldownSeconds, TimeSpan.Zero, SurveyTriggerPolicy.MaximumCooldown, "contactCooldownSeconds"),
            Seconds(request.Policy.InvitationLifetimeSeconds, SurveyTriggerPolicy.MinimumLifetime, SurveyTriggerPolicy.MaximumLifetime, "invitationLifetimeSeconds"),
            TicketFlowHttp.Parse<SurveyTrigger>(request.Policy.Trigger, "trigger"));
        return new SurveyDefinition(new SurveyId(id), request.Name, TicketFlowHttp.Parse<SurveyKind>(request.Kind, "kind"), new SurveyLocale(request.DefaultLocale),
            (request.Questions ?? throw new ArgumentException("Questions are required.", nameof(request)))
                .Select(question => new SurveyQuestion(new SurveyLocale(question.Locale), question.Text, question.CommentPrompt)),
            policy, request.InboxId is null ? null : new InboxId(request.InboxId), request.Enabled);
    }

    private static TimeSpan Seconds(long value, TimeSpan minimum, TimeSpan maximum, string name)
    {
        var minSeconds = (long)minimum.TotalSeconds;
        var maxSeconds = (long)maximum.TotalSeconds;
        if (value < minSeconds || value > maxSeconds)
            throw new ArgumentOutOfRangeException(name, $"'{name}' must be between {minSeconds} and {maxSeconds} seconds.");
        return TimeSpan.FromSeconds(value);
    }

    private static SurveyDefinitionResponse Dto(StoredSurvey stored)
    {
        var survey = stored.Survey;
        return new SurveyDefinitionResponse(survey.Id.Value, survey.Name, TicketFlowHttp.Token(survey.Kind), survey.DefaultLocale.Value,
            [.. survey.Questions.Select(question => new SurveyQuestionModel(question.Locale.Value, question.Text, question.CommentPrompt))],
            new SurveyPolicyModel((long)survey.Policy.Delay.TotalSeconds, (long)survey.Policy.ContactCooldown.TotalSeconds, (long)survey.Policy.InvitationLifetime.TotalSeconds,
                TicketFlowHttp.Token(survey.Policy.Trigger)), survey.Inbox?.Value, survey.Enabled, stored.Revision);
    }

    private static SurveyAnswerResponse Dto(SurveyResponseView view)
    {
        var response = view.Response;
        return new SurveyAnswerResponse(response.Invitation.Value, response.Survey.Value, TicketFlowHttp.Token(response.Kind), response.Score, view.Comment, view.CommentMasked,
            view.CommentRevealable, response.RespondedAt, response.Conversation.Value, response.Contact.Value, response.Inbox?.Value, response.Team?.Value, response.Agent?.Value);
    }
}
