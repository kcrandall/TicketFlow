namespace TicketFlow.Conversations;

/// <summary>
/// Thrown by an <see cref="IConversationOutboxHandler"/> to ask for a retry no earlier than <see cref="RetryAfter"/> from now (for example
/// when a provider returned <c>Retry-After</c>). The dispatcher releases the entry at the later of its exponential backoff and this
/// delay (capped at one day); attempts and dead-lettering are unchanged.
/// </summary>
public class ConversationOutboxRetryException : Exception
{
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromDays(1);

    public ConversationOutboxRetryException(string message, TimeSpan? retryAfter = null) : base(message) =>
        RetryAfter = retryAfter is { } delay ? TimeSpan.FromTicks(Math.Clamp(delay.Ticks, 0, MaximumRetryAfter.Ticks)) : null;

    public TimeSpan? RetryAfter { get; }
}
