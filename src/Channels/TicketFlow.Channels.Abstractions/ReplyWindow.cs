namespace TicketFlow.Channels;

/// <summary>Free-form replies are allowed; <see cref="Until"/> is when the window closes (<see langword="null"/> when the channel has no window).</summary>
public sealed record ReplyWindowOpen(DateTimeOffset? Until);

/// <summary>
/// Free-form replies are refused because the channel's reply window has elapsed since the contact's last message
/// (<see cref="ClosedAt"/>), or no incoming message exists (<see cref="ClosedAt"/> <see langword="null"/>). Only a new incoming
/// message or an approved template can reach the contact.
/// </summary>
public sealed record ReplyWindowClosed(DateTimeOffset? ClosedAt)
{
    public const string FailureCode = "reply-window-closed";
}

/// <summary>Reply window state. Switch over every case without a discard arm.</summary>
public union ReplyWindowState(ReplyWindowOpen, ReplyWindowClosed);

public static class ChannelReplyWindow
{
    /// <summary>
    /// Evaluates the reply window of <paramref name="capabilities"/> for a conversation whose last incoming message was received at
    /// <paramref name="lastIncomingAt"/>. The window is half-open: a reply exactly at the closing instant is refused.
    /// </summary>
    public static ReplyWindowState Evaluate(ChannelCapabilities capabilities, DateTimeOffset? lastIncomingAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.ReplyWindow is not { } window)
            return new ReplyWindowOpen(null);
        if (lastIncomingAt is not { } last)
            return new ReplyWindowClosed(null);
        var until = last + window;
        return now < until ? new ReplyWindowOpen(until) : new ReplyWindowClosed(until);
    }
}
