using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Keeps conversations pointing at the surviving contact. On <see cref="ContactChangeKind.Merged"/> every conversation of the source is
/// re-pointed to the target (a <see cref="ContactChanged"/> event and a <see cref="ContactRedirect"/> recording the merge); on
/// <see cref="ContactChangeKind.Unmerged"/> exactly the conversations re-pointed by that merge return to their previous contact.
/// Both are idempotent (keyed by merge identifier; merges whose record was removed by an unmerge are skipped), so replays and duplicate
/// notifications are no-ops. Walks repeat full passes until a pass changes nothing, so concurrent activity cannot hide a conversation.
/// <para>
/// The in-process path (<see cref="OnChangedAsync"/>, registered as an <see cref="IContactChangeObserver"/>) is best-effort: failures
/// other than cancellation are logged (identifiers only) and swallowed so the contact write's caller is not failed after commit.
/// <see cref="CatchUpAsync"/> over the durable contact change outbox is the reliable path and throws on failure.
/// </para>
/// Runs as a trusted internal component: the contact merge itself was authorized (<c>contact:merge</c>); conversation writes go to the
/// store directly with the system actor <c>contact-merge</c>.
/// </summary>
public sealed class ConversationContactMergeFollower : IContactChangeObserver
{
    /// <summary>The system actor recorded on re-pointing events.</summary>
    public static AccessActor Actor { get; } = new(AccessActorKind.System, "contact-merge");

    private readonly IContactStore contacts;
    private readonly ContactRepointer repointer;
    private readonly ILogger logger;

    public ConversationContactMergeFollower(IConversationStore store, IContactStore contacts, IOptions<ConversationsOptions> options, TimeProvider time,
        ILogger<ConversationContactMergeFollower>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        ArgumentNullException.ThrowIfNull(options);
        options.Value.Validate();
        ArgumentNullException.ThrowIfNull(time);
        repointer = new ContactRepointer(store, contacts, options.Value.MaximumWriteAttempts, time);
        this.logger = logger ?? NullLogger<ConversationContactMergeFollower>.Instance;
    }

    /// <summary>Best-effort in-process application of committed contact changes; see the type remarks.</summary>
    public async Task OnChangedAsync(IReadOnlyList<ContactChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            try
            {
                await ApplyAsync(change, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning("Following contact change {Sequence} ({Kind}) failed with {Error}; contact change catch-up will repair it.",
                    change.Sequence, change.Event.Kind, error.GetType().Name);
            }
        }
    }

    /// <summary>
    /// Applies merge and unmerge changes of <paramref name="scope"/> with a sequence greater than <paramref name="afterSequence"/>, in
    /// order, reading at most <paramref name="maximumChanges"/> changes. Returns the last sequence applied (the caller's durable checkpoint;
    /// replaying from an older checkpoint is safe). Failures throw, leaving the checkpoint at the last applied change.
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
            cancellationToken.ThrowIfCancellationRequested();
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
        switch (change.Event)
        {
            case { Kind: ContactChangeKind.Merged, Related: { } target, Merge: { } merge }:
                // A merge that has since been undone (its record is gone) is skipped, so replaying history never re-points again.
                if (await contacts.GetMergeAsync(change.Scope, merge, cancellationToken).ConfigureAwait(false) is not null)
                    await repointer.MergeAsync(change.Scope, change.Event.Contact, target, merge, cancellationToken).ConfigureAwait(false);
                break;
            case { Kind: ContactChangeKind.Unmerged, Related: { } target, Merge: { } merge }:
                await repointer.UnmergeAsync(change.Scope, target, merge, cancellationToken).ConfigureAwait(false);
                break;
        }
    }
}
