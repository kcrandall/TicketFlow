namespace TicketFlow.Core;

/// <summary>An application-authenticated API client. The client's secret or token never enters TicketFlow.</summary>
public sealed record ApiClientPrincipal
{
    public ApiClientPrincipal(TicketScope scope, ApiClientId clientId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (clientId == default)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));
        Scope = scope;
        ClientId = clientId;
    }

    public TicketScope Scope { get; }
    public ApiClientId ClientId { get; }

    public override string ToString() => "ApiClientPrincipal { <redacted> }";
}
