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
        group.MapGet("/automation/rules", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IAutomationRuleService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Id.Value, Rule, limit, cursor))))
            .WithName("ListAutomationRules").Produces<PageResponse<DefinitionResponse>>();
        group.MapGet("/automation/rules/{ruleId}", async (TicketFlowActor actor, string ruleId, [FromServices] IAutomationRuleService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new AutomationRuleId(ruleId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Rule(stored), stored.Revision)))
            .WithName("GetAutomationRule").Produces<DefinitionResponse>();
        group.MapPost("/automation/rules", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] IAutomationRuleService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Rule(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"automation/rules/{stored.Id.Value}", Rule(stored), stored.Revision)))
            .WithName("CreateAutomationRule").Produces<DefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/automation/rules/{ruleId}", async (TicketFlowActor actor, string ruleId, DefinitionRequest request, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Rule(ruleId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Rule(stored), stored.Revision));
        }).WithName("UpdateAutomationRule").Produces<DefinitionResponse>();
        group.MapDelete("/automation/rules/{ruleId}", async (TicketFlowActor actor, string ruleId, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new AutomationRuleId(ruleId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteAutomationRule").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/automation/dry-run", async (TicketFlowActor actor, AutomationPreviewRequest request, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            AutomationRule? draft = request.Draft is { ValueKind: not System.Text.Json.JsonValueKind.Null } definition
                ? AutomationCodec.DeserializeRule(new AutomationRuleId("draft"), definition.GetRawText())
                : null;
            var dryRun = new AutomationDryRunRequest(new ConversationId(request.ConversationId), Event(request.Event), request.Rules?.Select(rule => new AutomationRuleId(rule)).ToList(),
                draft, request.MessageBody);
            return Map(http, await service.DryRunAsync(actor.Principal, dryRun, http.RequestAborted), result => TicketFlowHttp.Ok(http, new AutomationDryRunResponse(
                result.Conversation.Value, [.. result.Rules.Select(Preview)], result.Stopped?.Value, result.EvaluatedAt)));
        }).WithName("DryRunAutomation").Produces<AutomationDryRunResponse>();
        group.MapPost("/automation/replay", async (TicketFlowActor actor, AutomationReplayWindowRequest request, [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            var replay = new AutomationReplayRequest(new ConversationId(request.ConversationId), request.From, request.To, request.Rules?.Select(rule => new AutomationRuleId(rule)).ToList());
            return Map(http, await service.ReplayAsync(actor.Principal, replay, http.RequestAborted), result => TicketFlowHttp.Ok(http, new AutomationReplayResponse(result.Conversation.Value,
                [.. result.Items.Select(item => new AutomationReplayItemResponse(item.Sequence, item.OccurredAt, TicketFlowHttp.Token(item.Event.Kind),
                    item.Cause is { } cause ? new AutomationCauseResponse(cause.Execution, cause.Rule.Value, cause.Depth) : null,
                    [.. item.Previews.Select(Preview)], [.. item.Executions.Select(Execution)]))], result.Truncated)));
        }).WithName("ReplayAutomation").Produces<AutomationReplayResponse>();
        group.MapGet("/automation/executions", async (TicketFlowActor actor, string? rule, string? conversation, DateTimeOffset? from, DateTimeOffset? to, int? limit, string? cursor,
            [FromServices] IAutomationRuleService service, HttpContext http) =>
        {
            AutomationExecutionCursor? after = null;
            if (cursor is not null)
            {
                var parts = TicketFlowHttp.DecodeCursor(cursor, 2);
                after = new AutomationExecutionCursor(DateTimeOffset.Parse(parts[0], CultureInfo.InvariantCulture), Guid.Parse(parts[1]));
            }
            var query = new AutomationExecutionQuery(rule is null ? null : new AutomationRuleId(rule), conversation is null ? null : new ConversationId(conversation), from, to,
                limit ?? AutomationExecutionQuery.DefaultLimit, after);
            return Map(http, await service.ListExecutionsAsync(actor.Principal, query, http.RequestAborted), page => TicketFlowHttp.Ok(http, new PageResponse<AutomationExecutionResponse>(
                [.. page.Executions.Select(Execution)],
                page.Next is { } next ? TicketFlowHttp.EncodeCursor(next.StartedAt.ToString("O", CultureInfo.InvariantCulture), next.Execution.ToString("D")) : null)));
        }).WithName("ListAutomationExecutions").Produces<PageResponse<AutomationExecutionResponse>>();

        group.MapGet("/macros", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Id.Value, Macro, limit, cursor))))
            .WithName("ListMacros").Produces<PageResponse<DefinitionResponse>>();
        group.MapGet("/macros/{macroId}", async (TicketFlowActor actor, string macroId, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new MacroId(macroId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Macro(stored), stored.Revision)))
            .WithName("GetMacro").Produces<DefinitionResponse>();
        group.MapPost("/macros", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Macro(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"macros/{stored.Id.Value}", Macro(stored), stored.Revision)))
            .WithName("CreateMacro").Produces<DefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/macros/{macroId}", async (TicketFlowActor actor, string macroId, DefinitionRequest request, [FromServices] IMacroService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Macro(macroId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Macro(stored), stored.Revision));
        }).WithName("UpdateMacro").Produces<DefinitionResponse>();
        group.MapDelete("/macros/{macroId}", async (TicketFlowActor actor, string macroId, [FromServices] IMacroService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new MacroId(macroId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteMacro").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/macros/{macroId}/run", async (TicketFlowActor actor, string macroId, MacroRunRequest request, [FromServices] IMacroService service, HttpContext http) =>
            Map(http, await service.RunAsync(actor.Principal, new MacroId(macroId), new ConversationId(request.ConversationId), http.RequestAborted), result => TicketFlowHttp.Ok(http,
                new MacroRunResponse(result.Macro.Value, result.Conversation.Value, result.Run, [.. result.Outcomes.Select(Outcome)], result.Completed))))
            .WithName("RunMacro").Produces<MacroRunResponse>();

        group.MapGet("/canned-responses", async (TicketFlowActor actor, string? prefix, string? cursor, int? limit, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, new CannedResponseQuery(prefix, cursor is null ? null : TicketFlowHttp.DecodeCursor(cursor, 1)[0], Math.Min(TicketFlowHttp.PageSize(limit), CannedResponseQuery.MaximumLimit)), http.RequestAborted),
                page => TicketFlowHttp.Ok(http, new PageResponse<DefinitionResponse>([.. page.Responses.Select(Canned)], page.NextAfterShortcut is { } next ? TicketFlowHttp.EncodeCursor(next) : null))))
            .WithName("ListCannedResponses").Produces<PageResponse<DefinitionResponse>>();
        group.MapGet("/canned-responses/by-shortcut/{shortcut}", async (TicketFlowActor actor, string shortcut, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.FindByShortcutAsync(actor.Principal, shortcut, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Canned(stored), stored.Revision)))
            .WithName("FindCannedResponse").Produces<DefinitionResponse>();
        group.MapGet("/canned-responses/{responseId}", async (TicketFlowActor actor, string responseId, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new CannedResponseId(responseId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Canned(stored), stored.Revision)))
            .WithName("GetCannedResponse").Produces<DefinitionResponse>();
        group.MapPost("/canned-responses", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Canned(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"canned-responses/{stored.Id.Value}", Canned(stored), stored.Revision)))
            .WithName("CreateCannedResponse").Produces<DefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/canned-responses/{responseId}", async (TicketFlowActor actor, string responseId, DefinitionRequest request, [FromServices] ICannedResponseService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Canned(responseId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Canned(stored), stored.Revision));
        }).WithName("UpdateCannedResponse").Produces<DefinitionResponse>();
        group.MapDelete("/canned-responses/{responseId}", async (TicketFlowActor actor, string responseId, [FromServices] ICannedResponseService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new CannedResponseId(responseId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteCannedResponse").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/canned-responses/{responseId}/render", async (TicketFlowActor actor, string responseId, CannedResponseRenderRequest request, [FromServices] ICannedResponseService service, HttpContext http) =>
            Map(http, await service.RenderAsync(actor.Principal, new CannedResponseId(responseId), new ConversationId(request.ConversationId), request.Locale, http.RequestAborted),
                rendered => TicketFlowHttp.Ok(http, new CannedResponseRenderResponse(rendered.Response.Value, rendered.Locale, TicketFlowHttp.Token(rendered.ContentType), rendered.Body))))
            .WithName("RenderCannedResponse").Produces<CannedResponseRenderResponse>();
        return group;
    }

    private static IResult Map<T>(HttpContext http, TicketResult<T> result, Func<T, IResult> success) => TicketFlowHttp.Map(http, result, success);

    private static AutomationEvent Event(AutomationEventInput? dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
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

    private static AutomationRulePreviewResponse Preview(AutomationRulePreview preview)
    {
        if (preview.Actions.Count == 0)
            return new(preview.Rule.Value, preview.Name, TicketFlowHttp.Token(preview.Match), []);
        var rawActions = new AutomationAction[preview.Actions.Count];
        for (var i = 0; i < preview.Actions.Count; i++)
            rawActions[i] = preview.Actions[i].Action;
        using var document = System.Text.Json.JsonDocument.Parse(AutomationCodec.SerializeActions(rawActions));
        var array = document.RootElement.GetProperty("actions");
        var outputs = new AutomationActionPreviewResponse[preview.Actions.Count];
        for (var i = 0; i < preview.Actions.Count; i++)
        {
            var action = preview.Actions[i];
            outputs[i] = new(action.Index, array[i].Clone(), action.RenderedText, action.Note);
        }
        return new(preview.Rule.Value, preview.Name, TicketFlowHttp.Token(preview.Match), outputs);
    }

    internal static AutomationActionOutcomeResponse Outcome(AutomationActionOutcome outcome) => outcome switch
    {
        AutomationActionApplied applied => new AutomationActionOutcomeResponse("applied", Sequences: applied.Sequences),
        AutomationActionSkipped skipped => new AutomationActionOutcomeResponse("skipped", Reason: skipped.Reason),
        AutomationActionRefused refused => new AutomationActionOutcomeResponse("refused", Reason: TicketFlowHttp.Token(refused.Reason)),
        AutomationActionFailed failed => new AutomationActionOutcomeResponse("failed", Reason: failed.Code),
    };

    private static AutomationExecutionResponse Execution(StoredAutomationExecution stored)
    {
        var execution = stored.Execution;
        return new AutomationExecutionResponse(execution.Id, execution.Rule.Value, execution.Conversation?.Value, execution.TriggerKey, TicketFlowHttp.Token(execution.EventKind),
            execution.Depth, TicketFlowHttp.Token(execution.Status), TicketFlowHttp.Token(execution.Suppression), execution.StartedAt, execution.CompletedAt, execution.ActionCount,
            [.. execution.Outcomes.Select(Outcome)]);
    }

    private static AutomationRule Rule(string id, DefinitionRequest request) => AutomationCodec.DeserializeRule(new AutomationRuleId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionResponse Rule(StoredAutomationRule stored) => new(stored.Id.Value, Json.Parse(AutomationCodec.SerializeRule(stored.Rule)), stored.Revision);

    private static Macro Macro(string id, DefinitionRequest request) => AutomationCodec.DeserializeMacro(new MacroId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionResponse Macro(StoredMacro stored) => new(stored.Id.Value, Json.Parse(AutomationCodec.SerializeMacro(stored.Macro)), stored.Revision);

    private static CannedResponse Canned(string id, DefinitionRequest request) =>
        AutomationCodec.DeserializeCannedResponse(new CannedResponseId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionResponse Canned(StoredCannedResponse stored) => new(stored.Id.Value, Json.Parse(AutomationCodec.SerializeCannedResponse(stored.Response)), stored.Revision);
}
