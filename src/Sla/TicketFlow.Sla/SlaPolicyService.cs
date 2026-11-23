using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>Access-checked SLA policy management: <c>sla:read</c> / <c>sla:manage</c> at workspace level, checked before the store.</summary>
public sealed class SlaPolicyService(ISlaPolicyStore store, IAccessEvaluator evaluator) : ISlaPolicyService
{
    private readonly ISlaPolicyStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<TicketResult<StoredSlaPolicy>> GetAsync(PrincipalRef actor, SlaPolicyId id, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        if (!await PermittedAsync(actor, SlaPermissions.Read, scope, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } stored ? new TicketValue<StoredSlaPolicy>(stored) : TicketFailure.NotFound;
    }

    public async Task<TicketResult<IReadOnlyList<StoredSlaPolicy>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (!await PermittedAsync(actor, SlaPermissions.Read, scope, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return new TicketValue<IReadOnlyList<StoredSlaPolicy>>(await store.ListAsync(scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredSlaPolicy>> CreateAsync(PrincipalRef actor, SlaPolicy policy, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        if (!await PermittedAsync(actor, SlaPermissions.Manage, scope, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return Map(await store.CreateAsync(scope, policy, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredSlaPolicy>> UpdateAsync(PrincipalRef actor, SlaPolicy policy, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await PermittedAsync(actor, SlaPermissions.Manage, scope, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return Map(await store.UpdateAsync(scope, policy, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredSlaPolicy>> DeleteAsync(PrincipalRef actor, SlaPolicyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await PermittedAsync(actor, SlaPermissions.Manage, scope, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return Map(await store.DeleteAsync(scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private static TicketResult<StoredSlaPolicy> Map(StoreWriteResult<StoredSlaPolicy, SlaWriteRejection> result) => result switch
    {
        StoreApplied<StoredSlaPolicy> applied => new TicketValue<StoredSlaPolicy>(applied.Value),
        StoreNotFound => TicketFailure.NotFound,
        StoreConflict conflict => TicketFailure.Conflict(conflict.CurrentRevision),
        StoreRejected<SlaWriteRejection> rejected => new TicketFailure(TicketFailureKind.Rejected, [$"The store rejected the write: {rejected.Reason}."]),
    };

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, TicketScope scope, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, new AccessResource(scope)), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    private static void RequireId(SlaPolicyId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A policy identifier is required.", nameof(id));
    }
}
