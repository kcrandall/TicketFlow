using TicketFlow.Conversations;

namespace TicketFlow.Channels;

/// <summary>Channel features. Combine as flags; <see cref="ChannelCapabilities"/> validates the combination.</summary>
[Flags]
public enum ChannelFeatures
{
    None = 0,

    /// <summary>Outgoing messages may carry attachments (see <see cref="ChannelCapabilities.MaximumAttachments"/>).</summary>
    Attachments = 1,

    /// <summary>HTML or Markdown bodies are rendered by the channel.</summary>
    RichContent = 2,

    /// <summary>Free-form replies are allowed only within <see cref="ChannelCapabilities.ReplyWindow"/> after the last incoming message (for example WhatsApp's 24 hours).</summary>
    ReplyWindow = 4,

    /// <summary>Replies can be threaded under a provider thread (email References, Slack thread ts).</summary>
    Threading = 8,

    /// <summary>Typing indicators.</summary>
    Typing = 16,

    /// <summary>The provider reports delivery.</summary>
    DeliveryReceipts = 32,

    /// <summary>The provider reports reads.</summary>
    ReadReceipts = 64,

    /// <summary>Pre-approved templates can be sent outside the reply window.</summary>
    Templates = 128,
}

/// <summary>
/// What a channel supports. <see cref="ReplyWindow"/> is set exactly when <see cref="ChannelFeatures.ReplyWindow"/> is (1 minute to
/// 30 days); <see cref="MaximumAttachments"/> is positive exactly when <see cref="ChannelFeatures.Attachments"/> is.
/// </summary>
public sealed record ChannelCapabilities
{
    public static readonly TimeSpan MaximumReplyWindow = TimeSpan.FromDays(30);

    public ChannelCapabilities(ChannelFeatures features, TimeSpan? replyWindow = null, int maximumAttachments = 0, int maximumBodyLength = ConversationMessageLimits.MaximumBodyBytes)
    {
        const ChannelFeatures all = ChannelFeatures.Attachments | ChannelFeatures.RichContent | ChannelFeatures.ReplyWindow | ChannelFeatures.Threading
            | ChannelFeatures.Typing | ChannelFeatures.DeliveryReceipts | ChannelFeatures.ReadReceipts | ChannelFeatures.Templates;
        if ((features & ~all) != 0)
            throw new ArgumentOutOfRangeException(nameof(features));
        if (features.HasFlag(ChannelFeatures.ReplyWindow) != replyWindow.HasValue
            || replyWindow is { } window && (window < TimeSpan.FromMinutes(1) || window > MaximumReplyWindow))
            throw new ArgumentException("A reply window (1 minute to 30 days) is required exactly when the ReplyWindow feature is set.", nameof(replyWindow));
        if (features.HasFlag(ChannelFeatures.Attachments) != maximumAttachments > 0 || maximumAttachments > ConversationMessageLimits.MaximumAttachments)
            throw new ArgumentException($"Attachment channels allow 1-{ConversationMessageLimits.MaximumAttachments} attachments; others none.", nameof(maximumAttachments));
        if (maximumBodyLength is < 1 or > ConversationMessageLimits.MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBodyLength));
        Features = features;
        ReplyWindow = replyWindow;
        MaximumAttachments = maximumAttachments;
        MaximumBodyLength = maximumBodyLength;
    }

    public ChannelFeatures Features { get; }
    public TimeSpan? ReplyWindow { get; }
    public int MaximumAttachments { get; }

    /// <summary>Maximum body length in UTF-16 code units accepted by the channel.</summary>
    public int MaximumBodyLength { get; }

    public bool Has(ChannelFeatures feature) => (Features & feature) == feature;
}
