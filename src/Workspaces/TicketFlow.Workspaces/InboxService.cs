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

    public async Task<WorkspaceResult<StoredInbox>> GetAsync(PrincipalRef actor, InboxId id, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        if (await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxRead, WorkspaceResources.Inbox(actor.Scope, id), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await inboxes.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false) is { } inbox
            ? new WorkspaceSucceeded<StoredInbox>(inbox)
            : NotFound;
    }

    public async Task<WorkspaceResult<WorkspacePage<StoredInbox>>> ListAsync(PrincipalRef actor, WorkspacePageRequest? page = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var filter = await planner.PlanAsync(new AccessQuery(actor, WorkspacePermissions.InboxRead, actor.Scope), cancellationToken).ConfigureAwait(false);
        if (filter is DenyAllFilter)
            return new WorkspaceSucceeded<WorkspacePage<StoredInbox>>(new([], false));
        return new WorkspaceSucceeded<WorkspacePage<StoredInbox>>(await inboxes.ListAsync(actor.Scope, filter, page ?? new(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorkspaceResult<StoredInbox>> CreateAsync(PrincipalRef actor, Inbox inbox, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(inbox);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? await ValidateCalendarAsync(actor.Scope, inbox, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        return Map(await inboxes.CreateAsync(actor.Scope, inbox, cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorkspaceResult<StoredInbox>> UpdateAsync(PrincipalRef actor, Inbox inbox, long expectedRevision, CancellationToken cancellationToken = default)
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

    public async Task<WorkspaceResult<StoredInbox>> DeleteAsync(PrincipalRef actor, InboxId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        RequireId(id);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.InboxManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision);
        if (refused is not null)
            return refused;
        return Map(await inboxes.DeleteAsync(actor.Scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorkspaceResult<StoredAccessGrant>> AddAgentAsync(PrincipalRef actor, InboxId inbox, UserId agent, RoleId? role = null, CancellationToken cancellationToken = default)
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

    public async Task<WorkspaceResult<StoredAccessGrant>> RemoveAgentAsync(PrincipalRef actor, InboxId inbox, UserId agent, RoleId? role = null, CancellationToken cancellationToken = default)
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

    private async Task<WorkspaceRefused?> ValidateCalendarAsync(TicketScope scope, Inbox inbox, CancellationToken cancellationToken) =>
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
