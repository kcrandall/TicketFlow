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

    public async Task<WorkspaceRefused?> CheckAdditionsAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> added, CancellationToken cancellationToken)
    {
        if (added.Count == 0)
            return null;
        var teamResource = new AccessResource(actor.Scope, team: team);
        var conferrals = (await TeamGrantsAsync(actor, team, cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.Grant.Effect == GrantEffect.Allow)
            .Select(entry => new GrantConferral(entry.Grant))
            .ToList();
        foreach (var grant in await MemberGrantsAsync(actor, added, cancellationToken).ConfigureAwait(false))
            if (grant.Effect == GrantEffect.Allow && UsesPrincipalTeams(grant.Condition))
                conferrals.Add(new GrantConferral(grant, teamResource));
        foreach (var batch in conferrals.Chunk(CheckBatch))
            if (Refuse(await administrator.CheckCanConferAsync(actor, batch, cancellationToken).ConfigureAwait(false)) is { } refused)
                return refused;
        return null;
    }

    public async Task<WorkspaceRefused?> CheckRemovalsAsync(PrincipalRef actor, TeamId team, IReadOnlyCollection<UserId> removed, CancellationToken cancellationToken)
    {
        if (removed.Count == 0)
            return null;
        var now = time.GetUtcNow();
        var revoked = (await TeamGrantsAsync(actor, team, cancellationToken).ConfigureAwait(false))
            .Select(entry => entry.Grant)
            .Where(grant => grant.Effect == GrantEffect.Deny && Live(grant, now))
            .ToList();
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
    public async Task RevokeAsync(PrincipalRef actor, IEnumerable<StoredAccessGrant> stored, CancellationToken cancellationToken)
    {
        foreach (var entry in stored)
            await administrator.RevokeGrantAsync(actor, entry.Grant.Id, entry.Revision, cancellationToken).ConfigureAwait(false);
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
