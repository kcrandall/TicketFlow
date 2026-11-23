using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Routing;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class RoutingEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/routing/agents", async (TicketFlowActor actor, string? cursor, int? limit, [FromServices] IAgentAvailabilityService service, [FromServices] TimeProvider time, HttpContext http) =>
        {
            var size = TicketFlowHttp.PageSize(limit);
            return Map(http, await service.ListAsync(actor.Principal, cursor is null ? null : new UserId(TicketFlowHttp.DecodeCursor(cursor, 1)[0]), size + 1, http.RequestAborted), profiles =>
            {
                var now = time.GetUtcNow();
                var count = Math.Min(profiles.Count, size);
                var items = new AgentProfileResponse[count];
                for (var i = 0; i < count; i++)
                    items[i] = Dto(profiles[i], now);
                return TicketFlowHttp.Ok(http, new PageResponse<AgentProfileResponse>(items, profiles.Count > size ? TicketFlowHttp.EncodeCursor(profiles[count - 1].Agent.Value) : null));
            });
        }).WithName("ListAgents").Produces<PageResponse<AgentProfileResponse>>();
        group.MapGet("/routing/agents/me", async (TicketFlowActor actor, [FromServices] IAgentAvailabilityService service, [FromServices] TimeProvider time, HttpContext http) =>
            actor.UserId is { } me
                ? Map(http, await service.GetAsync(actor.Principal, me, http.RequestAborted), profile => TicketFlowHttp.Ok(http, Dto(profile, time), profile.Revision))
                : TicketFlowHttp.Problem(StatusCodes.Status422UnprocessableEntity, "user-required"))
            .WithName("GetMyAgentProfile").Produces<AgentProfileResponse>();
        group.MapPut("/routing/agents/me/status", async (TicketFlowActor actor, AgentStatusRequest request, [FromServices] IAgentAvailabilityService service, [FromServices] TimeProvider time, HttpContext http) =>
            actor.UserId is { } me
                ? Map(http, await service.SetStatusAsync(actor.Principal, me, TicketFlowHttp.Parse<AgentStatus>(request.Status, "status"), http.RequestAborted),
                    profile => TicketFlowHttp.Ok(http, Dto(profile, time), profile.Revision))
                : TicketFlowHttp.Problem(StatusCodes.Status422UnprocessableEntity, "user-required"))
            .WithName("SetMyAgentStatus").Produces<AgentProfileResponse>();
        group.MapPost("/routing/agents/me/heartbeat", async (TicketFlowActor actor, [FromServices] IAgentAvailabilityService service, [FromServices] TimeProvider time, HttpContext http) =>
            Map(http, await service.HeartbeatAsync(actor.Principal, http.RequestAborted), profile => TicketFlowHttp.Ok(http, Dto(profile, time), profile.Revision)))
            .WithName("AgentHeartbeat").Produces<AgentProfileResponse>();
        group.MapGet("/routing/agents/{userId}", async (TicketFlowActor actor, string userId, [FromServices] IAgentAvailabilityService service, [FromServices] TimeProvider time, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new UserId(userId), http.RequestAborted), profile => TicketFlowHttp.Ok(http, Dto(profile, time), profile.Revision)))
            .WithName("GetAgentProfile").Produces<AgentProfileResponse>();
        group.MapPut("/routing/agents/{userId}/status", async (TicketFlowActor actor, string userId, AgentStatusRequest request, [FromServices] IAgentAvailabilityService service, [FromServices] TimeProvider time, HttpContext http) =>
            Map(http, await service.SetStatusAsync(actor.Principal, new UserId(userId), TicketFlowHttp.Parse<AgentStatus>(request.Status, "status"), http.RequestAborted),
                profile => TicketFlowHttp.Ok(http, Dto(profile, time), profile.Revision)))
            .WithName("SetAgentStatus").Produces<AgentProfileResponse>();
        group.MapPut("/routing/agents/{userId}/settings", async (TicketFlowActor actor, string userId, AgentSettingsRequest request, [FromServices] IAgentAvailabilityService service, [FromServices] TimeProvider time, HttpContext http) =>
        {
            // Replacing requires If-Match; creating requires If-None-Match: * (never an unfenced overwrite).
            long? revision = null;
            if (!TicketFlowHttp.IsCreateOnly(http))
            {
                if (!TicketFlowHttp.TryRequireRevision(http, out var expected, out var failure))
                    return failure;
                revision = expected;
            }
            var settings = new AgentRoutingSettings(new UserId(userId), request.DefaultCapacity,
                request.Capacities?.Select(pair => new KeyValuePair<ChannelKind, int>(new ChannelKind(pair.Key), pair.Value)),
                request.Skills?.Select(skill => new AgentSkill(skill.Name, skill.Level)));
            return Map(http, await service.ConfigureAsync(actor.Principal, settings, revision, http.RequestAborted), profile => TicketFlowHttp.Ok(http, Dto(profile, time), profile.Revision));
        }).WithName("ConfigureAgent").Produces<AgentProfileResponse>().ProducesProblem(StatusCodes.Status412PreconditionFailed);

        group.MapGet("/routing/queue", async (TicketFlowActor actor, string? inbox, string? team, int? limit, string? cursor, [FromServices] IRoutingQueueService service, HttpContext http) =>
        {
            RoutingQueueCursor? after = null;
            if (cursor is not null)
            {
                var parts = TicketFlowHttp.DecodeCursor(cursor, 2);
                after = new RoutingQueueCursor(DateTimeOffset.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), new ConversationId(parts[1]));
            }
            var query = new RoutingQueueQuery(inbox is null ? null : new InboxId(inbox), team is null ? null : new TeamId(team), limit ?? 25, after);
            return Map(http, await service.ListAsync(actor.Principal, query, http.RequestAborted), page => TicketFlowHttp.Ok(http, new PageResponse<QueueItemResponse>(
                [.. page.Items.Select(item => new QueueItemResponse(item.Conversation.Value, item.Inbox.Value, item.TargetInbox.Value, item.Team?.Value, item.WaitingSince, item.QueuedAt,
                    item.OverflowStage, item.WaitExceeded))],
                page.Next is { } next ? TicketFlowHttp.EncodeCursor(next.WaitingSince.ToString("O", System.Globalization.CultureInfo.InvariantCulture), next.Conversation.Value) : null)));
        }).WithName("ListQueue").Produces<PageResponse<QueueItemResponse>>();

        group.MapGet("/conversations/{conversationId}/presence", async (TicketFlowActor actor, string conversationId, [FromServices] IConversationCollisionService service, HttpContext http) =>
            Map(http, await service.ListActivityAsync(actor.Principal, new ConversationId(conversationId), http.RequestAborted), list => TicketFlowHttp.Ok(http, list.Select(Dto).ToList())))
            .WithName("ListPresence").Produces<List<PresenceResponse>>();
        group.MapPut("/conversations/{conversationId}/presence", async (TicketFlowActor actor, string conversationId, PresenceRequest request, [FromServices] IConversationCollisionService service, HttpContext http) =>
            Map(http, await service.ReportActivityAsync(actor.Principal, new ConversationId(conversationId), TicketFlowHttp.Parse<ConversationActivity>(request.Activity, "activity"), http.RequestAborted),
                presence => TicketFlowHttp.Ok(http, Dto(presence))))
            .WithName("ReportPresence").Produces<PresenceResponse>();
        group.MapDelete("/conversations/{conversationId}/presence", async (TicketFlowActor actor, string conversationId, [FromServices] IConversationCollisionService service, HttpContext http) =>
            Map(http, await service.LeaveAsync(actor.Principal, new ConversationId(conversationId), http.RequestAborted), _ => TypedResults.NoContent()))
            .WithName("LeaveConversation").Produces(StatusCodes.Status204NoContent);
        group.MapGet("/conversations/{conversationId}/reply-lock", async (TicketFlowActor actor, string conversationId, [FromServices] IConversationCollisionService service, HttpContext http) =>
            Map(http, await service.GetReplyLockAsync(actor.Principal, new ConversationId(conversationId), http.RequestAborted),
                current => current is null ? TypedResults.NoContent() : TicketFlowHttp.Ok(http, Dto(current), current.Fence)))
            .WithName("GetReplyLock").Produces<ReplyLockResponse>().Produces(StatusCodes.Status204NoContent);
        group.MapPost("/conversations/{conversationId}/reply-lock", async (TicketFlowActor actor, string conversationId, ReplyLockRequest? request, [FromServices] IConversationCollisionService service, HttpContext http) =>
        {
            TimeSpan? duration = request?.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
            return Map(http, await service.AcquireReplyLockAsync(actor.Principal, new ConversationId(conversationId), duration, http.RequestAborted), result => result switch
            {
                ReplyLockAcquired acquired => TicketFlowHttp.Ok(http, new ReplyLockAcquireResponse(true, acquired.Renewed, Dto(acquired.Lock)), acquired.Lock.Fence),
                ReplyLockHeld held => TicketFlowHttp.Problem(StatusCodes.Status409Conflict, "reply-lock-held", extensions: new Dictionary<string, object?> { ["lock"] = Dto(held.Current) }),
            });
        }).WithName("AcquireReplyLock").Produces<ReplyLockAcquireResponse>();
        group.MapDelete("/conversations/{conversationId}/reply-lock", async (TicketFlowActor actor, string conversationId, [FromServices] IConversationCollisionService service, HttpContext http) =>
        {
            // The lock's fence is its entity tag (returned as ETag on acquire); release is fenced with If-Match.
            if (!TicketFlowHttp.TryRequireRevision(http, out var fence, out var failure))
                return failure;
            return Map(http, await service.ReleaseReplyLockAsync(actor.Principal, new ConversationId(conversationId), fence, http.RequestAborted),
                released => released ? TypedResults.NoContent() : TicketFlowHttp.Problem(StatusCodes.Status412PreconditionFailed, "reply-lock-not-held"));
        }).WithName("ReleaseReplyLock").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/routing/policies", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IRoutingPolicyService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Policy.Id.Value, Dto, limit, cursor))))
            .WithName("ListRoutingPolicies").Produces<PageResponse<DefinitionResponse>>();
        group.MapGet("/routing/policies/{policyId}", async (TicketFlowActor actor, string policyId, [FromServices] IRoutingPolicyService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new RoutingPolicyId(policyId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetRoutingPolicy").Produces<DefinitionResponse>();
        group.MapPost("/routing/policies", async (TicketFlowActor actor, DefinitionRequest request, [FromServices] IRoutingPolicyService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Policy(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"routing/policies/{stored.Policy.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateRoutingPolicy").Produces<DefinitionResponse>(StatusCodes.Status201Created);
        group.MapPut("/routing/policies/{policyId}", async (TicketFlowActor actor, string policyId, DefinitionRequest request, [FromServices] IRoutingPolicyService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Policy(policyId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateRoutingPolicy").Produces<DefinitionResponse>();
        group.MapDelete("/routing/policies/{policyId}", async (TicketFlowActor actor, string policyId, [FromServices] IRoutingPolicyService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new RoutingPolicyId(policyId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteRoutingPolicy").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    private static IResult Map<T>(HttpContext http, TicketResult<T> result, Func<T, IResult> success) => TicketFlowHttp.Map(http, result, success);

    private static AgentProfileResponse Dto(StoredAgentProfile profile, TimeProvider time) => Dto(profile, time.GetUtcNow());

    private static AgentProfileResponse Dto(StoredAgentProfile profile, DateTimeOffset now) => new(
        profile.Agent.Value,
        TicketFlowHttp.Token(profile.Presence.EffectiveStatus(now)),
        profile.Presence.ChangedAt,
        profile.Presence.LastHeartbeatAt,
        profile.Presence.ExpiresAt,
        profile.Settings.DefaultCapacity,
        profile.Settings.Capacities.ToDictionary(pair => pair.Key.Value, pair => pair.Value, StringComparer.Ordinal),
        [.. profile.Settings.Skills.Select(skill => new AgentSkillModel(skill.Name, skill.Level))],
        profile.ReassignmentPending,
        profile.Revision);

    private static PresenceResponse Dto(ConversationPresence presence) => new(presence.User.Value, TicketFlowHttp.Token(presence.Activity), presence.Since, presence.ExpiresAt);

    private static ReplyLockResponse Dto(ReplyLock replyLock) => new(replyLock.Conversation.Value, replyLock.Holder.Value, replyLock.Fence, replyLock.AcquiredAt, replyLock.ExpiresAt);

    private static RoutingPolicy Policy(string id, DefinitionRequest request) => RoutingPolicyCodec.Deserialize(new RoutingPolicyId(id), Json.Raw(request.Definition, "definition"));

    private static DefinitionResponse Dto(StoredRoutingPolicy stored) => new(stored.Policy.Id.Value, Json.Parse(RoutingPolicyCodec.Serialize(stored.Policy)), stored.Revision);
}
