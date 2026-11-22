using System.Collections.Frozen;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// A team of agents: grant subject (team grants apply to every member's principal) and routing target. Members are bounded by
/// <see cref="MaximumMembers"/>; stores additionally cap each user at <see cref="WorkspaceLimits.MaximumTeamsPerUser"/> teams per
/// workspace so a principal's team snapshot always fits.
/// </summary>
public sealed record Team
{
    public const int MaximumNameLength = 128;
    public const int MaximumDescriptionLength = 1024;
    public const int MaximumMembers = 1000;

    public Team(TeamId id, string name, string? description = null, IEnumerable<UserId>? members = null)
    {
        if (id == default)
            throw new ArgumentException("A team identifier is required.", nameof(id));
        Id = id;
        Name = WorkspaceText.RequireText(name, MaximumNameLength, nameof(name));
        Description = description is null ? null : WorkspaceText.RequireMessage(description, MaximumDescriptionLength, nameof(description));
        Members = Snapshot(members);
    }

    public TeamId Id { get; }
    public string Name { get; }
    public string? Description { get; }
    public IReadOnlySet<UserId> Members { get; }

    /// <summary>A copy of this team with <paramref name="members"/> replacing the current members.</summary>
    public Team WithMembers(IEnumerable<UserId> members) => new(Id, Name, Description, members);

    public bool Equals(Team? other) =>
        other is not null && Id == other.Id && Name == other.Name && Description == other.Description && Members.SetEquals(other.Members);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Description, Members.Count);

    public override string ToString() => "Team { <redacted> }";

    private static FrozenSet<UserId> Snapshot(IEnumerable<UserId>? members)
    {
        if (members is null)
            return FrozenSet<UserId>.Empty;
        var snapshot = new HashSet<UserId>();
        foreach (var member in members)
        {
            if (member == default)
                throw new ArgumentException("Team members cannot contain uninitialized identifiers.", nameof(members));
            if (snapshot.Add(member) && snapshot.Count > MaximumMembers)
                throw new ArgumentException($"A team can have at most {MaximumMembers} members.", nameof(members));
        }
        return snapshot.ToFrozenSet();
    }
}

/// <summary>A team with its members, scope and revision (first revision is 1). Every membership change increments the revision.</summary>
public sealed record StoredTeam(TicketScope Scope, Team Team, long Revision);

/// <summary>A team without its member list, as returned by list queries.</summary>
public sealed record TeamSummary(TicketScope Scope, TeamId Id, string Name, string? Description, int MemberCount, long Revision);
