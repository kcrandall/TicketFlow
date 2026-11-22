namespace TicketFlow.Core;

/// <summary>An internal TicketFlow or host component (for example an SLA worker) acting within a scope.</summary>
public sealed record SystemPrincipal
{
    public SystemPrincipal(TicketScope scope, string component)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
        Component = TicketScope.RequireIdentifier(component, nameof(component));
    }

    public TicketScope Scope { get; }

    /// <summary>A stable component name such as <c>sla-worker</c>.</summary>
    public string Component { get; }

    public override string ToString() => "SystemPrincipal { <redacted> }";
}
