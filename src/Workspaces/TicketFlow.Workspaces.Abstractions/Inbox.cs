using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// The channel an inbox is bound to: an opaque <see cref="ChannelKind"/> and the identifier of the channel configuration that the
/// channel package (E6) owns. Credentials and transport settings never live in the inbox.
/// </summary>
public sealed record ChannelBinding
{
    public ChannelBinding(ChannelKind kind, string configurationId)
    {
        if (!kind.IsInitialized)
            throw new ArgumentException("A channel kind is required.", nameof(kind));
        Kind = kind;
        ConfigurationId = TicketScope.RequireIdentifier(configurationId, nameof(configurationId));
    }

    public ChannelKind Kind { get; }

    /// <summary>Opaque channel configuration reference (at most 256 UTF-16 code units).</summary>
    public string ConfigurationId { get; }
}

/// <summary>
/// An inbox definition: a channel binding plus optional business calendar, SLA policy and routing references, an enabled flag and
/// bounded greeting/away messages. SLA and routing references are opaque until their packages (E9, E8) define them.
/// </summary>
public sealed record Inbox
{
    public const int MaximumNameLength = 128;
    public const int MaximumMessageLength = 1024;

    public Inbox(
        InboxId id,
        string name,
        ChannelBinding channel,
        BusinessCalendarId? calendar = null,
        SlaPolicyId? slaPolicy = null,
        RoutingPolicyId? routingPolicy = null,
        bool enabled = true,
        string? greetingMessage = null,
        string? awayMessage = null)
    {
        if (id == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(channel);
        if (calendar is { IsInitialized: false } || slaPolicy is { IsInitialized: false } || routingPolicy is { IsInitialized: false })
            throw new ArgumentException("Calendar, SLA policy and routing references must be initialized when supplied.", calendar is { IsInitialized: false } ? nameof(calendar) : slaPolicy is { IsInitialized: false } ? nameof(slaPolicy) : nameof(routingPolicy));
        Id = id;
        Name = WorkspaceText.RequireText(name, MaximumNameLength, nameof(name));
        Channel = channel;
        Calendar = calendar;
        SlaPolicy = slaPolicy;
        RoutingPolicy = routingPolicy;
        Enabled = enabled;
        GreetingMessage = greetingMessage is null ? null : WorkspaceText.RequireMessage(greetingMessage, MaximumMessageLength, nameof(greetingMessage));
        AwayMessage = awayMessage is null ? null : WorkspaceText.RequireMessage(awayMessage, MaximumMessageLength, nameof(awayMessage));
    }

    public InboxId Id { get; }
    public string Name { get; }
    public ChannelBinding Channel { get; }

    /// <summary>Business hours for the inbox; <see langword="null"/> means no calendar (always open).</summary>
    public BusinessCalendarId? Calendar { get; }

    public SlaPolicyId? SlaPolicy { get; }
    public RoutingPolicyId? RoutingPolicy { get; }
    public bool Enabled { get; }

    /// <summary>Message shown to contacts when a conversation starts. Customer-facing content; never logged.</summary>
    public string? GreetingMessage { get; }

    /// <summary>Message shown outside business hours. Customer-facing content; never logged.</summary>
    public string? AwayMessage { get; }

    public override string ToString() => "Inbox { <redacted> }";
}

/// <summary>An inbox with its scope and revision (first revision is 1).</summary>
public sealed record StoredInbox(TicketScope Scope, Inbox Inbox, long Revision);
