namespace TicketFlow.Core;

/// <summary>An automation rule or macro acting within a scope. Grants for automation are configured explicitly.</summary>
public sealed record AutomationPrincipal : PrincipalRef
{
    public AutomationPrincipal(TicketScope scope, string automationId)
        : base(scope)
    {
        AutomationId = TicketScope.RequireIdentifier(automationId, nameof(automationId));
    }

    /// <summary>The rule or macro identifier that produced the action.</summary>
    public string AutomationId { get; }

    public override string ToString() => "AutomationPrincipal { <redacted> }";
}
