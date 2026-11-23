namespace TicketFlow.Core;

/// <summary>An application-authenticated API client. The client's secret or token never enters TicketFlow.</summary>
public sealed record ApiClientPrincipal : PrincipalRef
{
    public ApiClientPrincipal(TicketScope scope, ApiClientId clientId)
        : base(scope)
    {
        if (clientId == default)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));
        ClientId = clientId;
    }

    public ApiClientId ClientId { get; }

    public override string ToString() => "ApiClientPrincipal { <redacted> }";
}
