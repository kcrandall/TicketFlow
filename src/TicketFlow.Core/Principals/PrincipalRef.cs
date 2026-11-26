using System.Collections.Frozen;

namespace TicketFlow.Core;

/// <summary>
/// The closed set of actors that can read or mutate support data. Principals are references resolved by the application;
/// they never carry credentials, tokens or authentication objects. Switch over every case without a discard arm.
/// </summary>
public closed record PrincipalRef
{
    /// <summary>Maximum number of external role names carried in one snapshot.</summary>
    public const int MaximumExternalRoles = 256;

    private protected PrincipalRef(TicketScope scope) => Scope = scope ?? throw new ArgumentNullException(nameof(scope));

    /// <summary>The full scope the principal acts within.</summary>
    public TicketScope Scope { get; }

    /// <summary>External identity provider role names carried at resolution time for optional role federation.</summary>
    public virtual IReadOnlySet<string> ExternalRoles => FrozenSet<string>.Empty;

    /// <summary>External identity provider identifier carried at resolution time for provider-scoped role federation.</summary>
    public virtual string? IdentityProvider => null;

    protected static IReadOnlySet<string> SnapshotExternalRoles(IEnumerable<string>? externalRoles)
    {
        if (externalRoles is null)
            return FrozenSet<string>.Empty;

        var snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in externalRoles)
        {
            if (string.IsNullOrWhiteSpace(role))
                continue;

            var normalized = TicketScope.RequireIdentifier(role.Trim(), nameof(externalRoles));
            if (snapshot.Add(normalized) && snapshot.Count > MaximumExternalRoles)
                throw new ArgumentException($"A principal can carry at most {MaximumExternalRoles} external roles.", nameof(externalRoles));
        }

        return snapshot.Count == 0
            ? FrozenSet<string>.Empty
            : snapshot.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    protected static string? NormalizeIdentityProvider(string? identityProvider) =>
        string.IsNullOrWhiteSpace(identityProvider)
            ? null
            : TicketScope.RequireIdentifier(identityProvider.Trim(), nameof(identityProvider));

    protected static bool IdentityProvidersEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    protected static int ComputeIdentityProviderHash(string? identityProvider) =>
        identityProvider is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(identityProvider);

    protected static int ComputeExternalRolesHash(IReadOnlySet<string> externalRoles)
    {
        var hash = 0;
        foreach (var role in externalRoles)
            hash ^= StringComparer.OrdinalIgnoreCase.GetHashCode(role);
        return hash;
    }
}
