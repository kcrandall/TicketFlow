namespace TicketFlow.Core;

/// <summary>An application-authenticated API client. The client's secret or token never enters TicketFlow.</summary>
public sealed record ApiClientPrincipal : PrincipalRef
{
    public ApiClientPrincipal(TicketScope scope, ApiClientId clientId, IEnumerable<string>? externalRoles = null)
        : base(scope)
    {
        if (clientId == default)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));
        ClientId = clientId;
        ExternalRoles = SnapshotExternalRoles(externalRoles);
    }

    public ApiClientId ClientId { get; }

    public override IReadOnlySet<string> ExternalRoles { get; }

    public bool Equals(ApiClientPrincipal? other) =>
        other is not null && Scope == other.Scope && ClientId == other.ClientId
        && ExternalRoles.Count == other.ExternalRoles.Count && ExternalRoles.SetEquals(other.ExternalRoles);

    public override int GetHashCode() =>
        HashCode.Combine(Scope, ClientId, ComputeExternalRolesHash(ExternalRoles));

    public override string ToString() => "ApiClientPrincipal { <redacted> }";
}
