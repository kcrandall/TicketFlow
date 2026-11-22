namespace TicketFlow.Core;

/// <summary>
/// The closed set of actors that can read or mutate support data. Principals are references resolved by the application;
/// they never carry credentials, tokens or authentication objects. Switch over every case without a discard arm.
/// </summary>
public union PrincipalRef(UserPrincipal, ApiClientPrincipal, AutomationPrincipal, SystemPrincipal)
{
    /// <summary>The full scope the principal acts within.</summary>
    /// <exception cref="System.Runtime.CompilerServices.SwitchExpressionException">The reference is <c>default</c>.</exception>
    public TicketScope Scope => this switch
    {
        UserPrincipal user => user.Scope,
        ApiClientPrincipal client => client.Scope,
        AutomationPrincipal automation => automation.Scope,
        SystemPrincipal system => system.Scope,
    };
}
