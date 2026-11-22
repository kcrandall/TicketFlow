namespace TicketFlow.Core;

/// <summary>An automation rule or macro acting within a scope. Grants for automation are configured explicitly.</summary>
public sealed record AutomationPrincipal
{
    public AutomationPrincipal(TicketScope scope, string automationId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
        AutomationId = TicketScope.RequireIdentifier(automationId, nameof(automationId));
    }

    public TicketScope Scope { get; }

    /// <summary>The rule or macro identifier that produced the action.</summary>
    public string AutomationId { get; }

    public override string ToString() => "AutomationPrincipal { <redacted> }";
}
