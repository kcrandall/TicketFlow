namespace TicketFlow.Mcp;

/// <summary>
/// Compile-time permission names for <see cref="TicketFlowToolAttribute"/>. Each constant equals the feature package's
/// <c>Permission</c> value (verified by tests against <c>AccessPermissions</c>, <c>ConversationPermissions</c>, <c>ContactPermissions</c>,
/// ...), so a renamed permission fails the build of the tests instead of silently drifting.
/// </summary>
public static class TicketFlowMcpPermissions
{
    public const string ConversationRead = "conversation:read";
    public const string ConversationCreate = "conversation:create";
    public const string ConversationReply = "conversation:reply";
    public const string ConversationAssign = "conversation:assign";
    public const string ConversationResolve = "conversation:resolve";
    public const string ConversationManage = "conversation:manage";
    public const string ConversationDelete = "conversation:delete";
    public const string NoteRead = "note:read";
    public const string NoteWrite = "note:write";
    public const string ContactRead = "contact:read";
    public const string ContactCreate = "contact:create";
    public const string ContactUpdate = "contact:update";
    public const string ContactReveal = "contact:reveal";
    public const string ContactMerge = "contact:merge";
    public const string ContactLink = "contact:link";
    public const string SlaRead = "sla:read";
    public const string SlaManage = "sla:manage";
    public const string AutomationRead = "automation:read";
    public const string AutomationManage = "automation:manage";
    public const string MacroRun = "macro:run";
    public const string MacroManage = "macro:manage";
    public const string CannedResponseManage = "canned-response:manage";
    public const string RoutingManage = "routing:manage";
    public const string HelpCenterRead = "helpcenter:read";
    public const string HelpCenterWrite = "helpcenter:write";
    public const string HelpCenterPublish = "helpcenter:publish";
    public const string SurveyRead = "survey:read";
    public const string ReportView = "report:view";
    public const string ReportViewOwn = "report:view-own";
    public const string WorkspaceRead = "workspace:read";
    public const string InboxRead = "inbox:read";
    public const string TeamRead = "team:read";
    public const string TeamManage = "team:manage";
    public const string ChannelRead = "channel:read";
    public const string AccessManage = "access:manage";
    public const string AgentStatusUpdate = "agent-status:update";
    public const string AgentStatusManage = "agent-status:manage";
    public const string AssistUse = "assist:use";
}
