using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Shared contact re-pointing used by <see cref="ConversationContactMergeFollower"/> and by <see cref="ConversationService.StartAsync"/>
/// (which repairs a conversation started for a contact merged concurrently). Trusted internal component: writes go to the store
/// directly with <see cref="ConversationContactMergeFollower.Actor"/>.
/// </summary>
internal sealed class ContactRepointer(IConversationStore store, IContactStore contacts, int maximumWriteAttempts, TimeProvider time)
{
    /// <summary>Full passes over a contact's conversations; a pass that changes nothing ends the walk.</summary>
    public const int MaximumPasses = 16;

    /// <summary>Contact changes scanned when looking up the merge that tombstoned a contact.</summary>
    public const int MaximumMergeScan = 1_000_000;

    /// <summary>Merge-chain hops followed when repairing one conversation.</summary>
    public const int MaximumChain = Conversation.MaximumRedirects;

    /// <summary>Re-points every conversation of <paramref name="source"/> to <paramref name="target"/> for <paramref name="merge"/>.</summary>
    public Task MergeAsync(TicketScope scope, ContactId source, ContactId target, Guid merge, CancellationToken cancellationToken) =>
        WalkAsync(scope, source, conversation => Merged(conversation, target, merge), cancellationToken);

    /// <summary>Returns the conversations re-pointed by <paramref name="merge"/> from <paramref name="target"/> to their previous contact.</summary>
    public Task UnmergeAsync(TicketScope scope, ContactId target, Guid merge, CancellationToken cancellationToken) =>
        WalkAsync(scope, target, conversation => conversation.ContactRedirects.FirstOrDefault(redirect => redirect.Merge == merge) is not { } redirect
            ? null
            : (conversation.With(contact: redirect.Previous, contactRedirects: conversation.ContactRedirects.Where(item => item.Merge != merge)),
                new ContactChanged(conversation.Contact, redirect.Previous, merge)), cancellationToken);

    /// <summary>
    /// Follows the merge chain of one conversation's contact: while the contact is a merge tombstone, finds the (still recorded) merge in
    /// the contact change log and re-points the conversation to its target. Returns the final stored conversation.
    /// </summary>
    public async Task<StoredConversation?> RepairAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop < MaximumChain; hop++)
        {
            var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            if (stored is null)
                return null;
            var contact = await contacts.GetAsync(scope, stored.Conversation.Contact, cancellationToken).ConfigureAwait(false);
            if (contact?.Contact.MergedInto is not { } target)
                return stored;
            if (await FindMergeAsync(scope, stored.Conversation.Contact, target, cancellationToken).ConfigureAwait(false) is not { } merge)
                return stored;
            await RepointOneAsync(stored, stored.Conversation.Contact, conversation => Merged(conversation, target, merge), cancellationToken).ConfigureAwait(false);
        }
        return await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
    }

    private static (Conversation Next, ContactChanged Event)? Merged(Conversation conversation, ContactId target, Guid merge) =>
        conversation.ContactRedirects.Any(redirect => redirect.Merge == merge)
            ? null
            : (conversation.With(contact: target, contactRedirects: [.. conversation.ContactRedirects, new ContactRedirect(merge, conversation.Contact)]),
                new ContactChanged(conversation.Contact, target, merge));

    /// <summary>The newest merge of <paramref name="source"/> into <paramref name="target"/> whose record still exists.</summary>
    private async Task<Guid?> FindMergeAsync(TicketScope scope, ContactId source, ContactId target, CancellationToken cancellationToken)
    {
        Guid? found = null;
        long position = 0;
        for (var read = 0; read < MaximumMergeScan;)
        {
            var batch = await contacts.ReadChangesAsync(scope, position, IContactStore.MaximumChangesPerRead, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
                break;
            foreach (var change in batch)
                if (change.Event is { Kind: ContactChangeKind.Merged, Merge: { } merge } && change.Event.Contact == source && change.Event.Related == target)
                    found = merge;
            position = batch[^1].Sequence;
            read += batch.Count;
        }
        return found is { } candidate && await contacts.GetMergeAsync(scope, candidate, cancellationToken).ConfigureAwait(false) is not null ? candidate : null;
    }

    /// <summary>
    /// Applies <paramref name="repoint"/> to every conversation of <paramref name="contact"/>. Concurrent activity reorders the
    /// activity-ordered list, so the walk repeats full passes until one changes nothing (at most <see cref="MaximumPasses"/>).
    /// </summary>
    private async Task WalkAsync(TicketScope scope, ContactId contact, Func<Conversation, (Conversation Next, ContactChanged Event)?> repoint, CancellationToken cancellationToken)
    {
        for (var pass = 0; pass < MaximumPasses; pass++)
        {
            var changed = 0;
            var query = new ConversationListQuery(contact: contact, limit: ConversationListQuery.MaximumLimit);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await store.ListAsync(scope, query, AllowAllFilter.Instance, cancellationToken).ConfigureAwait(false);
                foreach (var stored in page.Conversations)
                    if (await RepointOneAsync(stored, contact, repoint, cancellationToken).ConfigureAwait(false))
                        changed++;
                if (page.Next is null)
                    break;
                query = query.WithAfter(page.Next);
            }
            if (changed == 0)
                return;
        }
        throw new InvalidOperationException("Contact re-pointing did not converge; retry.");
    }

    /// <summary>Re-points one conversation, starting from the listed snapshot and re-reading only after a conflict. True when it wrote.</summary>
    private async Task<bool> RepointOneAsync(StoredConversation listed, ContactId expectedContact, Func<Conversation, (Conversation Next, ContactChanged Event)?> repoint,
        CancellationToken cancellationToken)
    {
        var stored = listed;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stored.Conversation.Contact != expectedContact || repoint(stored.Conversation) is not { } plan)
                return false;
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
            var timelineEvent = new TimelineEvent(stored.Id, sequence, Guid.CreateVersion7(), now, ConversationContactMergeFollower.Actor, plan.Event);
            var change = new ConversationChange(Guid.CreateVersion7(), stored.Scope, stored.Id, next.Inbox, stored.Revision + 1, ConversationChangeKind.EventsAppended,
                [ConversationEventSummary.From(timelineEvent)], null, null, now);
            switch (await store.WriteAsync(stored.Scope, new ConversationWrite(next, stored.Revision, [timelineEvent], change), cancellationToken).ConfigureAwait(false))
            {
                case ConversationWriteApplied:
                    return true;
                case ConversationWriteNotFound:
                    return false;
                case ConversationWriteConflict when attempt < maximumWriteAttempts:
                    if (await store.GetAsync(stored.Scope, stored.Id, cancellationToken).ConfigureAwait(false) is not { } fresh)
                        return false;
                    stored = fresh;
                    continue;
                case ConversationWriteConflict:
                    throw new InvalidOperationException("Concurrent conversation writers kept winning while re-pointing a contact; retry.");
                case ConversationDuplicateMessage:
                case ConversationWriteRejected:
                    throw new InvalidOperationException("The conversation store rejected a contact re-pointing write.");
            }
        }
    }
}
