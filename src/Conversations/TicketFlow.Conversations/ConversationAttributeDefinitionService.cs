using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>Default <see cref="IConversationAttributeDefinitionService"/>: reads need <c>conversation:read</c>, writes <c>conversation-attribute:manage</c> at workspace level.</summary>
public sealed class ConversationAttributeDefinitionService(IConversationAttributeDefinitionStore store, IAccessEvaluator evaluator) : IConversationAttributeDefinitionService
{
    private readonly IConversationAttributeDefinitionStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));

    public async Task<ConversationResult<IReadOnlyList<StoredConversationAttributeDefinition>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (!await PermittedAsync(actor, ConversationPermissions.Read, scope, cancellationToken).ConfigureAwait(false))
            return ConversationRefused.Forbidden;
        return new ConversationValue<IReadOnlyList<StoredConversationAttributeDefinition>>(await store.ListAsync(scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ConversationResult<StoredConversationAttributeDefinition>> CreateAsync(PrincipalRef actor, ConversationAttributeDefinition definition, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(definition);
        if (!await PermittedAsync(actor, ConversationPermissions.ManageAttributes, scope, cancellationToken).ConfigureAwait(false))
            return ConversationRefused.Forbidden;
        return Map(await store.CreateAsync(scope, definition, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ConversationResult<StoredConversationAttributeDefinition>> UpdateAsync(PrincipalRef actor, ConversationAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await PermittedAsync(actor, ConversationPermissions.ManageAttributes, scope, cancellationToken).ConfigureAwait(false))
            return ConversationRefused.Forbidden;
        return Map(await store.UpdateAsync(scope, definition, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ConversationResult<StoredConversationAttributeDefinition>> DeleteAsync(PrincipalRef actor, string key, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ConversationAttributeDefinition.RequireKey(key, nameof(key));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await PermittedAsync(actor, ConversationPermissions.ManageAttributes, scope, cancellationToken).ConfigureAwait(false))
            return ConversationRefused.Forbidden;
        return Map(await store.DeleteAsync(scope, key, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    private static ConversationResult<StoredConversationAttributeDefinition> Map(ConversationAttributeDefinitionWriteResult result) => result switch
    {
        ConversationAttributeDefinitionApplied applied => new ConversationValue<StoredConversationAttributeDefinition>(applied.Value),
        ConversationWriteNotFound => ConversationNotFound.Instance,
        ConversationWriteConflict conflict => new ConversationConflict(conflict.CurrentRevision),
        ConversationWriteRejected rejected => new ConversationRefused(ConversationRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]),
    };

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, TicketScope scope, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, new AccessResource(scope)), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }
}
