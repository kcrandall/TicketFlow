namespace TicketFlow.Channels.LiveChat;

public sealed class LiveChatOptions
{
    /// <summary>The channel kind served by <see cref="LiveChatChannel"/> (the inbox <c>ChannelBinding.Kind</c>; default <c>livechat</c>).</summary>
    public string Kind { get; set; } = "livechat";

    /// <summary>Session (and token) lifetime from issue or rotation (1 hour to 400 days, default 30 days).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Token age after which authentication recommends rotation (1 minute to the session lifetime, default 1 day).</summary>
    public TimeSpan TokenRotationInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long the previous token stays valid after rotation, for concurrent tabs (0 to 10 minutes, default 2 minutes).</summary>
    public TimeSpan TokenRotationGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Accept connections without an Origin (non-browser clients). Default <see langword="false"/>.</summary>
    public bool AllowMissingOrigin { get; set; }

    /// <summary>
    /// When an <c>IContactLinkingService</c> is registered, link the anonymous visitor contact to the verified account after
    /// <c>IdentifyAsync</c> (merging it into an existing account contact). Default <see langword="true"/>.
    /// </summary>
    public bool LinkVisitorContactOnIdentify { get; set; } = true;

    /// <summary>Messages returned per read (1-200, default 100).</summary>
    public int MaximumMessagesPerRead { get; set; } = 100;

    internal void Validate()
    {
        _ = new TicketFlow.Workspaces.ChannelKind(Kind);
        if (SessionLifetime < TimeSpan.FromHours(1) || SessionLifetime > TimeSpan.FromDays(400)
            || TokenRotationInterval < TimeSpan.FromMinutes(1) || TokenRotationInterval > SessionLifetime
            || TokenRotationGrace < TimeSpan.Zero || TokenRotationGrace > TimeSpan.FromMinutes(10)
            || MaximumMessagesPerRead is < 1 or > 200)
            throw new InvalidOperationException("Live chat options are invalid.");
    }
}
