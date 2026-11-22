using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class SlaEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/conversations/{conversationId}/sla", async (TicketFlowActor actor, string conversationId, [FromServices] ISlaService service, HttpContext http) =>
            Map(http, await service.GetStatusAsync(actor.Principal, new ConversationId(conversationId), http.RequestAborted), status => TicketFlowHttp.Ok(http, Dto(status))))
            .WithName("GetConversationSla").Produces<SlaStatusResponse>();
        group.MapGet("/sla/risk", async (TicketFlowActor actor, string? filter, string? inbox, int? limit, string? cursor, [FromServices] ISlaService service, HttpContext http) =>
        {
            var query = new SlaRiskQuery(filter is null ? SlaRiskFilter.AtRiskOrBreached : TicketFlowHttp.Parse<SlaRiskFilter>(filter, "filter"),
                inbox is null ? null : new InboxId(inbox), limit ?? SlaRiskQuery.DefaultLimit);
            return Map(http, await service.ListAsync(actor.Principal, query, cursor, http.RequestAborted),
                page => TicketFlowHttp.Ok(http, new PageResponse<SlaStatusResponse>([.. page.Items.Select(Dto)], page.NextCursor)));
        }).WithName("ListSlaRisk").Produces<PageResponse<SlaStatusResponse>>();

        group.MapGet("/sla/policies", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] ISlaPolicyService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Policy.Id.Value, Dto, limit, cursor))))
            .WithName("ListSlaPolicies").Produces<PageResponse<DefinitionResponse>>();
        group.MapGet("/sla/policies/{policyId}", async (TicketFlowActor actor, string policyId, [FromServices] ISlaPolicyService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new SlaPolicyId(policyId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetSlaPolicy").Produces<DefinitionResponse>();
        group.MapPost("/sla/policies", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] ISlaPolicyService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Policy(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"sla/policies/{stored.Policy.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateSlaPolicy").Produces<DefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/sla/policies/{policyId}", async (TicketFlowActor actor, string policyId, DefinitionRequest request, [FromServices] ISlaPolicyService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Policy(policyId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateSlaPolicy").Produces<DefinitionResponse>();
        group.MapDelete("/sla/policies/{policyId}", async (TicketFlowActor actor, string policyId, [FromServices] ISlaPolicyService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new SlaPolicyId(policyId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteSlaPolicy").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    private static IResult Map<T>(HttpContext http, SlaResult<T> result, Func<T, IResult> success) => result switch
    {
        SlaValue<T> value => success(value.Value),
        SlaNotFound => TicketFlowHttp.NotFound(),
        SlaConflict conflict => TicketFlowHttp.Conflict(http, conflict.CurrentRevision),
        SlaRefused refused => TicketFlowHttp.Refused(http, refused.Reason switch
        {
            SlaRefusal.Forbidden => RefusalKind.Forbidden,
            SlaRefusal.Invalid => RefusalKind.Invalid,
            _ => RefusalKind.Rejected,
        }, refused.Errors),
    };

    internal static SlaStatusResponse Dto(SlaStatusView view) => new(
        view.Conversation.Value,
        view.Inbox.Value,
        view.Policy?.Value,
        view.RuleName,
        TicketFlowHttp.Token(view.Basis),
        view.Paused,
        [.. view.Clocks.Select(clock => new SlaClockResponse(TicketFlowHttp.Token(clock.Metric), clock.Cycle, TicketFlowHttp.Token(clock.State), TicketFlowHttp.Token(clock.Outcome),
            Seconds(clock.Target), Seconds(clock.Elapsed), Seconds(clock.Remaining), clock.StartedAt, clock.DueAt, clock.BreachedAt, clock.CompletedAt, clock.AtRisk, clock.Breached))],
        view.AtRisk,
        view.Breached,
        view.DueAt,
        view.EvaluatedAt);

    private static long Seconds(TimeSpan value) => (long)Math.Floor(value.TotalSeconds);

    private static SlaPolicy Policy(string id, DefinitionRequest request) => SlaPolicyCodec.Deserialize(new SlaPolicyId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionResponse Dto(StoredSlaPolicy stored) => new(stored.Policy.Id.Value, Json.Parse(SlaPolicyCodec.Serialize(stored.Policy)), stored.Revision);
}
