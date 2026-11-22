using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.Workspaces.WorkspaceGuards;

namespace TicketFlow.Workspaces;

/// <summary>Default <see cref="IWorkspaceSettingsService"/>: authorizes through Access before every store call.</summary>
public sealed class WorkspaceSettingsService(IWorkspaceSettingsStore store, IAccessEvaluator evaluator) : IWorkspaceSettingsService
{
    private readonly IWorkspaceSettingsStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<WorkspaceResult<StoredWorkspaceSettings>> GetAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        if (await AuthorizeAsync(evaluator, actor, WorkspacePermissions.WorkspaceRead, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        return await store.GetAsync(actor.Scope, cancellationToken).ConfigureAwait(false) is { } settings
            ? new WorkspaceSucceeded<StoredWorkspaceSettings>(settings)
            : NotFound;
    }

    public async Task<WorkspaceResult<StoredWorkspaceSettings>> CreateAsync(PrincipalRef actor, WorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(settings);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.WorkspaceManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidateTimeZone(settings.TimeZoneId);
        if (refused is not null)
            return refused;
        return Map(await store.CreateAsync(actor.Scope, settings, cancellationToken).ConfigureAwait(false));
    }

    public async Task<WorkspaceResult<StoredWorkspaceSettings>> UpdateAsync(PrincipalRef actor, WorkspaceSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        ArgumentNullException.ThrowIfNull(settings);
        var refused = await AuthorizeAsync(evaluator, actor, WorkspacePermissions.WorkspaceManage, WorkspaceResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false)
            ?? ValidateRevision(expectedRevision)
            ?? ValidateTimeZone(settings.TimeZoneId);
        if (refused is not null)
            return refused;
        return Map(await store.UpdateAsync(actor.Scope, settings, expectedRevision, cancellationToken).ConfigureAwait(false));
    }
}
