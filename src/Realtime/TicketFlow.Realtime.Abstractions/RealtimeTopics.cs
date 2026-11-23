using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>A realtime channel. Switch over every case without a discard arm.</summary>
public closed record RealtimeTopic
{
    private protected RealtimeTopic(TicketScope scope) => Scope = scope ?? throw new ArgumentNullException(nameof(scope));

    public TicketScope Scope { get; }
}

/// <summary>Every change, presence, lock, typing and read event of one conversation (agents with <c>conversation:read</c>).</summary>
public sealed record ConversationTopic : RealtimeTopic
{
    public ConversationTopic(TicketScope scope, ConversationId conversation)
        : base(scope) =>
        Conversation = conversation == default ? throw new ArgumentException("A conversation identifier is required.", nameof(conversation)) : conversation;

    public ConversationId Conversation { get; }

    public override string ToString() => $"ConversationTopic {{ Conversation = {Conversation} }}";
}

/// <summary>
/// Changes of conversations in one inbox (the inbox's queue view). Subscribing needs <c>inbox:read</c>; each delivery additionally
/// needs the conversation to match the subscriber's planned <c>conversation:read</c> filter.
/// </summary>
public sealed record InboxQueueTopic : RealtimeTopic
{
    public InboxQueueTopic(TicketScope scope, InboxId inbox)
        : base(scope) =>
        Inbox = inbox == default ? throw new ArgumentException("An inbox identifier is required.", nameof(inbox)) : inbox;

    public InboxId Inbox { get; }

    public override string ToString() => $"InboxQueueTopic {{ Inbox = {Inbox} }}";
}

/// <summary>One agent's own status and the conversations assigned to them (self-subscription only).</summary>
public sealed record AgentTopic : RealtimeTopic
{
    public AgentTopic(TicketScope scope, UserId agent)
        : base(scope) =>
        Agent = agent == default ? throw new ArgumentException("An agent identifier is required.", nameof(agent)) : agent;

    public UserId Agent { get; }

    public override string ToString() => $"AgentTopic {{ Agent = {Agent} }}";
}

/// <summary>Visitor-facing events of one live-chat session (only that session's authenticated visitor may subscribe).</summary>
public sealed record VisitorSessionTopic : RealtimeTopic
{
    public VisitorSessionTopic(TicketScope scope, VisitorSessionId session)
        : base(scope) =>
        Session = session.IsInitialized ? session : throw new ArgumentException("A session identifier is required.", nameof(session));

    public VisitorSessionId Session { get; }

    public override string ToString() => "VisitorSessionTopic { <redacted> }";
}

/// <summary>Who holds a connection. Switch over every case without a discard arm.</summary>
public closed record RealtimeSubscriber
{
    private protected RealtimeSubscriber()
    {
    }

    public abstract TicketScope Scope { get; }
}

/// <summary>An application-authenticated principal (agent, supervisor or API client) holding a realtime connection.</summary>
public sealed record AgentSubscriber : RealtimeSubscriber
{
    public AgentSubscriber(PrincipalRef principal) =>
        Principal = principal ?? throw new ArgumentNullException(nameof(principal));

    public PrincipalRef Principal { get; }

    public override TicketScope Scope => Principal.Scope;

    public override string ToString() => "AgentSubscriber { <redacted> }";
}

/// <summary>
/// A visitor whose session token was verified by the live-chat package. Visitors are not Access principals: they can subscribe only
/// to their own <see cref="VisitorSessionTopic"/> and receive only visitor-facing events.
/// </summary>
public sealed record VisitorSubscriber : RealtimeSubscriber
{
    public VisitorSubscriber(TicketScope scope, VisitorSessionId session)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Session = session.IsInitialized ? session : throw new ArgumentException("A session identifier is required.", nameof(session));
    }

    public override TicketScope Scope { get; }
    public VisitorSessionId Session { get; }

    public override string ToString() => "VisitorSubscriber { <redacted> }";
}
