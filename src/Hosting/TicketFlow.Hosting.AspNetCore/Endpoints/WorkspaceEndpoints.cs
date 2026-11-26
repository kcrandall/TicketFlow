using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class WorkspaceEndpoints
{
    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/settings", async (TicketFlowActor actor, [FromServices] IWorkspaceSettingsService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetWorkspaceSettings").Produces<WorkspaceSettingsResponse>();
        group.MapPost("/settings", async (TicketFlowActor actor, WorkspaceSettingsRequest request, [FromServices] IWorkspaceSettingsService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Settings(request), http.RequestAborted), stored => TicketFlowHttp.Created(http, "settings", Dto(stored), stored.Revision)))
            .WithName("CreateWorkspaceSettings").Produces<WorkspaceSettingsResponse>(StatusCodes.Status201Created);
        group.MapPut("/settings", async (TicketFlowActor actor, WorkspaceSettingsRequest request, [FromServices] IWorkspaceSettingsService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Settings(request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateWorkspaceSettings").Produces<WorkspaceSettingsResponse>();

        group.MapGet("/inboxes", async (TicketFlowActor actor, [FromServices] IInboxService service, HttpContext http, int? limit, string? cursor) =>
            Map(http, await service.ListAsync(actor.Principal, new WorkspacePageRequest(TicketFlowHttp.PageSize(limit), cursor is null ? null : TicketFlowHttp.DecodeCursor(cursor, 1)[0]), http.RequestAborted),
                page => TicketFlowHttp.Ok(http, new PageResponse<InboxResponse>([.. page.Items.Select(Dto)], page.HasMore ? TicketFlowHttp.EncodeCursor(page.Items[^1].Inbox.Id.Value) : null))))
            .WithName("ListInboxes").Produces<PageResponse<InboxResponse>>();
        group.MapGet("/inboxes/{inboxId}", async (TicketFlowActor actor, string inboxId, [FromServices] IInboxService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new InboxId(inboxId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetInbox").Produces<InboxResponse>();
        group.MapPost("/inboxes", async (TicketFlowActor actor, InboxRequest request, [FromServices] IInboxService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Inbox(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"inboxes/{stored.Inbox.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateInbox").Produces<InboxResponse>(StatusCodes.Status201Created);
        group.MapPut("/inboxes/{inboxId}", async (TicketFlowActor actor, string inboxId, InboxRequest request, [FromServices] IInboxService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Inbox(inboxId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateInbox").Produces<InboxResponse>();
        group.MapDelete("/inboxes/{inboxId}", async (TicketFlowActor actor, string inboxId, [FromServices] IInboxService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new InboxId(inboxId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteInbox").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/inboxes/{inboxId}/agents", async (TicketFlowActor actor, string inboxId, InboxAgentRequest request, [FromServices] IInboxService service, HttpContext http) =>
            Map(http, await service.AddAgentAsync(actor.Principal, new InboxId(inboxId), new UserId(request.UserId), request.Role is null ? null : new RoleId(request.Role), http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, AccessDtos.Grant(stored), stored.Revision)))
            .WithName("AddInboxAgent").Produces<GrantResponse>();
        group.MapDelete("/inboxes/{inboxId}/agents/{userId}", async (TicketFlowActor actor, string inboxId, string userId, string? role, [FromServices] IInboxService service, HttpContext http) =>
            Map(http, await service.RemoveAgentAsync(actor.Principal, new InboxId(inboxId), new UserId(userId), role is null ? null : new RoleId(role), http.RequestAborted), _ => TypedResults.NoContent()))
            .WithName("RemoveInboxAgent").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/teams", async (TicketFlowActor actor, [FromServices] ITeamService service, HttpContext http, int? limit, string? cursor) =>
            Map(http, await service.ListAsync(actor.Principal, new WorkspacePageRequest(TicketFlowHttp.PageSize(limit), cursor is null ? null : TicketFlowHttp.DecodeCursor(cursor, 1)[0]), http.RequestAborted),
                page => TicketFlowHttp.Ok(http, new PageResponse<TeamSummaryResponse>([.. page.Items.Select(team => new TeamSummaryResponse(team.Id.Value, team.Name, team.Description, team.MemberCount, team.Revision))],
                    page.HasMore ? TicketFlowHttp.EncodeCursor(page.Items[^1].Id.Value) : null))))
            .WithName("ListTeams").Produces<PageResponse<TeamSummaryResponse>>();
        group.MapGet("/teams/{teamId}", async (TicketFlowActor actor, string teamId, [FromServices] ITeamService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new TeamId(teamId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetTeam").Produces<TeamResponse>();
        group.MapPost("/teams", async (TicketFlowActor actor, TeamRequest request, [FromServices] ITeamService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Team(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"teams/{stored.Team.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateTeam").Produces<TeamResponse>(StatusCodes.Status201Created);
        group.MapPut("/teams/{teamId}", async (TicketFlowActor actor, string teamId, TeamRequest request, [FromServices] ITeamService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Team(teamId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateTeam").Produces<TeamResponse>();
        group.MapDelete("/teams/{teamId}", async (TicketFlowActor actor, string teamId, [FromServices] ITeamService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new TeamId(teamId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteTeam").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/teams/{teamId}/members", async (TicketFlowActor actor, string teamId, TeamMemberRequest request, [FromServices] ITeamService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.AddMemberAsync(actor.Principal, new TeamId(teamId), new UserId(request.UserId), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("AddTeamMember").Produces<TeamResponse>();
        group.MapDelete("/teams/{teamId}/members/{userId}", async (TicketFlowActor actor, string teamId, string userId, [FromServices] ITeamService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.RemoveMemberAsync(actor.Principal, new TeamId(teamId), new UserId(userId), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("RemoveTeamMember").Produces<TeamResponse>();

        group.MapGet("/calendars", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IBusinessCalendarService service, HttpContext http) =>
            Map(http, await service.ListAsync(actor.Principal, http.RequestAborted), list => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(list, stored => stored.Calendar.Id.Value, Dto, limit, cursor))))
            .WithName("ListCalendars").Produces<PageResponse<CalendarResponse>>();
        group.MapGet("/calendars/{calendarId}", async (TicketFlowActor actor, string calendarId, [FromServices] IBusinessCalendarService service, HttpContext http) =>
            Map(http, await service.GetAsync(actor.Principal, new BusinessCalendarId(calendarId), http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision)))
            .WithName("GetCalendar").Produces<CalendarResponse>();
        group.MapPost("/calendars", async (TicketFlowActor actor, CalendarRequest request, [FromServices] IBusinessCalendarService service, HttpContext http) =>
            Map(http, await service.CreateAsync(actor.Principal, Calendar(request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"calendars/{stored.Calendar.Id.Value}", Dto(stored), stored.Revision)))
            .WithName("CreateCalendar").Produces<CalendarResponse>(StatusCodes.Status201Created);
        group.MapPut("/calendars/{calendarId}", async (TicketFlowActor actor, string calendarId, CalendarRequest request, [FromServices] IBusinessCalendarService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.UpdateAsync(actor.Principal, Calendar(calendarId, request), revision, http.RequestAborted), stored => TicketFlowHttp.Ok(http, Dto(stored), stored.Revision));
        }).WithName("UpdateCalendar").Produces<CalendarResponse>();
        group.MapDelete("/calendars/{calendarId}", async (TicketFlowActor actor, string calendarId, [FromServices] IBusinessCalendarService service, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await service.DeleteAsync(actor.Principal, new BusinessCalendarId(calendarId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteCalendar").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    internal static IResult Map<T>(HttpContext http, TicketResult<T> result, Func<T, IResult> success) => TicketFlowHttp.Map(http, result, success);

    private static WorkspaceSettings Settings(WorkspaceSettingsRequest request) =>
        new(request.Name, request.DefaultLocale, request.TimeZoneId, request.FeatureToggles);

    private static WorkspaceSettingsResponse Dto(StoredWorkspaceSettings stored) =>
        new(stored.Settings.Name, stored.Settings.DefaultLocale, stored.Settings.TimeZoneId, stored.Settings.FeatureToggles, stored.Revision);

    private static Inbox Inbox(string id, InboxRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Channel, nameof(request));
        return new Inbox(new InboxId(id), request.Name, new ChannelBinding(new ChannelKind(request.Channel.Kind), request.Channel.ConfigurationId),
            request.CalendarId is null ? null : new BusinessCalendarId(request.CalendarId),
            request.SlaPolicyId is null ? null : new SlaPolicyId(request.SlaPolicyId),
            request.RoutingPolicyId is null ? null : new RoutingPolicyId(request.RoutingPolicyId),
            request.Enabled, request.GreetingMessage, request.AwayMessage);
    }

    internal static InboxResponse Dto(StoredInbox stored)
    {
        var inbox = stored.Inbox;
        return new InboxResponse(inbox.Id.Value, inbox.Name, new ChannelBindingModel(inbox.Channel.Kind.Value, inbox.Channel.ConfigurationId), inbox.Calendar?.Value,
            inbox.SlaPolicy?.Value, inbox.RoutingPolicy?.Value, inbox.Enabled, inbox.GreetingMessage, inbox.AwayMessage, stored.Revision);
    }

    private static Team Team(string id, TeamRequest request) =>
        new(new TeamId(id), request.Name, request.Description, request.Members?.Select(member => new UserId(member)));

    private static TeamResponse Dto(StoredTeam stored) =>
        new(stored.Team.Id.Value, stored.Team.Name, stored.Team.Description, [.. stored.Team.Members.Select(member => member.Value).Order(StringComparer.Ordinal)], stored.Revision);

    private static BusinessCalendar Calendar(string id, CalendarRequest request) =>
        BusinessCalendarCodec.DecodeSchedule(new BusinessCalendarId(id), request.Name, request.TimeZoneId, Json.Raw(request.Schedule, "schedule"));

    private static CalendarResponse Dto(StoredBusinessCalendar stored) =>
        new(stored.Calendar.Id.Value, stored.Calendar.Name, stored.Calendar.TimeZoneId, Json.Parse(BusinessCalendarCodec.EncodeSchedule(stored.Calendar)), stored.Revision);
}

internal static class Ids
{
    /// <summary>A generated identifier: a version 7 GUID in "N" format.</summary>
    public static string New() => Guid.CreateVersion7().ToString("N");
}

internal static class Json
{
    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The raw JSON of a request definition; <c>undefined</c>/<c>null</c> definitions are invalid.</summary>
    public static string Raw(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? throw new ArgumentException($"'{name}' is required.", name) : element.GetRawText();
}
