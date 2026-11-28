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

    public async Task<TicketFailure?> CheckMembershipChangeAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> added, IReadOnlyCollection<UserId> removed, CancellationToken cancellationToken)
    {
        if (added.Count == 0 && removed.Count == 0)
            return null;
        var teamGrantsTask = TeamGrantsAsync(actor, team, cancellationToken);
        var addedGrantsTask = added.Count > 0 ? MemberGrantsAsync(actor, added, cancellationToken) : null;
        var removedGrantsTask = removed.Count > 0 ? MemberGrantsAsync(actor, removed, cancellationToken) : null;
        if (addedGrantsTask is not null && removedGrantsTask is not null)
            await Task.WhenAll(teamGrantsTask, addedGrantsTask, removedGrantsTask).ConfigureAwait(false);
        else if (addedGrantsTask is not null)
            await Task.WhenAll(teamGrantsTask, addedGrantsTask).ConfigureAwait(false);
        else if (removedGrantsTask is not null)
            await Task.WhenAll(teamGrantsTask, removedGrantsTask).ConfigureAwait(false);
        else
            await teamGrantsTask.ConfigureAwait(false);
        var teamGrants = await teamGrantsTask.ConfigureAwait(false);
        if (addedGrantsTask is not null && await CheckAdditionsWithGrantsAsync(actor, team, added, teamGrants, await addedGrantsTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is { } escalation)
            return escalation;
        if (removedGrantsTask is not null && await CheckRemovalsWithGrantsAsync(actor, teamGrants, await removedGrantsTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is { } lifted)
            return lifted;
        return null;
    }

    public async Task<TicketFailure?> CheckAdditionsAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> added, CancellationToken cancellationToken)
    {
        if (added.Count == 0)
            return null;
        var teamGrantsTask = TeamGrantsAsync(actor, team, cancellationToken);
        var addedGrantsTask = MemberGrantsAsync(actor, added, cancellationToken);
        await Task.WhenAll(teamGrantsTask, addedGrantsTask).ConfigureAwait(false);
        return await CheckAdditionsWithGrantsAsync(actor, team, added, await teamGrantsTask.ConfigureAwait(false), await addedGrantsTask.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<TicketFailure?> CheckAdditionsWithGrantsAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> added, IReadOnlyList<StoredAccessGrant> teamGrants, List<AccessGrant> memberGrants, CancellationToken cancellationToken)
    {
        var teamResource = new AccessResource(actor.Scope, team: team);
        var conferrals = new List<GrantConferral>(teamGrants.Count);
        for (var i = 0; i < teamGrants.Count; i++)
            if (teamGrants[i].Grant.Effect == GrantEffect.Allow)
                conferrals.Add(new GrantConferral(teamGrants[i].Grant));
        for (var i = 0; i < memberGrants.Count; i++)
        {
            var grant = memberGrants[i];
            if (grant.Effect == GrantEffect.Allow && UsesPrincipalTeams(grant.Condition))
                conferrals.Add(new GrantConferral(grant, teamResource));
        }
        foreach (var batch in conferrals.Chunk(CheckBatch))
            if (Refuse(await administrator.CheckCanConferAsync(actor, batch, cancellationToken).ConfigureAwait(false)) is { } refused)
                return refused;
        foreach (var member in added)
        {
            if (Refuse(await administrator.CheckCanConferTeamFederatedRolesAsync(actor, member, [team], cancellationToken).ConfigureAwait(false)) is { } refused)
                return refused;
            break;
        }
        return null;
    }

    private async Task<TicketFailure?> CheckRemovalsWithGrantsAsync(PrincipalRef actor, IReadOnlyList<StoredAccessGrant> teamGrants, List<AccessGrant> memberGrants, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var revoked = new List<AccessGrant>(teamGrants.Count);
        for (var i = 0; i < teamGrants.Count; i++)
        {
            var grant = teamGrants[i].Grant;
            if (grant.Effect == GrantEffect.Deny && Live(grant, now))
                revoked.Add(grant);
        }
        for (var i = 0; i < memberGrants.Count; i++)
        {
            var grant = memberGrants[i];
            if (grant.Effect == GrantEffect.Deny && Live(grant, now) && UsesPrincipalTeams(grant.Condition))
                revoked.Add(grant);
        }
        return await CheckRevokeAsync(actor, revoked, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TicketFailure?> CheckRevokeAsync(PrincipalRef actor, IReadOnlyCollection<AccessGrant> revoked, CancellationToken cancellationToken)
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

    public static TicketFailure? Refuse(AccessCheckResult result) => result switch
    {
        AccessCheckPassed => null,
        TicketFailure refused => new TicketFailure(TicketFailureKind.Forbidden, refused.Errors),
    };

    private async Task<List<AccessGrant>> MemberGrantsAsync(PrincipalRef actor, IReadOnlyCollection<UserId> members, CancellationToken cancellationToken)
    {
        var partition = AccessPartition.From(actor.Scope);
        var workspace = actor.Scope.WorkspaceId;
        if (members.Count <= AccessLimits.MaximumSubjectsPerQuery)
        {
            var subjects = new GrantSubject[members.Count];
            var i = 0;
            foreach (var member in members)
                subjects[i++] = member;
            var stored = await grants.ListForSubjectsAsync(partition, workspace, subjects, cancellationToken).ConfigureAwait(false);
            var single = new List<AccessGrant>(stored.Count);
            for (var j = 0; j < stored.Count; j++)
                single.Add(stored[j].Grant);
            return single;
        }
        var tasks = new List<Task<IReadOnlyList<StoredAccessGrant>>>();
        foreach (var batch in members.Chunk(AccessLimits.MaximumSubjectsPerQuery))
        {
            var subjects = new GrantSubject[batch.Length];
            for (var i = 0; i < batch.Length; i++)
                subjects[i] = batch[i];
            tasks.Add(grants.ListForSubjectsAsync(partition, workspace, subjects, cancellationToken));
        }
        var pages = await Task.WhenAll(tasks).ConfigureAwait(false);
        var total = 0;
        for (var i = 0; i < pages.Length; i++)
            total += pages[i].Count;
        var result = new List<AccessGrant>(total);
        for (var i = 0; i < pages.Length; i++)
        {
            var page = pages[i];
            for (var j = 0; j < page.Count; j++)
                result.Add(page[j].Grant);
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
