namespace TicketFlow.Events;

/// <summary>The default <see cref="IEventPublisher"/>: appends to <see cref="IEventLog"/> in chunks of <see cref="IEventLog.MaximumAppend"/>, stamped with the registered clock.</summary>
public sealed class EventPublisher(IEventLog log, TimeProvider time) : IEventPublisher
{
    private readonly IEventLog log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));

    public async Task<EventAppendResult> PublishAsync(Core.TicketScope scope, IReadOnlyList<IntegrationEventDraft> drafts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(drafts);
        if (drafts.Count == 0)
            return new EventAppendResult([], 0);
        if (drafts.Count <= IEventLog.MaximumAppend)
            return await log.AppendAsync(scope, drafts, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var appended = new List<StoredIntegrationEvent>(drafts.Count);
        var duplicates = 0;
        foreach (var chunk in drafts.Chunk(IEventLog.MaximumAppend))
        {
            var result = await log.AppendAsync(scope, chunk, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            appended.AddRange(result.Appended);
            duplicates += result.Duplicates;
        }
        return new EventAppendResult(appended, duplicates);
    }
}
