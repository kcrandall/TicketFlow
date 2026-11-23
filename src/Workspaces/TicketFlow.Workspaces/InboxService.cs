using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.Workspaces.WorkspaceGuards;

namespace TicketFlow.Workspaces;

/// <summary>
/// Default <see cref="IInboxService"/>. Every call authorizes through Access before touching the store; lists use the access query
/// planner and push the filter into the store. Inbox agents are inbox-scoped grants written through <see cref="IAccessAdministrator"/>,
/// which authorizes, prevents escalation and audits the change.
/// </summary>
public sealed class InboxService : IInboxService
{
    private readonly IInboxStore inboxes;
    private readonly IBusinessCalendarStore calendars;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;
    private readonly IAccessAdministrator administrator;
    private readonly IAccessGrantStore grants;

    public InboxService(IInboxStore inboxes, IBusinessCalendarStore calendars, IAccessEvaluator evaluator, IAccessQueryPlanner planner, IAccessAdministrator administrator, IAccessGrantStore grants)
    {
        this.inboxes = inboxes ?? throw new ArgumentNullException(nameof(inboxes));
        this.calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.administrator = administrator ?? throw new ArgumentNullException(nameof(administrator));
        this.grants = grants ?? throw new ArgumentNullException(nameof(grants));
    }

    public async Task<TicketResult<StoredInbox>> GetAsync(PrincipalRef actor, InboxId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        if (await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxRead, WorkspaceResources.Inbox(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await inboxes.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } inbox
            ? new TicketValue<StoredInbox>(inbox)
            : NotFound;
    }

    public async Task<TicketResult<WorkspacePage<StoredInbox>>> ListAsync(PrincipalRef actor, WorkspacePageRequest? page = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var filter = await planner.PlanAsync(new AccessQuery(actor, WorkspacePermissions.InboxRead, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new TicketValue<WorkspacePage<StoredInbox>>(new([], false));
        return new TicketValue<WorkspacePage<StoredInbox>>(await inboxes.ListAsync(actor.Scope, filter, page ?? new(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredInbox>> CreateAsync(PrincipalRef actor, Inbox inbox, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(inbox);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? await ValidateCalendarAsync(actor.Scope, inbox, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        return Map(await inboxes.CreateAsync(actor.Scope, inbox, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredInbox>> UpdateAsync(PrincipalRef actor, Inbox inbox, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(inbox);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxManage, WorkspaceResources.Inbox(actor.Scope, inbox.Id), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision)
            ?? await ValidateCalendarAsync(actor.Scope, inbox, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        return Map(await inboxes.UpdateAsync(actor.Scope, inbox, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Deletes the inbox and then revokes its inbox agent grants (<see cref="InboxAgentGrants"/>) through
    /// <see cref="IAccessAdministrator"/>. The actor needs the authority to revoke them (checked before anything is written); other
    /// grants that name the inbox are left in place.
    /// </summary>
    public async Task<TicketResult<StoredInbox>> DeleteAsync(PrincipalRef actor, InboxId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        var current = await inboxes.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (Fence(current?.Revision, expectedRevision) is { } fenced)
            return fenced;
        var agentGrants = await AgentGrantsAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (agentGrants.Count > 0
            && TeamGrantPolicy.Refuse(await administrator.CheckCanRevokeAsync(actor, [.. agentGrants.Select(entry => entry.Grant)], cancellationToken).ConfigureAwait(false)) is { } forbidden)
            return forbidden;
        var result = await inboxes.DeleteAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (result.Value is StoreApplied<StoredInbox> && agentGrants.Count > 0)
        {
            var targets = new (GrantId Id, long ExpectedRevision)[agentGrants.Count];
            for (var i = 0; i < agentGrants.Count; i++)
                targets[i] = (agentGrants[i].Grant.Id, agentGrants[i].Revision);
            await administrator.RevokeGrantsAsync(actor, targets, cancellationToken).ConfigureAwait(false);
        }
        return Map(result);
    }

    /// <summary>Every grant on the inbox whose identifier and shape match <see cref="InboxAgentGrants.Create"/>.</summary>
    private async Task<List<StoredAccessGrant>> AgentGrantsAsync(TicketScope scope, InboxId inbox, CancellationToken cancellationToken)
    {
        var partition = AccessPartition.From(scope);
        var target = new InboxGrantScope(scope.WorkspaceId, inbox);
        var result = new List<StoredAccessGrant>();
        GrantId? after = null;
        while (true)
        {
            var page = await grants.ListForTargetAsync(partition, target, AccessLimits.MaximumTargetPage, after, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page)
                if (entry.Grant.Id.Value.StartsWith(InboxAgentGrants.Prefix, StringComparison.Ordinal) && entry.Grant.Subject.Value is UserId agent
                    && InboxAgentGrants.Matches(entry.Grant, scope, inbox, agent, entry.Grant.Role))
                    result.Add(entry);
            if (page.Count < AccessLimits.MaximumTargetPage)
                return result;
            after = page[^1].Grant.Id;
        }
    }

    public async Task<TicketResult<StoredAccessGrant>> AddAgentAsync(PrincipalRef actor, InboxId inbox, UserId agent, RoleId? role = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(inbox);
        var (user, granted) = RequireAgent(agent, role);
        if (await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxManage, WorkspaceResources.Inbox(actor.Scope, inbox), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        if (await inboxes.GetAsync(actor.Scope, inbox, cancellationToken).ConfigureAwait(false) is null)
            return NotFound;
        var grant = InboxAgentGrants.Create(actor.Scope, inbox, user, granted);
        return Map(await administrator.CreateGrantAsync(actor, grant, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredAccessGrant>> RemoveAgentAsync(PrincipalRef actor, InboxId inbox, UserId agent, RoleId? role = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(inbox);
        var (user, granted) = RequireAgent(agent, role);
        if (await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxManage, WorkspaceResources.Inbox(actor.Scope, inbox), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        var id = InboxAgentGrants.For(actor.Scope.WorkspaceId, inbox, user, granted);
        var existing = await grants.GetAsync(AccessPartition.From(actor.Scope), id, cancellationToken).ConfigureAwait(false);
        // Only grants of the exact shape AddAgentAsync creates are removed through this convenience path.
        if (existing is null || !InboxAgentGrants.Matches(existing.Grant, actor.Scope, inbox, user, granted))
            return NotFound;
        return Map(await administrator.RevokeGrantAsync(actor, id, existing.Revision, cancellationToken).ConfigureAwait(false));
    }

    private async Task<TicketFailure?> ValidateCalendarAsync(TicketScope scope, Inbox inbox, CancellationToken cancellationToken) =>
        inbox.Calendar is { } calendar && await calendars.GetAsync(scope, calendar, cancellationToken).ConfigureAwait(false) is null
            ? Invalid("The referenced business calendar does not exist in this workspace.")
            : null;

    private static (UserId Agent, RoleId Role) RequireAgent(UserId agent, RoleId? role)
    {
        if (agent == default)
            throw new ArgumentException("An agent identifier is required.", nameof(agent));
        if (role is { IsInitialized: false })
            throw new ArgumentException("The role must be initialized when supplied.", nameof(role));
        return (agent, role ?? BuiltInRoles.Agent.Id);
    }

    private static void RequireId(InboxId id)
    {
        if (id == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(id));
    }
}
