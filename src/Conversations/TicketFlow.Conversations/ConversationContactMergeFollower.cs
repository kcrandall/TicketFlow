using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Keeps conversations pointing at the surviving contact. On <see cref="ContactChangeKind.Merged"/> every conversation of the source is
/// re-pointed to the target (a <see cref="ContactChanged"/> event and a <see cref="ContactRedirect"/> recording the merge); on
/// <see cref="ContactChangeKind.Unmerged"/> exactly the conversations re-pointed by that merge return to their previous contact.
/// Both are idempotent (keyed by merge identifier, and merges whose record was removed by an unmerge are skipped), so replays and
/// duplicate notifications are no-ops. Runs as a trusted internal
/// component: the contact merge itself was authorized (<c>contact:merge</c>); conversation writes go to the store directly with the
/// system actor <c>contact-merge</c>. Use it in process (registered as an <see cref="IContactChangeObserver"/>) and/or durably with
/// <see cref="CatchUpAsync"/> over the contact change outbox.
/// </summary>
public sealed class ConversationContactMergeFollower : IContactChangeObserver
{
    /// <summary>The system actor recorded on re-pointing events.</summary>
    public static AccessActor Actor { get; } = new(AccessActorKind.System, "contact-merge");

    private readonly IConversationStore store;
    private readonly IContactStore contacts;
    private readonly ConversationsOptions options;
    private readonly TimeProvider time;

    public ConversationContactMergeFollower(IConversationStore store, IContactStore contacts, IOptions<ConversationsOptions> options, TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task OnChangedAsync(IReadOnlyList<ContactChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
            await ApplyAsync(change, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies merge and unmerge changes of <paramref name="scope"/> with a sequence greater than <paramref name="afterSequence"/>, in
    /// order, reading at most <paramref name="maximumChanges"/> changes. Returns the last sequence read (the caller's durable checkpoint;
    /// replaying from an older checkpoint is safe).
    /// </summary>
    public async Task<long> CatchUpAsync(TicketScope scope, long afterSequence, int maximumChanges = 10_000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumChanges, 1);
        var position = afterSequence;
        var read = 0;
        while (read < maximumChanges)
        {
            var batch = await contacts.ReadChangesAsync(scope, position, Math.Min(IContactStore.MaximumChangesPerRead, maximumChanges - read), cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
                break;
            foreach (var change in batch)
            {
                if (change.Scope != scope || change.Sequence <= position)
                    throw new InvalidOperationException("The contact store returned changes out of order or outside the scope.");
                await ApplyAsync(change, cancellationToken).ConfigureAwait(false);
                position = change.Sequence;
            }
            read += batch.Count;
        }
        return position;
    }

    private async Task ApplyAsync(ContactChange change, CancellationToken cancellationToken)
    {
        // A merge that has since been undone (its record is gone) is skipped, so replaying history never re-points again.
        if (change.Event is { Kind: ContactChangeKind.Merged, Merge: { } active }
            && await contacts.GetMergeAsync(change.Scope, active, cancellationToken).ConfigureAwait(false) is null)
            return;
        await RepointForAsync(change, cancellationToken).ConfigureAwait(false);
    }

    private Task RepointForAsync(ContactChange change, CancellationToken cancellationToken) => change.Event.Kind switch
    {
        ContactChangeKind.Merged when change.Event.Related is { } target && change.Event.Merge is { } merge =>
            RepointAsync(change.Scope, change.Event.Contact, conversation => conversation.ContactRedirects.Any(redirect => redirect.Merge == merge)
                ? null
                : (conversation.With(contact: target, contactRedirects: [.. conversation.ContactRedirects, new ContactRedirect(merge, conversation.Contact)]),
                    new ContactChanged(conversation.Contact, target, merge)), cancellationToken),
        ContactChangeKind.Unmerged when change.Event.Related is { } target && change.Event.Merge is { } merge =>
            RepointAsync(change.Scope, target, conversation => conversation.ContactRedirects.FirstOrDefault(redirect => redirect.Merge == merge) is not { } redirect
                ? null
                : (conversation.With(contact: redirect.Previous, contactRedirects: conversation.ContactRedirects.Where(item => item.Merge != merge)),
                    new ContactChanged(conversation.Contact, redirect.Previous, merge)), cancellationToken),
        _ => Task.CompletedTask,
    };

    /// <summary>Applies <paramref name="repoint"/> to every conversation currently pointing at <paramref name="contact"/>.</summary>
    private async Task RepointAsync(TicketScope scope, ContactId contact, Func<Conversation, (Conversation Next, ContactChanged Event)?> repoint, CancellationToken cancellationToken)
    {
        var query = new ConversationListQuery(contact: contact, limit: ConversationListQuery.MaximumLimit);
        while (true)
        {
            var page = await store.ListAsync(scope, query, AllowAllFilter.Instance, cancellationToken).ConfigureAwait(false);
            foreach (var stored in page.Conversations)
                await RepointOneAsync(scope, stored.Id, contact, repoint, cancellationToken).ConfigureAwait(false);
            if (page.Next is null)
                return;
            query = query.WithAfter(page.Next);
        }
    }

    private async Task RepointOneAsync(TicketScope scope, ConversationId id, ContactId expectedContact, Func<Conversation, (Conversation Next, ContactChanged Event)?> repoint,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            if (stored is null || stored.Conversation.Contact != expectedContact || repoint(stored.Conversation) is not { } plan)
                return;
            var now = time.GetUtcNow();
            var sequence = stored.Conversation.LastSequence + 1;
            Conversation next;
            try
            {
                next = plan.Next.With(lastSequence: sequence);
            }
            catch (ArgumentException error)
            {
                throw new InvalidOperationException("The conversation cannot record another contact redirect.", error);
            }
            var timelineEvent = new TimelineEvent(id, sequence, Guid.CreateVersion7(), now, Actor, plan.Event);
            var change = new ConversationChange(Guid.CreateVersion7(), scope, id, next.Inbox, stored.Revision + 1, ConversationChangeKind.EventsAppended,
                [ConversationEventSummary.From(timelineEvent)], null, null, now);
            switch (await store.WriteAsync(scope, new ConversationWrite(next, stored.Revision, [timelineEvent], change), cancellationToken).ConfigureAwait(false))
            {
                case ConversationWriteApplied:
                case ConversationWriteNotFound:
                    return;
                case ConversationWriteConflict when attempt < options.MaximumWriteAttempts:
                    continue;
                case ConversationWriteConflict:
                    throw new InvalidOperationException("Concurrent conversation writers kept winning while following a contact merge; retry.");
                case ConversationDuplicateMessage:
                case ConversationWriteRejected:
                    throw new InvalidOperationException("The conversation store rejected a contact re-pointing write.");
            }
        }
    }
}
