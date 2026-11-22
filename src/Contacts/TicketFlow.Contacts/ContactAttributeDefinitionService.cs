using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>Default <see cref="IContactAttributeDefinitionService"/>: fenced before authorization, authorized through Access.</summary>
public sealed class ContactAttributeDefinitionService(IContactAttributeDefinitionStore store, IAccessEvaluator evaluator) : IContactAttributeDefinitionService
{
    private readonly IContactAttributeDefinitionStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<ContactResult<IReadOnlyList<StoredContactAttributeDefinition>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (!await PermittedAsync(actor, ContactPermissions.Read, cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        return new ContactApplied<IReadOnlyList<StoredContactAttributeDefinition>>(await store.ListAsync(scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ContactResult<StoredContactAttributeDefinition>> CreateAsync(PrincipalRef actor, ContactAttributeDefinition definition, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(definition);
        if (!await PermittedAsync(actor, ContactPermissions.ManageAttributes, cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        return Map(await store.CreateAsync(scope, definition, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ContactResult<StoredContactAttributeDefinition>> UpdateAsync(PrincipalRef actor, ContactAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var existing = await store.GetAsync(scope, definition.Key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return ContactRefused.NotFound;
        if (existing.Revision != expectedRevision)
            return new ContactRefused(ContactRefusal.Conflict, [], existing.Revision);
        if (!await PermittedAsync(actor, ContactPermissions.ManageAttributes, cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        return Map(await store.UpdateAsync(scope, definition, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ContactResult<StoredContactAttributeDefinition>> DeleteAsync(PrincipalRef actor, string key, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ContactAttributeDefinition.RequireKey(key, nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var existing = await store.GetAsync(scope, key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return ContactRefused.NotFound;
        if (existing.Revision != expectedRevision)
            return new ContactRefused(ContactRefusal.Conflict, [], existing.Revision);
        if (!await PermittedAsync(actor, ContactPermissions.ManageAttributes, cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        return Map(await store.DeleteAsync(scope, key, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, ContactAccess.Workspace(actor.Scope)), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private static ContactResult<StoredContactAttributeDefinition> Map(AttributeDefinitionWriteResult result) => result switch
    {
        AttributeDefinitionApplied applied => new ContactApplied<StoredContactAttributeDefinition>(applied.Value),
        AttributeDefinitionNotFound => ContactRefused.NotFound,
        AttributeDefinitionConflict conflict => new ContactRefused(ContactRefusal.Conflict, [], conflict.CurrentRevision),
        ContactWriteRejected rejected => new ContactRefused(ContactRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]),
    };
}
