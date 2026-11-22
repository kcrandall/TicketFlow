using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Channels;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp.Tools;

/// <summary>Administration tools: workspace settings, inboxes, teams and membership, business calendars and channel endpoints.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Admin)]
public sealed class AdminTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "admin_get_workspace", Title = "Get workspace settings", ReadOnly = true, Idempotent = true)]
    [Description("Reads the workspace settings: name, default locale, time zone and feature toggles.")]
    [TicketFlowTool("workspace:read")]
    public Task<CallToolResult> GetWorkspace(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IWorkspaceSettingsService>().GetAsync(principal, cancellationToken), stored => new
        {
            workspace = principal.Scope.WorkspaceId.Value,
            name = stored.Settings.Name,
            defaultLocale = stored.Settings.DefaultLocale,
            timeZone = stored.Settings.TimeZoneId,
            featureToggles = stored.Settings.FeatureToggles,
            revision = stored.Revision,
        }), cancellationToken);

    [McpServerTool(Name = "admin_list_inboxes", Title = "List inboxes", ReadOnly = true, Idempotent = true)]
    [Description("Lists inboxes the caller may read (channel kind, enabled, SLA/routing policy and calendar references).")]
    [TicketFlowTool("inbox:read", Service = typeof(IInboxService))]
    public Task<CallToolResult> ListInboxes(
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var page = new WorkspacePageRequest(limit, cursor is null ? null : TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            return Map(await context.Service<IInboxService>().ListAsync(principal, page, cancellationToken), result => new
            {
                items = result.Items.Select(Inbox).ToList(),
                next = result.HasMore && result.Items.Count > 0 ? TicketFlowToolJson.EncodeCursor(result.Items[^1].Inbox.Id.Value) : null,
            });
        }, cancellationToken);

    [McpServerTool(Name = "admin_get_inbox", Title = "Get inbox", ReadOnly = true, Idempotent = true)]
    [Description("Reads one inbox.")]
    [TicketFlowTool("inbox:read", Service = typeof(IInboxService))]
    public Task<CallToolResult> GetInbox(
        [Description("Inbox id.")] string inboxId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IInboxService>().GetAsync(principal, new InboxId(inboxId), cancellationToken), Inbox), cancellationToken);

    [McpServerTool(Name = "admin_list_teams", Title = "List teams", ReadOnly = true, Idempotent = true)]
    [Description("Lists teams the caller may read with member counts.")]
    [TicketFlowTool("team:read", Service = typeof(ITeamService))]
    public Task<CallToolResult> ListTeams(
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var page = new WorkspacePageRequest(limit, cursor is null ? null : TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            return Map(await context.Service<ITeamService>().ListAsync(principal, page, cancellationToken), result => new
            {
                items = result.Items.Select(team => new { id = team.Id.Value, name = team.Name, description = team.Description, memberCount = team.MemberCount, revision = team.Revision }).ToList(),
                next = result.HasMore && result.Items.Count > 0 ? TicketFlowToolJson.EncodeCursor(result.Items[^1].Id.Value) : null,
            });
        }, cancellationToken);

    [McpServerTool(Name = "admin_get_team", Title = "Get team", ReadOnly = true, Idempotent = true)]
    [Description("Reads one team with its members.")]
    [TicketFlowTool("team:read", Service = typeof(ITeamService))]
    public Task<CallToolResult> GetTeam(
        [Description("Team id.")] string teamId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ITeamService>().GetAsync(principal, new TeamId(teamId), cancellationToken), Team), cancellationToken);

    [McpServerTool(Name = "admin_add_team_member", Title = "Add team member", Destructive = false, Idempotent = false)]
    [Description("Adds a user to a team (fenced on the team revision). Team members inherit the team's grants, so the caller must hold every permission those grants confer.")]
    [TicketFlowTool("team:manage", Service = typeof(ITeamService))]
    public Task<CallToolResult> AddTeamMember(
        [Description("Team id.")] string teamId,
        [Description("User id to add.")] string userId,
        [Description("Current team revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ITeamService>().AddMemberAsync(principal, new TeamId(teamId), new UserId(userId), expectedRevision,
            cancellationToken), Team), cancellationToken);

    [McpServerTool(Name = "admin_remove_team_member", Title = "Remove team member", Destructive = true, Idempotent = false)]
    [Description("Removes a user from a team (fenced on the team revision); the user loses the team's grants.")]
    [TicketFlowTool("team:manage", Service = typeof(ITeamService))]
    public Task<CallToolResult> RemoveTeamMember(
        [Description("Team id.")] string teamId,
        [Description("User id to remove.")] string userId,
        [Description("Current team revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ITeamService>().RemoveMemberAsync(principal, new TeamId(teamId), new UserId(userId), expectedRevision,
            cancellationToken), Team), cancellationToken);

    [McpServerTool(Name = "admin_list_calendars", Title = "List business calendars", ReadOnly = true, Idempotent = true)]
    [Description("Lists business calendars with their time zone and schedule (versioned calendar JSON).")]
    [TicketFlowTool("workspace:read", Service = typeof(IBusinessCalendarService))]
    public Task<CallToolResult> ListCalendars(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IBusinessCalendarService>().ListAsync(principal, cancellationToken), list => new
        {
            items = list.Select(stored =>
            {
                using var schedule = JsonDocument.Parse(BusinessCalendarCodec.EncodeSchedule(stored.Calendar));
                return new { id = stored.Calendar.Id.Value, name = stored.Calendar.Name, timeZone = stored.Calendar.TimeZoneId, schedule = schedule.RootElement.Clone(), revision = stored.Revision };
            }).ToList(),
        }), cancellationToken);

    [McpServerTool(Name = "admin_get_channel_endpoint", Title = "Get channel endpoint", ReadOnly = true, Idempotent = true)]
    [Description("Reads the channel endpoint bound to an inbox's channel configuration (kind and configuration id from the inbox).")]
    [TicketFlowTool("channel:read", Service = typeof(IChannelEndpointService))]
    public Task<CallToolResult> GetChannelEndpoint(
        [Description("Channel kind, for example 'email' or 'livechat'.")] string kind,
        [Description("Channel configuration id.")] string configurationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => await context.Service<IChannelEndpointService>().GetAsync(principal, new ChannelKind(kind), configurationId, cancellationToken) switch
        {
            ChannelValue<StoredChannelEndpoint> value => new
            {
                kind = value.Value.Endpoint.Kind.Value,
                configurationId = value.Value.Endpoint.ConfigurationId,
                inboxId = value.Value.Endpoint.Inbox.Value,
                address = value.Value.Endpoint.Address,
                revision = value.Value.Revision,
            },
            ChannelNotFound => TicketFlowToolError.NotFound("The channel endpoint"),
            ChannelConflict conflict => TicketFlowToolError.Conflict(conflict.CurrentRevision),
            ChannelRefused refused => refused.Reason switch
            {
                ChannelRefusal.Forbidden => TicketFlowToolError.Forbidden(),
                ChannelRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
                _ => TicketFlowToolError.Rejected(refused.Errors),
            },
        }, cancellationToken);

    private static object Inbox(StoredInbox stored) => new
    {
        id = stored.Inbox.Id.Value,
        name = stored.Inbox.Name,
        channel = new { kind = stored.Inbox.Channel.Kind.Value, configurationId = stored.Inbox.Channel.ConfigurationId },
        calendarId = stored.Inbox.Calendar?.Value,
        slaPolicyId = stored.Inbox.SlaPolicy?.Value,
        routingPolicyId = stored.Inbox.RoutingPolicy?.Value,
        enabled = stored.Inbox.Enabled,
        revision = stored.Revision,
    };

    private static object Team(StoredTeam stored) => new
    {
        id = stored.Team.Id.Value,
        name = stored.Team.Name,
        description = stored.Team.Description,
        members = stored.Team.Members.Select(member => member.Value).Order(StringComparer.Ordinal).ToList(),
        revision = stored.Revision,
    };

    internal static object Map<T>(WorkspaceResult<T> result, Func<T, object> success) => result switch
    {
        WorkspaceSucceeded<T> succeeded => success(succeeded.Value),
        WorkspaceRefused refused => refused.Reason switch
        {
            WorkspaceRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            WorkspaceRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            WorkspaceRefusal.NotFound => TicketFlowToolError.NotFound(),
            WorkspaceRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
    };
}
