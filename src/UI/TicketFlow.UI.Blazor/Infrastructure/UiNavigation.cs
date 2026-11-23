using System.Collections.Frozen;
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

    public bool IsPermitted(TicketFlowUISession session)
    {
        for (var i = 0; i < Permissions.Count; i++)
            if (session.Can(Permissions[i]))
                return true;
        return Permissions.Count == 0;
    }

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

    private static readonly FrozenDictionary<string, UiSection> ByKey = Sections.ToFrozenDictionary(section => section.Key, StringComparer.Ordinal);

    public static UiSection Section(string key) => ByKey[key];

    /// <summary>Whether the session may open <paramref name="route"/> (the same rule for GET pages and POST actions).</summary>
    public static bool Allows(TicketFlowUISession session, UiRoute route) =>
        session.State == UiSessionState.Ready && Section(route.Section).IsVisible(session);

    /// <summary>The section an action belongs to; actions run only on a page of that section. Null for unknown actions.</summary>
    public static string? ActionSection(string action)
    {
        var dot = action.IndexOf('.');
        var prefix = dot < 0 ? action.AsSpan() : action.AsSpan(0, dot);
        return prefix switch
        {
            "conversation" => "conversations",
            "contact" => "contacts",
            "inbox" => "inboxes",
            "team" => "teams",
            "calendar" => "calendars",
            "channel" => "channels",
            "routing" => "routing",
            "sla" => "sla",
            "automation" => "automation",
            "helpcenter" => "help-center",
            "survey" => "surveys",
            "access" => "access",
            "settings" => "settings",
            _ => null,
        };
    }

    public static IEnumerable<UiSection> Visible(TicketFlowUISession session) =>
        Sections.Where(section => section.IsAvailable(session) && (section.IsPermitted(session) || session.Options.ShowDisabledSections));
}
