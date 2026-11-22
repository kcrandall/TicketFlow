using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Realtime;
using WalFlow.Abstractions.Models;
using WalFlow.Abstractions.Sinks;

namespace TicketFlow.Integrations.WalFlow;

/// <summary>
/// A WalFlow <see cref="ISink"/> that listens to PostgreSQL change-data-capture events on <c>tf_conversation_outbox</c>, decodes
/// inserts through the published <see cref="ConversationOutboxRowCodec"/> contract, deduplicates idempotently by <c>entry_id</c>
/// within a bounded window, and publishes identifier-only <see cref="ConversationChangedEvent"/>s onto <see cref="IRealtimePublisher"/>.
/// </summary>
public sealed class WalFlowRealtimeSink : ISink
{
    private readonly IRealtimePublisher publisher;
    private readonly IConversationStore? conversations;
    private readonly WalFlowRealtimeOptions options;
    private readonly ILogger<WalFlowRealtimeSink>? logger;
    private readonly Lock dedupeGate = new();
    private readonly HashSet<Guid> seen = [];
    private readonly Queue<Guid> order = [];

    public WalFlowRealtimeSink(IRealtimePublisher publisher, IOptions<WalFlowRealtimeOptions> options, IConversationStore? conversations = null,
        ILogger<WalFlowRealtimeSink>? logger = null)
    {
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.conversations = conversations;
        this.logger = logger;
    }

    public async Task SendAsync(DebeziumPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Operation is not ("c" or "r") || payload.After is null)
            return;
        if (payload.Source is null || !string.Equals(payload.Source.Table, options.TableName, StringComparison.OrdinalIgnoreCase))
            return;
        if (options.SchemaName is not null && !string.Equals(payload.Source.Schema, options.SchemaName, StringComparison.OrdinalIgnoreCase))
            return;

        if (!ConversationOutboxRowCodec.TryDecode(payload.After, out var entry, out var error) || entry is null)
        {
            logger?.LogWarning("Ignored malformed conversation outbox CDC row: {Error}", error);
            return;
        }

        if (!TryMarkSeen(entry.Change.Id))
            return;

        var change = entry.Change;
        var notification = ConversationChangedEvent.From(change);
        await publisher.PublishAsync(new RealtimePublication(new ConversationTopic(change.Scope, change.Conversation), notification), ct).ConfigureAwait(false);
        await publisher.PublishAsync(new RealtimePublication(new InboxQueueTopic(change.Scope, change.Inbox), notification), ct).ConfigureAwait(false);
        if (change.Kind != ConversationChangeKind.Deleted && conversations is not null
            && await conversations.GetAsync(change.Scope, change.Conversation, ct).ConfigureAwait(false) is { Conversation.Assignee: UserId assignee })
            await publisher.PublishAsync(new RealtimePublication(new AgentTopic(change.Scope, assignee), notification), ct).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;

    private bool TryMarkSeen(Guid entryId)
    {
        lock (dedupeGate)
        {
            if (!seen.Add(entryId))
                return false;
            order.Enqueue(entryId);
            while (order.Count > options.DedupeWindowCapacity)
                seen.Remove(order.Dequeue());
            return true;
        }
    }
}
