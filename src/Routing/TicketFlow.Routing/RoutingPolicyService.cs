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
    public async Task<RoutingResult<StoredRoutingPolicy>> GetAsync(PrincipalRef actor, RoutingPolicyId id, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (!id.IsInitialized)
            throw new ArgumentException("A routing policy identifier is required.", nameof(id));
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.PolicyRead, RoutingResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false))
            return RoutingRefused.Forbidden;
        return await policies.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } stored
            ? new RoutingValue<StoredRoutingPolicy>(stored)
            : RoutingNotFound.Instance;
    }

    public async Task<RoutingResult<IReadOnlyList<StoredRoutingPolicy>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.PolicyRead, RoutingResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false))
            return RoutingRefused.Forbidden;
        return new RoutingValue<IReadOnlyList<StoredRoutingPolicy>>(await policies.ListAsync(actor.Scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RoutingResult<StoredRoutingPolicy>> CreateAsync(PrincipalRef actor, RoutingPolicy policy, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        if (await AuthorizeAndValidateAsync(actor, policy, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return RoutingGuards.FromWrite(await policies.CreateAsync(actor.Scope, policy, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RoutingResult<StoredRoutingPolicy>> UpdateAsync(PrincipalRef actor, RoutingPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        if (await AuthorizeAndValidateAsync(actor, policy, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return RoutingGuards.FromWrite(await policies.UpdateAsync(actor.Scope, policy, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RoutingResult<StoredRoutingPolicy>> DeleteAsync(PrincipalRef actor, RoutingPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (!id.IsInitialized)
            throw new ArgumentException("A routing policy identifier is required.", nameof(id));
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.Manage, RoutingResources.Policy(actor.Scope, id), cancellationToken).ConfigureAwait(false))
            return RoutingRefused.Forbidden;
        return RoutingGuards.FromWrite(await policies.DeleteAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private async Task<RoutingRefused?> AuthorizeAndValidateAsync(PrincipalRef actor, RoutingPolicy policy, CancellationToken cancellationToken)
    {
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.Manage, RoutingResources.Policy(actor.Scope, policy.Id), cancellationToken).ConfigureAwait(false))
            return RoutingRefused.Forbidden;
        var scope = actor.Scope;
        var referencedTeams = policy.Overflow.Select(rule => rule.Target.Value).OfType<OverflowToTeam>().Select(target => target.Team).ToList();
        if (policy.Team is { } team)
            referencedTeams.Add(team);
        foreach (var referenced in referencedTeams.Distinct())
            if (await teams.GetAsync(scope, referenced, cancellationToken).ConfigureAwait(false) is null)
                return RoutingRefused.Invalid("A referenced team does not exist.");
        foreach (var inbox in policy.Overflow.Select(rule => rule.Target.Value).OfType<OverflowToInbox>().Select(target => target.Inbox).Distinct())
            if (await inboxes.GetAsync(scope, inbox, cancellationToken).ConfigureAwait(false) is null)
                return RoutingRefused.Invalid("A referenced inbox does not exist.");
        return null;
    }
}
