using TicketFlow.Access;

namespace TicketFlow.Sla;

/// <summary>
/// Permissions owned by the SLA feature, registered in the catalog by <c>AddSla()</c>. They are not Access built-ins, so only the
/// Owner role holds them until custom roles grant them. Timeline writes by the SLA tracker additionally need
/// <c>conversation:manage</c> (extension events).
/// </summary>
public static class SlaPermissions
{
    /// <summary>Read SLA policies (workspace level) and conversation SLA status and risk lists (on the conversation resource).</summary>
    public static Permission Read { get; } = new("sla:read");

    /// <summary>Create, update and delete SLA policies.</summary>
    public static Permission Manage { get; } = new("sla:manage");

    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Manage, false, "Manage SLA policies."),
        new(Read, false, "Read SLA policies and conversation SLA status."),
    ];
}
