namespace TicketFlow.Core;

/// <summary>An application-authenticated API client. The client's secret or token never enters TicketFlow.</summary>
public sealed record ApiClientPrincipal : PrincipalRef
{
    public ApiClientPrincipal(TicketScope scope, ApiClientId clientId, IEnumerable<string>? externalRoles = null, string? identityProvider = null)
        : base(scope)
    {
        if (clientId == default)
            throw new ArgumentException("An API client identifier is required.", nameof(clientId));
        ClientId = clientId;
        ExternalRoles = SnapshotExternalRoles(externalRoles);
        IdentityProvider = NormalizeIdentityProvider(identityProvider);
    }

    public ApiClientId ClientId { get; }

    public override IReadOnlySet<string> ExternalRoles { get; }

    public override string? IdentityProvider { get; }

    public bool Equals(ApiClientPrincipal? other) =>
        other is not null && Scope == other.Scope && ClientId == other.ClientId
        && ExternalRoles.Count == other.ExternalRoles.Count && ExternalRoles.SetEquals(other.ExternalRoles)
        && IdentityProvidersEqual(IdentityProvider, other.IdentityProvider);

    public override int GetHashCode() =>
        HashCode.Combine(Scope, ClientId, ComputeExternalRolesHash(ExternalRoles), ComputeIdentityProviderHash(IdentityProvider));

    public override string ToString() => "ApiClientPrincipal { <redacted> }";
}
