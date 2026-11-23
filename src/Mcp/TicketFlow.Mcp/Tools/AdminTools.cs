using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Channels;
using TicketFlow.Core;
using TicketFlow.Workspaces;

using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>Administration tools: workspace settings, inboxes, teams and membership, business calendars and channel endpoints.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Admin)]
public sealed class AdminTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    [McpServerTool(Name = "admin_get_workspace", Title = "Get workspace settings", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(WorkspaceSettingsOutput))]
    [Description("Reads the workspace settings: name, default locale, time zone and feature toggles.")]
    [TicketFlowTool(P.WorkspaceRead)]
    public Task<CallToolResult> GetWorkspace(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IWorkspaceSettingsService>().GetAsync(principal, cancellationToken), stored =>
            new WorkspaceSettingsOutput(principal.Scope.WorkspaceId.Value, stored.Settings.Name, stored.Settings.DefaultLocale, stored.Settings.TimeZoneId,
                stored.Settings.FeatureToggles, stored.Revision)), cancellationToken);

    [McpServerTool(Name = "admin_list_inboxes", Title = "List inboxes", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<InboxOutput>))]
    [Description("Lists inboxes the caller may read (channel kind, enabled, SLA/routing policy and calendar references).")]
    [TicketFlowTool(P.InboxRead, Service = typeof(IInboxService))]
    public Task<CallToolResult> ListInboxes(
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var page = new WorkspacePageRequest(limit, cursor is null ? null : TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            return Map(await context.Service<IInboxService>().ListAsync(principal, page, cancellationToken), result => new ToolPage<InboxOutput>(
                [.. result.Items.Select(Inbox)], result.HasMore && result.Items.Count > 0 ? TicketFlowToolJson.EncodeCursor(result.Items[^1].Inbox.Id.Value) : null));
        }, cancellationToken);

    [McpServerTool(Name = "admin_get_inbox", Title = "Get inbox", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(InboxOutput))]
    [Description("Reads one inbox.")]
    [TicketFlowTool(P.InboxRead, Service = typeof(IInboxService))]
    public Task<CallToolResult> GetInbox(
        [Description("Inbox id.")] string inboxId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IInboxService>().GetAsync(principal, new InboxId(inboxId), cancellationToken), Inbox), cancellationToken);

    [McpServerTool(Name = "admin_list_teams", Title = "List teams", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<TeamSummaryOutput>))]
    [Description("Lists teams the caller may read with member counts.")]
    [TicketFlowTool(P.TeamRead, Service = typeof(ITeamService))]
    public Task<CallToolResult> ListTeams(
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var page = new WorkspacePageRequest(limit, cursor is null ? null : TicketFlowToolJson.DecodeCursor(cursor, 1)[0]);
            return Map(await context.Service<ITeamService>().ListAsync(principal, page, cancellationToken), result => new ToolPage<TeamSummaryOutput>(
                [.. result.Items.Select(team => new TeamSummaryOutput(team.Id.Value, team.Name, team.Description, team.MemberCount, team.Revision))],
                result.HasMore && result.Items.Count > 0 ? TicketFlowToolJson.EncodeCursor(result.Items[^1].Id.Value) : null));
        }, cancellationToken);

    [McpServerTool(Name = "admin_get_team", Title = "Get team", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(TeamOutput))]
    [Description("Reads one team with its members.")]
    [TicketFlowTool(P.TeamRead, Service = typeof(ITeamService))]
    public Task<CallToolResult> GetTeam(
        [Description("Team id.")] string teamId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ITeamService>().GetAsync(principal, new TeamId(teamId), cancellationToken), Team), cancellationToken);

    [McpServerTool(Name = "admin_add_team_member", Title = "Add team member", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(TeamOutput))]
    [Description("Adds a user to a team (fenced on the team revision). Team members inherit the team's grants, so the caller must hold every permission those grants confer.")]
    [TicketFlowTool(P.TeamManage, Service = typeof(ITeamService))]
    public Task<CallToolResult> AddTeamMember(
        [Description("Team id.")] string teamId,
        [Description("User id to add.")] string userId,
        [Description("Current team revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ITeamService>().AddMemberAsync(principal, new TeamId(teamId), new UserId(userId), expectedRevision,
            cancellationToken), Team), cancellationToken);

    [McpServerTool(Name = "admin_remove_team_member", Title = "Remove team member", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(TeamOutput))]
    [Description("Removes a user from a team (fenced on the team revision); the user loses the team's grants.")]
    [TicketFlowTool(P.TeamManage, Service = typeof(ITeamService))]
    public Task<CallToolResult> RemoveTeamMember(
        [Description("Team id.")] string teamId,
        [Description("User id to remove.")] string userId,
        [Description("Current team revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<ITeamService>().RemoveMemberAsync(principal, new TeamId(teamId), new UserId(userId), expectedRevision,
            cancellationToken), Team), cancellationToken);

    [McpServerTool(Name = "admin_list_calendars", Title = "List business calendars", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<CalendarOutput>))]
    [Description("Lists business calendars with their time zone and schedule (versioned calendar JSON).")]
    [TicketFlowTool(P.WorkspaceRead, Service = typeof(IBusinessCalendarService))]
    public Task<CallToolResult> ListCalendars(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IBusinessCalendarService>().ListAsync(principal, cancellationToken), list =>
            new ToolList<CalendarOutput>([.. list.Select(stored => new CalendarOutput(stored.Calendar.Id.Value, stored.Calendar.Name, stored.Calendar.TimeZoneId,
                TicketFlowToolJson.Parse(BusinessCalendarCodec.EncodeSchedule(stored.Calendar)), stored.Revision))])), cancellationToken);

    [McpServerTool(Name = "admin_get_channel_endpoint", Title = "Get channel endpoint", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ChannelEndpointOutput))]
    [Description("Reads the channel endpoint bound to an inbox's channel configuration (kind and configuration id from the inbox).")]
    [TicketFlowTool(P.ChannelRead, Service = typeof(IChannelEndpointService))]
    public Task<CallToolResult> GetChannelEndpoint(
        [Description("Channel kind, for example 'email' or 'livechat'.")] string kind,
        [Description("Channel configuration id.")] string configurationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => TicketFlowToolError.Map(
            await context.Service<IChannelEndpointService>().GetAsync(principal, new ChannelKind(kind), configurationId, cancellationToken),
            value => new ChannelEndpointOutput(value.Endpoint.Kind.Value, value.Endpoint.ConfigurationId,
                value.Endpoint.Inbox.Value, value.Endpoint.Address, value.Revision),
            notFoundWhat: "The channel endpoint"), cancellationToken);

    private static InboxOutput Inbox(StoredInbox stored) => new(stored.Inbox.Id.Value, stored.Inbox.Name,
        new ChannelBindingOutput(stored.Inbox.Channel.Kind.Value, stored.Inbox.Channel.ConfigurationId), stored.Inbox.Calendar?.Value, stored.Inbox.SlaPolicy?.Value,
        stored.Inbox.RoutingPolicy?.Value, stored.Inbox.Enabled, stored.Revision);

    private static TeamOutput Team(StoredTeam stored) => new(stored.Team.Id.Value, stored.Team.Name, stored.Team.Description,
        [.. stored.Team.Members.Select(member => member.Value).Order(StringComparer.Ordinal)], stored.Revision);

    internal static object Map<T>(TicketResult<T> result, Func<T, object> success) => TicketFlowToolError.Map(result, success);
}
