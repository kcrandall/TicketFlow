using TicketFlow.Contacts;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Testing;

/// <summary>A scripted <see cref="ITicketChannel"/>: returns queued results (default Accepted with a generated id) and records requests.</summary>
public sealed class FakeChannel(ChannelKind kind, ChannelCapabilities capabilities, params ContactIdentityKind[] recipientKinds) : ITicketChannel
{
    private readonly Queue<Func<OutboundChannelMessage, ChannelSendResult>> results = new();
    private int sent;

    public ChannelKind Kind { get; } = kind;
    public ChannelCapabilities Capabilities { get; } = capabilities;
    public IReadOnlyList<ContactIdentityKind> RecipientKinds { get; } = recipientKinds;
    public List<OutboundChannelMessage> Requests { get; } = [];

    public FakeChannel Then(ChannelSendResult result)
    {
        results.Enqueue(_ => result);
        return this;
    }

    public FakeChannel ThenThrow()
    {
        results.Enqueue(_ => throw new InvalidOperationException("transport down"));
        return this;
    }

    public Task<ChannelSendResult> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken = default)
    {
        Requests.Add(message);
        var next = results.Count > 0 ? results.Dequeue() : _ => new ChannelSendAccepted($"ext-{Kind}-{Interlocked.Increment(ref sent)}");
        return Task.FromResult(next(message));
    }
}
