using TicketFlow.Core;

namespace TicketFlow.Routing;

/// <summary>What an agent is doing on a conversation. Never renumber.</summary>
public enum ConversationActivity
{
    Viewing = 1,
    Composing = 2,
}

/// <summary>An agent's live presence on a conversation; it lapses at <see cref="ExpiresAt"/> unless refreshed.</summary>
public sealed record ConversationPresence(UserId User, ConversationActivity Activity, DateTimeOffset Since, DateTimeOffset ExpiresAt);

/// <summary>
/// A soft reply lock: a lease telling other agents that <see cref="Holder"/> is replying. <see cref="Fence"/> increases with every
/// acquisition or renewal; release requires the current fence. Advisory only — replies are not blocked by the conversation service.
/// </summary>
public sealed record ReplyLock(ConversationId Conversation, UserId Holder, long Fence, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt);

/// <summary>The presence was recorded; <see cref="Changed"/> is false when only the expiry was extended.</summary>
public sealed record PresenceTouched(ConversationPresence Presence, bool Changed);

/// <summary>The conversation already has the maximum number of present agents.</summary>
public sealed record PresenceCapacityExceeded
{
    private PresenceCapacityExceeded()
    {
    }

    public static PresenceCapacityExceeded Instance { get; } = new();
}

/// <summary>Outcome of a presence write. Switch over every case without a discard arm.</summary>
public union PresenceWriteResult(PresenceTouched, PresenceCapacityExceeded);

/// <summary>The lock is held by the caller (new or renewed).</summary>
public sealed record ReplyLockAcquired(ReplyLock Lock, bool Renewed);

/// <summary>Another agent holds a live lock.</summary>
public sealed record ReplyLockHeld(ReplyLock Current);

/// <summary>Outcome of a lock acquisition. Switch over every case without a discard arm.</summary>
public union ReplyLockResult(ReplyLockAcquired, ReplyLockHeld);
