using TicketFlow.Access;
using TicketFlow.Automation;
using TicketFlow.Channels;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.HelpCenter;
using TicketFlow.Reporting;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>A sidebar section: shown when its feature service is registered and the viewer holds one of its permissions at workspace level.</summary>
public sealed record UiSection(string Key, string Title, string Path, string Icon, string Group, Type? Service, IReadOnlyList<Permission> Permissions)
{
    /// <summary>The host registered the feature (optional features without services are hidden entirely).</summary>
    public bool IsAvailable(TicketFlowUISession session) => Service is null || session.Services.GetService(Service) is not null;

    public bool IsPermitted(TicketFlowUISession session) => Permissions.Count == 0 || Permissions.Any(session.Can);

    public bool IsVisible(TicketFlowUISession session) => IsAvailable(session) && IsPermitted(session);
}

public static class UiNavigation
{
    public const string Overview = "Overview";
    public const string Workspace = "Workspace";
    public const string Operations = "Operations";
    public const string Administration = "Administration";

    public static IReadOnlyList<UiSection> Sections { get; } =
    [
        new("dashboard", "Dashboard", "", "layout-dashboard", Overview, null, []),
        new("conversations", "Conversations", "conversations", "messages-square", Overview, typeof(IConversationService), [AccessPermissions.ConversationRead]),
        new("contacts", "Contacts", "contacts", "contact", Overview, typeof(IContactService), [AccessPermissions.ContactRead]),
        new("reports", "Reports", "reports", "chart-column", Overview, typeof(IReportingService), [AccessPermissions.ReportView, ReportPermissions.ViewOwn]),

        new("inboxes", "Inboxes", "inboxes", "inbox", Workspace, typeof(IInboxService), [AccessPermissions.InboxRead]),
        new("teams", "Teams", "teams", "users", Workspace, typeof(ITeamService), [AccessPermissions.TeamRead]),
        new("calendars", "Business hours", "calendars", "calendar-clock", Workspace, typeof(IBusinessCalendarService), [AccessPermissions.WorkspaceRead]),
        new("channels", "Channels", "channels", "radio-tower", Workspace, typeof(IChannelEndpointService), [ChannelPermissions.Read, ChannelPermissions.Manage, AccessPermissions.InboxManage]),

        new("routing", "Routing", "routing", "route", Operations, typeof(IRoutingQueueService), [AccessPermissions.RoutingManage, AccessPermissions.ConversationRead]),
        new("sla", "SLA policies", "sla", "timer", Operations, typeof(ISlaService), [SlaPermissions.Read, SlaPermissions.Manage]),
        new("automation", "Automation", "automation", "workflow", Operations, typeof(IAutomationRuleService), [AutomationPermissions.Read, AutomationPermissions.Manage, AutomationPermissions.MacroManage]),
        new("help-center", "Help center", "help-center", "book-open", Operations, typeof(IHelpCenterService), [HelpCenterPermissions.Read, HelpCenterPermissions.Write]),
        new("surveys", "Surveys", "surveys", "smile", Operations, typeof(ISurveyService), [SurveyPermissions.Read, SurveyPermissions.Manage]),

        new("access", "Access", "access", "shield-check", Administration, typeof(IAccessAdministrator), [AccessPermissions.AccessManage]),
        new("settings", "Settings", "settings", "settings", Administration, null, []),
    ];

    public static UiSection Section(string key) => Sections.First(section => section.Key == key);

    public static IEnumerable<UiSection> Visible(TicketFlowUISession session) =>
        Sections.Where(section => section.IsAvailable(session) && (section.IsPermitted(session) || session.Options.ShowDisabledSections));
}
