using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Automation;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Sla;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class AutomationEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/automation/rules", async (TicketFlowActor actor, [FromServices] IAutomationRuleService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, list.Select(Rule).ToList())))
            .WithName("ListAutomationRules").Produces<List<DefinitionDto>>();
        group.MapGet("/automation/rules/{ruleId}", async (TicketFlowActor actor, string ruleId, [FromServices] IAutomationRuleService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new AutomationRuleId(ruleId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Rule(stored), stored.Revision)))
            .WithName("GetAutomationRule").Produces<DefinitionDto>();
        group.MapPost("/automation/rules", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] IAutomationRuleService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Rule(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"automation/rules/{stored.Id.Value}", Rule(stored), stored.Revision)))
            .WithName("CreateAutomationRule").Produces<DefinitionDto>(StatusCodes.Status201Created);
        group.MapPut("/automation/rules/{ruleId}", async (TicketFlowActor actor, string ruleId, DefinitionRequest request, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Rule(ruleId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Rule(stored), stored.Revision));
        }).WithName("UpdateAutomationRule").Produces<DefinitionDto>();
        group.MapDelete("/automation/rules/{ruleId}", async (TicketFlowActor actor, string ruleId, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new AutomationRuleId(ruleId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteAutomationRule").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/automation/dry-run", async (TicketFlowActor actor, AutomationDryRunRequestDto request, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            AutomationRule? draft = request.Draft is { ValueKind: not System.Text.Json.JsonValueKind.Null } definition
                ? AutomationCodec.DeserializeRule(new AutomationRuleId("draft"), definition.GetRawText())
                : null;
            var dryRun = new AutomationDryRunRequest(new ConversationId(request.ConversationId), Event(request.Event), request.Rules?.Select(rule => new AutomationRuleId(rule)).ToList(),
                draft, request.MessageBody);
            return Map(http, await service.DryRunAsync(actor.Principal, dryRun, http.RequestAborted), result => TicketFlowHttp.Ok(http, new AutomationDryRunResultDto(
                result.Conversation.Value, [.. result.Rules.Select(Preview)], result.Stopped?.Value, result.EvaluatedAt)));
        }).WithName("DryRunAutomation").Produces<AutomationDryRunResultDto>();
        group.MapPost("/automation/replay", async (TicketFlowActor actor, AutomationReplayRequestDto request, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            var replay = new AutomationReplayRequest(new ConversationId(request.ConversationId), request.From, request.To, request.Rules?.Select(rule => new AutomationRuleId(rule)).ToList());
            return Map(http, await service.ReplayAsync(actor.Principal, replay, http.RequestAborted), result => TicketFlowHttp.Ok(http, new AutomationReplayResultDto(result.Conversation.Value,
                [.. result.Items.Select(item => new AutomationReplayItemDto(item.Sequence, item.OccurredAt, TicketFlowHttp.Token(item.Event.Kind),
                    item.Cause is { } cause ? new AutomationCauseDto(cause.Execution, cause.Rule.Value, cause.Depth) : null,
                    [.. item.Previews.Select(Preview)], [.. item.Executions.Select(Execution)]))], result.Truncated)));
        }).WithName("ReplayAutomation").Produces<AutomationReplayResultDto>();
        group.MapGet("/automation/executions", async (TicketFlowActor actor, string? rule, string? conversation, DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor,
            IAutomationRuleService service, HttpContext http) =>
        {
            AutomationExecutionCursor? after = null;
            if (cursor is not null)
            {
                var parts = TicketFlowHttp.DecodeCursor(cursor, 2);
                after = new AutomationExecutionCursor(DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture), Guid.Parse(parts[1]));
            }
            var query = new AutomationExecutionQuery(rule is null ? null : new AutomationRuleId(rule), conversation is null ? null : new ConversationId(conversation), from, to,
                limit ?? AutomationExecutionQuery.DefaultLimit, after);
            return Map(http, await service.ListExecutionsAsync(actor.Principal, query, http.RequestAborted), page => TicketFlowHttp.Ok(http, new PageDto<AutomationExecutionDto>(
                [.. page.Executions.Select(Execution)],
                page.Next is { } next ? TicketFlowHttp.EncodeCursor(next.StartedAt.ToString("O", CultureInfo.InvariantCulture), next.Execution.ToString("D")) : null)));
        }).WithName("ListAutomationExecutions").Produces<PageDto<AutomationExecutionDto>>();

        group.MapGet("/macros", async (TicketFlowActor actor, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, list.Select(Macro).ToList())))
            .WithName("ListMacros").Produces<List<DefinitionDto>>();
        group.MapGet("/macros/{macroId}", async (TicketFlowActor actor, string macroId, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new MacroId(macroId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Macro(stored), stored.Revision)))
            .WithName("GetMacro").Produces<DefinitionDto>();
        group.MapPost("/macros", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Macro(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"macros/{stored.Id.Value}", Macro(stored), stored.Revision)))
            .WithName("CreateMacro").Produces<DefinitionDto>(StatusCodes.Status201Created);
        group.MapPut("/macros/{macroId}", async (TicketFlowActor actor, string macroId, DefinitionRequest request, [FromServices] IMacroService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Macro(macroId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Macro(stored), stored.Revision));
        }).WithName("UpdateMacro").Produces<DefinitionDto>();
        group.MapDelete("/macros/{macroId}", async (TicketFlowActor actor, string macroId, [FromServices] IMacroService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new MacroId(macroId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteMacro").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/macros/{macroId}/run", async (TicketFlowActor actor, string macroId, MacroRunRequest request, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.RunAsync(actor.Principal, new MacroId(macroId), new ConversationId(request.ConversationId), http.RequestAborted), result => TicketFlowHttp.Ok(http,
                new MacroRunResultDto(result.Macro.Value, result.Conversation.Value, result.Run, [.. result.Outcomes.Select(Outcome)], result.Completed))))
            .WithName("RunMacro").Produces<MacroRunResultDto>();

        group.MapGet("/canned-responses", async (TicketFlowActor actor, string? prefix, string? after, int? limit, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, new CannedResponseQuery(prefix, after, limit ?? 25), http.RequestAborted),
                page => TicketFlowHttp.Ok(http, new PageDto<DefinitionDto>([.. page.Responses.Select(Canned)], page.NextAfterShortcut))))
            .WithName("ListCannedResponses").Produces<PageDto<DefinitionDto>>();
        group.MapGet("/canned-responses/by-shortcut/{shortcut}", async (TicketFlowActor actor, string shortcut, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.FindByShortcutAsync(actor.Principal, shortcut, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Canned(stored), stored.Revision)))
            .WithName("FindCannedResponse").Produces<DefinitionDto>();
        group.MapGet("/canned-responses/{responseId}", async (TicketFlowActor actor, string responseId, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new CannedResponseId(responseId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Canned(stored), stored.Revision)))
            .WithName("GetCannedResponse").Produces<DefinitionDto>();
        group.MapPost("/canned-responses", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Canned(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"canned-responses/{stored.Id.Value}", Canned(stored), stored.Revision)))
            .WithName("CreateCannedResponse").Produces<DefinitionDto>(StatusCodes.Status201Created);
        group.MapPut("/canned-responses/{responseId}", async (TicketFlowActor actor, string responseId, DefinitionRequest request, [FromServices] ICannedResponseService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Canned(responseId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Canned(stored), stored.Revision));
        }).WithName("UpdateCannedResponse").Produces<DefinitionDto>();
        group.MapDelete("/canned-responses/{responseId}", async (TicketFlowActor actor, string responseId, [FromServices] ICannedResponseService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new CannedResponseId(responseId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteCannedResponse").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/canned-responses/{responseId}/render", async (TicketFlowActor actor, string responseId, CannedResponseRenderRequest request, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.RenderAsync(actor.Principal, new CannedResponseId(responseId), new ConversationId(request.ConversationId), request.Locale, http.RequestAborted),
                rendered => TicketFlowHttp.Ok(http, new RenderedCannedResponseDto(rendered.Response.Value, rendered.Locale, TicketFlowHttp.Token(rendered.ContentType), rendered.Body))))
            .WithName("RenderCannedResponse").Produces<RenderedCannedResponseDto>();
        return group;
    }

    private static IResult Map<T>(HttpContext http, AutomationResult<T> result, Func<T, IResult> success) => result switch
    {
        AutomationValue<T> value => success(value.Value),
        AutomationNotFound => TicketFlowHttp.NotFound(),
        AutomationConflict conflict => TicketFlowHttp.Conflict(http, conflict.CurrentRevision),
        AutomationRefused refused => TicketFlowHttp.Refused(http, refused.Reason switch
        {
            AutomationRefusal.Forbidden => RefusalKind.Forbidden,
            AutomationRefusal.Invalid => RefusalKind.Invalid,
            _ => RefusalKind.Rejected,
        }, refused.Errors),
    };

    private static AutomationEvent Event(AutomationEventDto? dto)
    {
        ArgumentNullException.ThrowIfNull(dto, "event");
        return TicketFlowHttp.Parse<AutomationEventKind>(dto.Kind, "event.kind") switch
        {
            AutomationEventKind.ConversationCreated => AutomationEvent.ConversationCreated(),
            AutomationEventKind.MessageReceived => AutomationEvent.MessageReceived(),
            AutomationEventKind.MessageSent => AutomationEvent.MessageSent(),
            AutomationEventKind.ConversationUpdated => AutomationEvent.ConversationUpdated(TicketFlowHttp.Parse<ConversationUpdateField>(dto.Field, "event.field")),
            AutomationEventKind.StatusChanged => AutomationEvent.StatusChanged(TicketFlowHttp.Parse<ConversationStatusKind>(dto.Status, "event.status")),
            AutomationEventKind.AssignmentChanged => AutomationEvent.AssignmentChanged(),
            AutomationEventKind.SlaEvent => AutomationEvent.SlaEvent(TicketFlowHttp.Parse<SlaEventKind>(dto.Sla, "event.sla"), TicketFlowHttp.Parse<SlaMetric>(dto.Metric, "event.metric"), []),
            AutomationEventKind.Scheduled => AutomationEvent.Scheduled(),
            _ => AutomationEvent.ContactUpdated(),
        };
    }

    private static AutomationRulePreviewDto Preview(AutomationRulePreview preview) => new(preview.Rule.Value, preview.Name, TicketFlowHttp.Token(preview.Match),
        [.. preview.Actions.Select(action => new AutomationActionPreviewDto(action.Index, Action(action.Action), action.RenderedText, action.Note))]);

    private static System.Text.Json.JsonElement Action(AutomationAction action) => Json.Parse(AutomationCodec.SerializeActions([action])).GetProperty("actions")[0];

    internal static AutomationActionOutcomeDto Outcome(AutomationActionOutcome outcome) => outcome switch
    {
        AutomationActionApplied applied => new AutomationActionOutcomeDto("applied", Sequences: applied.Sequences),
        AutomationActionSkipped skipped => new AutomationActionOutcomeDto("skipped", Reason: skipped.Reason),
        AutomationActionRefused refused => new AutomationActionOutcomeDto("refused", Reason: TicketFlowHttp.Token(refused.Reason)),
        AutomationActionFailed failed => new AutomationActionOutcomeDto("failed", Reason: failed.Code),
    };

    private static AutomationExecutionDto Execution(StoredAutomationExecution stored)
    {
        var execution = stored.Execution;
        return new AutomationExecutionDto(execution.Id, execution.Rule.Value, execution.Conversation?.Value, execution.TriggerKey, TicketFlowHttp.Token(execution.EventKind),
            execution.Depth, TicketFlowHttp.Token(execution.Status), TicketFlowHttp.Token(execution.Suppression), execution.StartedAt, execution.CompletedAt, execution.ActionCount,
            [.. execution.Outcomes.Select(Outcome)]);
    }

    private static AutomationRule Rule(string id, DefinitionRequest request) => AutomationCodec.DeserializeRule(new AutomationRuleId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionDto Rule(StoredAutomationRule stored) => new(stored.Id.Value, Json.Parse(AutomationCodec.SerializeRule(stored.Rule)), stored.Revision);

    private static Macro Macro(string id, DefinitionRequest request) => AutomationCodec.DeserializeMacro(new MacroId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionDto Macro(StoredMacro stored) => new(stored.Id.Value, Json.Parse(AutomationCodec.SerializeMacro(stored.Macro)), stored.Revision);

    private static CannedResponse Canned(string id, DefinitionRequest request) =>
        AutomationCodec.DeserializeCannedResponse(new CannedResponseId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionDto Canned(StoredCannedResponse stored) => new(stored.Id.Value, Json.Parse(AutomationCodec.SerializeCannedResponse(stored.Response)), stored.Revision);
}
