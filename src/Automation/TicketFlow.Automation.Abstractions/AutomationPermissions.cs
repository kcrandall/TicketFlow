using TicketFlow.Access;

namespace TicketFlow.Automation;

/// <summary>
/// Permissions owned by the automation feature, registered in the catalog by <c>AddAutomation()</c>. They are not Access built-ins,
/// so only the Owner role holds them until custom roles grant them. Reading canned responses also succeeds with
/// <c>conversation:reply</c>.
/// </summary>
public static class AutomationPermissions
{
    /// <summary>Read automation rules, the execution log, dry-run previews and replays.</summary>
    public static Permission Read { get; } = new("automation:read");

    /// <summary>Create, update and delete automation rules (sensitive: rules act with the automation client's grants).</summary>
    public static Permission Manage { get; } = new("automation:manage");

    /// <summary>Run macros on a conversation (checked on the conversation resource) and manage one's personal macros.</summary>
    public static Permission MacroRun { get; } = new("macro:run");

    /// <summary>Create, update and delete shared macros.</summary>
    public static Permission MacroManage { get; } = new("macro:manage");

    /// <summary>Create, update and delete canned responses.</summary>
    public static Permission CannedResponseManage { get; } = new("canned-response:manage");

    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Manage, true, "Manage automation rules."),
        new(Read, false, "Read automation rules and the execution log."),
        new(CannedResponseManage, false, "Manage canned responses."),
        new(MacroManage, false, "Manage shared macros."),
        new(MacroRun, false, "Run macros and manage personal macros."),
    ];
}
