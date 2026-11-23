namespace TicketFlow.Core;

/// <summary>
/// The closed set of actors that can read or mutate support data. Principals are references resolved by the application;
/// they never carry credentials, tokens or authentication objects. Switch over every case without a discard arm.
/// </summary>
public closed record PrincipalRef
{
    private protected PrincipalRef(TicketScope scope) => Scope = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <summary>The full scope the principal acts within.</summary>
    public TicketScope Scope { get; }
}
