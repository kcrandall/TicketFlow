using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>
/// Default <see cref="IRoutingPolicyService"/>. Authorization is workspace-level and precedes existence checks; writes validate that
/// the pool team and overflow teams and inboxes exist.
/// </summary>
public sealed class RoutingPolicyService(IAccessEvaluator access, IRoutingPolicyStore policies, ITeamStore teams, IInboxStore inboxes) : IRoutingPolicyService
{
    public async Task<TicketResult<StoredRoutingPolicy>> GetAsync(PrincipalRef actor, RoutingPolicyId id, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (!id.IsInitialized)
            throw new ArgumentException("A routing policy identifier is required.", nameof(id));
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.PolicyRead, RoutingResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return await policies.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } stored
            ? new TicketValue<StoredRoutingPolicy>(stored)
            : TicketFailure.NotFound;
    }

    public async Task<TicketResult<IReadOnlyList<StoredRoutingPolicy>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.PolicyRead, RoutingResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return new TicketValue<IReadOnlyList<StoredRoutingPolicy>>(await policies.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredRoutingPolicy>> CreateAsync(PrincipalRef actor, RoutingPolicy policy, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        if (await AuthorizeAndValidateAsync(actor, policy, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return RoutingGuards.FromWrite(await policies.CreateAsync(actor.Scope, policy, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredRoutingPolicy>> UpdateAsync(PrincipalRef actor, RoutingPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        if (await AuthorizeAndValidateAsync(actor, policy, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return RoutingGuards.FromWrite(await policies.UpdateAsync(actor.Scope, policy, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredRoutingPolicy>> DeleteAsync(PrincipalRef actor, RoutingPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (!id.IsInitialized)
            throw new ArgumentException("A routing policy identifier is required.", nameof(id));
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.Manage, RoutingResources.Policy(actor.Scope, id), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return RoutingGuards.FromWrite(await policies.DeleteAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TicketFailure?> AuthorizeAndValidateAsync(PrincipalRef actor, RoutingPolicy policy, CancellationToken cancellationToken)
    {
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.Manage, RoutingResources.Policy(actor.Scope, policy.Id), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var scope = actor.Scope;
        var teamSet = new HashSet<TeamId>();
        var inboxSet = new HashSet<InboxId>();
        if (policy.Team is { } team)
            teamSet.Add(team);
        for (var i = 0; i < policy.Overflow.Count; i++)
        {
            switch (policy.Overflow[i].Target)
            {
                case OverflowToTeam toTeam:
                    teamSet.Add(toTeam.Team);
                    break;
                case OverflowToInbox toInbox:
                    inboxSet.Add(toInbox.Inbox);
                    break;
            }
        }
        var teamTasks = new Task<StoredTeam?>[teamSet.Count];
        var ti = 0;
        foreach (var referenced in teamSet)
            teamTasks[ti++] = teams.GetAsync(scope, referenced, cancellationToken);
        var inboxTasks = new Task<StoredInbox?>[inboxSet.Count];
        var ii = 0;
        foreach (var inbox in inboxSet)
            inboxTasks[ii++] = inboxes.GetAsync(scope, inbox, cancellationToken);
        if (teamTasks.Length > 0 && inboxTasks.Length > 0)
            await Task.WhenAll(Task.WhenAll(teamTasks), Task.WhenAll(inboxTasks)).ConfigureAwait(false);
        else if (teamTasks.Length > 0)
            await Task.WhenAll(teamTasks).ConfigureAwait(false);
        else if (inboxTasks.Length > 0)
            await Task.WhenAll(inboxTasks).ConfigureAwait(false);
        for (var i = 0; i < teamTasks.Length; i++)
            if (teamTasks[i].Result is null)
                return TicketFailure.Invalid("A referenced team does not exist.");
        for (var i = 0; i < inboxTasks.Length; i++)
            if (inboxTasks[i].Result is null)
                return TicketFailure.Invalid("A referenced inbox does not exist.");
        return null;
    }
}
