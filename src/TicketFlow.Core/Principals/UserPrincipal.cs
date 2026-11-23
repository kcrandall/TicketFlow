using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace TicketFlow.Core;

/// <summary>
/// An application-authenticated human user (agent) with an optional, bounded snapshot of team memberships taken when the
/// principal was resolved. The snapshot is evidence for authorization conditions, not a live membership query.
/// </summary>
public sealed record UserPrincipal : PrincipalRef
{
    /// <summary>Maximum number of team memberships carried in one snapshot.</summary>
    public const int MaximumTeamMemberships = 256;

    public UserPrincipal(TicketScope scope, UserId userId, IEnumerable<TeamId>? teams = null)
        : base(scope)
    {
        if (userId == default)
            throw new ArgumentException("A user identifier is required.", nameof(userId));
        UserId = userId;
        Teams = SnapshotTeams(teams);
    }

    public UserId UserId { get; }

    /// <summary>Team memberships within <see cref="PrincipalRef.Scope"/>'s workspace at resolution time.</summary>
    public IReadOnlySet<TeamId> Teams { get; }

    public bool Equals(UserPrincipal? other) =>
        other is not null && Scope == other.Scope && UserId == other.UserId && Teams.SetEquals(other.Teams);

    public override int GetHashCode()
    {
        var teams = 0;
        foreach (var team in Teams)
            teams ^= team.GetHashCode();
        return HashCode.Combine(Scope, UserId, Teams.Count, teams);
    }

    public override string ToString() => "UserPrincipal { <redacted> }";

    private static IReadOnlySet<TeamId> SnapshotTeams(IEnumerable<TeamId>? teams)
    {
        if (teams is null)
            return FrozenSet<TeamId>.Empty;

        var capacity = teams.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumTeamMemberships) : 0;
        if (count == 0 && teams is ICollection<TeamId> or IReadOnlyCollection<TeamId>)
            return FrozenSet<TeamId>.Empty;

        var snapshot = capacity > 0 ? new HashSet<TeamId>(capacity) : new HashSet<TeamId>();
        foreach (var team in teams)
        {
            if (team == default)
                throw new ArgumentException("Team memberships cannot contain uninitialized identifiers.", nameof(teams));
            if (snapshot.Add(team) && snapshot.Count > MaximumTeamMemberships)
                throw new ArgumentException($"A principal can carry at most {MaximumTeamMemberships} team memberships.", nameof(teams));
        }
        return snapshot.Count == 0 ? FrozenSet<TeamId>.Empty : new ReadOnlySet<TeamId>(snapshot);
    }
}
