using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// Keeps team membership changes within the actor's access-administration authority, using
/// <see cref="IAccessAdministrator.CheckCanConferAsync"/> and <see cref="IAccessAdministrator.CheckCanRevokeAsync"/> (standard grants
/// only, tenant authority for tenant grants, actor grants loaded once, no audit records):
/// <list type="bullet">
/// <item>Adding members confers every allow grant whose subject is the team, and makes each added member's own allow grants that use
/// <see cref="TeamInPrincipalTeamsCondition"/> apply to the team's resources; the actor must be able to confer both.</item>
/// <item>Removing members (or deleting the team) stops live deny grants whose subject is the team from applying to them, and lifts
/// removed members' own deny grants that use <see cref="TeamInPrincipalTeamsCondition"/> on the team's resources; the actor needs the
/// authority to revoke those grants.</item>
/// </list>
/// </summary>
internal sealed class TeamGrantPolicy(IAccessAdministrator administrator, IAccessGrantStore grants, TimeProvider time)
{
    private const int CheckBatch = 1024;

    public Task<IReadOnlyList<StoredAccessGrant>> TeamGrantsAsync(PrincipalRef actor, TeamId team, CancellationToken cancellationToken) =>
        grants.ListForSubjectsAsync(AccessPartition.From(actor.Scope), actor.Scope.WorkspaceId, [team], cancellationToken);

    public async Task<WorkspaceRefused?> CheckMembershipChangeAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> added, IReadOnlyCollection<UserId> removed, CancellationToken cancellationToken)
    {
        if (added.Count == 0 && removed.Count == 0)
            return null;
        var teamGrants = await TeamGrantsAsync(actor, team, cancellationToken).ConfigureAwait(false);
        if (added.Count > 0 && await CheckAdditionsWithTeamGrantsAsync(actor, team, added, teamGrants, cancellationToken).ConfigureAwait(false) is { } escalation)
            return escalation;
        if (removed.Count > 0 && await CheckRemovalsWithTeamGrantsAsync(actor, team, removed, teamGrants, cancellationToken).ConfigureAwait(false) is { } lifted)
            return lifted;
        return null;
    }

    public async Task<WorkspaceRefused?> CheckAdditionsAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> added, CancellationToken cancellationToken)
    {
        if (added.Count == 0)
            return null;
        var teamGrants = await TeamGrantsAsync(actor, team, cancellationToken).ConfigureAwait(false);
        return await CheckAdditionsWithTeamGrantsAsync(actor, team, added, teamGrants, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkspaceRefused?> CheckAdditionsWithTeamGrantsAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> added, IReadOnlyList<StoredAccessGrant> teamGrants, CancellationToken cancellationToken)
    {
        var teamResource = new AccessResource(actor.Scope, team: team);
        var conferrals = new List<GrantConferral>(teamGrants.Count);
        for (var i = 0; i < teamGrants.Count; i++)
            if (teamGrants[i].Grant.Effect == GrantEffect.Allow)
                conferrals.Add(new GrantConferral(teamGrants[i].Grant));
        foreach (var grant in await MemberGrantsAsync(actor, added, cancellationToken).ConfigureAwait(false))
            if (grant.Effect == GrantEffect.Allow && UsesPrincipalTeams(grant.Condition))
                conferrals.Add(new GrantConferral(grant, teamResource));
        foreach (var batch in conferrals.Chunk(CheckBatch))
            if (Refuse(await administrator.CheckCanConferAsync(actor, batch, cancellationToken).ConfigureAwait(false)) is { } refused)
                return refused;
        return null;
    }

    private async Task<WorkspaceRefused?> CheckRemovalsWithTeamGrantsAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> removed, IReadOnlyList<StoredAccessGrant> teamGrants, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var revoked = new List<AccessGrant>(teamGrants.Count);
        for (var i = 0; i < teamGrants.Count; i++)
        {
            var grant = teamGrants[i].Grant;
            if (grant.Effect == GrantEffect.Deny && Live(grant, now))
                revoked.Add(grant);
        }
        foreach (var grant in await MemberGrantsAsync(actor, removed, cancellationToken).ConfigureAwait(false))
            if (grant.Effect == GrantEffect.Deny && Live(grant, now) && UsesPrincipalTeams(grant.Condition))
                revoked.Add(grant);
        return await CheckRevokeAsync(actor, revoked, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceRefused?> CheckRevokeAsync(PrincipalRef actor, IReadOnlyCollection<AccessGrant> revoked, CancellationToken cancellationToken)
    {
        foreach (var batch in revoked.Chunk(CheckBatch))
            if (Refuse(await administrator.CheckCanRevokeAsync(actor, batch, cancellationToken).ConfigureAwait(false)) is { } refused)
                return refused;
        return null;
    }

    /// <summary>Revokes grants through the administrator (authorized and audited). Concurrently changed or removed grants are skipped.</summary>
    public Task RevokeAsync(PrincipalRef actor, IReadOnlyList<StoredAccessGrant> stored, CancellationToken cancellationToken)
    {
        if (stored.Count == 0)
            return Task.CompletedTask;
        var targets = new (GrantId Id, long ExpectedRevision)[stored.Count];
        for (var i = 0; i < stored.Count; i++)
            targets[i] = (stored[i].Grant.Id, stored[i].Revision);
        return administrator.RevokeGrantsAsync(actor, targets, cancellationToken);
    }

    public static WorkspaceRefused? Refuse(AccessCheckResult result) => result switch
    {
        AccessCheckPassed => null,
        AccessChangeRefused refused => new WorkspaceRefused(WorkspaceRefusal.Forbidden, refused.Errors),
    };

    private async Task<List<AccessGrant>> MemberGrantsAsync(PrincipalRef actor, IReadOnlyCollection<UserId> members, CancellationToken cancellationToken)
    {
        var result = new List<AccessGrant>();
        foreach (var batch in members.Chunk(AccessLimits.MaximumSubjectsPerQuery))
        {
            var stored = await grants.ListForSubjectsAsync(AccessPartition.From(actor.Scope), actor.Scope.WorkspaceId,
                [.. batch.Select(member => (GrantSubject)member)], cancellationToken).ConfigureAwait(false);
            result.AddRange(stored.Select(entry => entry.Grant));
        }
        return result;
    }

    private static bool Live(AccessGrant grant, DateTimeOffset now) => grant.ValidUntil is not { } until || until > now;

    private static bool UsesPrincipalTeams(AccessCondition? condition) => condition switch
    {
        null => false,
        TeamInPrincipalTeamsCondition => true,
        AndCondition and => and.Operands.Any(UsesPrincipalTeams),
        OrCondition or => or.Operands.Any(UsesPrincipalTeams),
        NotCondition not => UsesPrincipalTeams(not.Operand),
        AssigneeIsSelfCondition or AttributeEqualsCondition or AttributeInCondition or HasLabelCondition or TimeWindowCondition => false,
    };
}
