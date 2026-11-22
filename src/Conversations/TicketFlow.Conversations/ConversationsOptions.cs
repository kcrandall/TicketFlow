using TicketFlow.Core;

namespace TicketFlow.Conversations;

public sealed class ConversationsOptions
{
    /// <summary>Generates identifiers for new conversations. Default: a version 7 GUID in "N" format.</summary>
    public Func<ConversationId> ConversationIdFactory { get; set; } = () => new ConversationId(Guid.CreateVersion7().ToString("N"));

    /// <summary>Generates identifiers for new messages and notes. Default: a version 7 GUID in "N" format.</summary>
    public Func<MessageId> MessageIdFactory { get; set; } = () => new MessageId(Guid.CreateVersion7().ToString("N"));

    /// <summary>Attempts for commands without an expected revision when concurrent writers win (1-64, default 16).</summary>
    public int MaximumWriteAttempts { get; set; } = 16;

    internal void Validate()
    {
        if (ConversationIdFactory is null || MessageIdFactory is null || MaximumWriteAttempts is < 1 or > 64)
            throw new InvalidOperationException("Conversation options are invalid.");
    }
}
