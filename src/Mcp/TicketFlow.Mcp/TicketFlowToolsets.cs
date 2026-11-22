using TicketFlow.Access;
using TicketFlow.Assist;
using TicketFlow.Automation;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.HelpCenter;
using TicketFlow.Reporting;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp;

/// <summary>
/// Role-oriented groups of TicketFlow MCP tools. Hosts plug in any combination; the self-awareness tools (<c>whoami</c>,
/// <c>list_toolsets</c>, <c>describe_tools</c>, <c>explain_access</c>) are always registered and are not a selectable toolset.
/// </summary>
[Flags]
public enum TicketFlowToolsets
{
    None = 0,

    /// <summary>Conversation lists, timelines, replies, notes, assignment, status, priority, labels and deletion.</summary>
    Conversations = 1 << 0,

    /// <summary>Contact search and profile edits, audited reveal, merge/unmerge and account-link suggestions.</summary>
    Contacts = 1 << 1,

    /// <summary>Unassigned queues and agent availability.</summary>
    Routing = 1 << 2,

    /// <summary>Conversation SLA status, the at-risk/breached list and SLA policies.</summary>
    Sla = 1 << 3,

    /// <summary>Automation rules and dry runs, macros and canned responses.</summary>
    Automation = 1 << 4,

    /// <summary>Help center search, articles, drafts and publishing.</summary>
    HelpCenter = 1 << 5,

    /// <summary>Survey definitions, responses and CSAT/NPS aggregates.</summary>
    Surveys = 1 << 6,

    /// <summary>Support overview, agent leaderboard and trends.</summary>
    Reports = 1 << 7,

    /// <summary>Workspace settings, inboxes, teams and membership, business calendars and channel endpoints.</summary>
    Admin = 1 << 8,

    /// <summary>Roles, grants and field policies. Sensitive: every decision is audited by Access; plug in deliberately.</summary>
    Access = 1 << 9,

    /// <summary>Assistant summaries, reply drafts and triage suggestions.</summary>
    Assist = 1 << 10,

    /// <summary>Every toolset except <see cref="Access"/>.</summary>
    Standard = Conversations | Contacts | Routing | Sla | Automation | HelpCenter | Surveys | Reports | Admin | Assist,

    /// <summary>Every toolset, including <see cref="Access"/>.</summary>
    All = Standard | Access,
}

/// <summary>Descriptive metadata of one toolset (or of the always-on self-awareness group).</summary>
public sealed record TicketFlowToolsetInfo(TicketFlowToolsets Toolset, string Name, string Description, Type PrimaryService);

/// <summary>Names, descriptions and primary feature services of the toolsets.</summary>
public static class TicketFlowToolsetCatalog
{
    /// <summary>The name of the always-registered self-awareness group.</summary>
    public const string SelfName = "self";

    /// <summary>Every selectable toolset in display order.</summary>
    public static IReadOnlyList<TicketFlowToolsetInfo> All { get; } =
    [
        new(TicketFlowToolsets.Conversations, "conversations", "Conversation lists, timelines, replies, private notes, assignment, status, priority, labels and deletion.", typeof(IConversationService)),
        new(TicketFlowToolsets.Contacts, "contacts", "Contact search, profiles, audited reveal of masked fields, merge/unmerge and account-link suggestions.", typeof(IContactService)),
        new(TicketFlowToolsets.Routing, "routing", "Unassigned queues and agent availability status.", typeof(IRoutingQueueService)),
        new(TicketFlowToolsets.Sla, "sla", "Conversation SLA status, the at-risk/breached list and SLA policies.", typeof(ISlaService)),
        new(TicketFlowToolsets.Automation, "automation", "Automation rules and dry runs, macros and canned responses.", typeof(IAutomationRuleService)),
        new(TicketFlowToolsets.HelpCenter, "helpcenter", "Help center search, articles, drafts and publishing.", typeof(IHelpCenterService)),
        new(TicketFlowToolsets.Surveys, "surveys", "Survey definitions, responses and CSAT/NPS aggregates.", typeof(ISurveyService)),
        new(TicketFlowToolsets.Reports, "reports", "Support overview, agent leaderboard and trends.", typeof(IReportingService)),
        new(TicketFlowToolsets.Admin, "admin", "Workspace settings, inboxes, teams and membership, business calendars and channel endpoints.", typeof(IWorkspaceSettingsService)),
        new(TicketFlowToolsets.Access, "access", "Roles, grants and field policies (sensitive; Access audits every decision).", typeof(IAccessAdministrator)),
        new(TicketFlowToolsets.Assist, "assist", "Assistant conversation summaries, reply drafts and triage suggestions.", typeof(IAssistService)),
    ];

    /// <summary>The toolset with <paramref name="name"/> (ordinal, case-insensitive), or <see langword="null"/>.</summary>
    public static TicketFlowToolsetInfo? Find(string name) =>
        All.FirstOrDefault(info => string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The metadata of a single toolset flag.</summary>
    public static TicketFlowToolsetInfo Get(TicketFlowToolsets toolset) =>
        All.FirstOrDefault(info => info.Toolset == toolset) ?? throw new ArgumentException("A single toolset flag is required.", nameof(toolset));

    /// <summary>The name of a single toolset flag, or <see cref="SelfName"/> for <see cref="TicketFlowToolsets.None"/>.</summary>
    public static string NameOf(TicketFlowToolsets toolset) => toolset == TicketFlowToolsets.None ? SelfName : Get(toolset).Name;
}
