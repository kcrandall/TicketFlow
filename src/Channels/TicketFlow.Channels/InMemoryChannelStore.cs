using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>
/// Development-only, process-local implementation of the four channel state store contracts. Bounded
/// (<see cref="MaximumRecords"/> per record kind), serialized by one lock, not durable.
/// </summary>
public sealed class InMemoryChannelStore : IChannelEndpointStore, IChannelConversationStateStore, IChannelThreadStore, IChannelDispatchStore
{
    public const int MaximumRecords = 100_000;

    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, ChannelKind Kind, string Configuration), StoredChannelEndpoint> endpoints = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), StoredChannelConversationState> states = [];
    private readonly Dictionary<(TicketScope Scope, InboxId Inbox, string Reference), StoredChannelMessageReference> references = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation, MessageId Message), StoredChannelDispatch> dispatches = [];
    private long nextReference;

    public Task<StoredChannelEndpoint?> GetAsync(TicketScope scope, ChannelKind kind, string configurationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        lock (gate)
            return Task.FromResult(endpoints.GetValueOrDefault((scope, kind, configurationId)));
    }

    public Task<StoredChannelEndpoint?> FindByAddressAsync(AccessPartition partition, ChannelKind kind, string address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(address);
        lock (gate)
            return Task.FromResult(endpoints.Values.FirstOrDefault(stored => AccessPartition.From(stored.Scope) == partition && stored.Endpoint.Kind == kind
                && string.Equals(stored.Endpoint.Address, address, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<StoredChannelEndpoint>> ListByAddressPrefixAsync(AccessPartition partition, ChannelKind kind, string prefix, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IChannelEndpointStore.MaximumPrefixMatches);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredChannelEndpoint>>([.. endpoints.Values
                .Where(stored => stored.Endpoint.Kind == kind && stored.Endpoint.Address is { } address && address.StartsWith(prefix, StringComparison.Ordinal)
                    && AccessPartition.From(stored.Scope) == partition)
                .OrderBy(stored => stored.Endpoint.Address, StringComparer.Ordinal).Take(limit)]);
    }

    public Task<ChannelWriteResult<StoredChannelEndpoint>> CreateAsync(TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (gate)
        {
            var key = (scope, endpoint.Kind, endpoint.ConfigurationId);
            if (endpoints.TryGetValue(key, out var existing))
                return Result<StoredChannelEndpoint>(new ChannelWriteConflict(existing.Revision));
            if (AddressTaken(scope, endpoint, null))
                return Result<StoredChannelEndpoint>(new ChannelWriteRejected(ChannelWriteRejection.AddressTaken));
            if (endpoints.Keys.Count(item => item.Scope == scope) >= IChannelEndpointStore.MaximumEndpointsPerScope || endpoints.Count >= MaximumRecords)
                return Result<StoredChannelEndpoint>(new ChannelWriteRejected(ChannelWriteRejection.CapacityExceeded));
            var stored = new StoredChannelEndpoint(scope, endpoint, 1);
            endpoints[key] = stored;
            return Result<StoredChannelEndpoint>(new ChannelWriteApplied<StoredChannelEndpoint>(stored));
        }
    }

    public Task<ChannelWriteResult<StoredChannelEndpoint>> UpdateAsync(TicketScope scope, ChannelEndpoint endpoint, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (gate)
        {
            var key = (scope, endpoint.Kind, endpoint.ConfigurationId);
            if (!endpoints.TryGetValue(key, out var existing))
                return Result<StoredChannelEndpoint>(ChannelWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredChannelEndpoint>(new ChannelWriteConflict(existing.Revision));
            if (AddressTaken(scope, endpoint, key))
                return Result<StoredChannelEndpoint>(new ChannelWriteRejected(ChannelWriteRejection.AddressTaken));
            var stored = new StoredChannelEndpoint(scope, endpoint, existing.Revision + 1);
            endpoints[key] = stored;
            return Result<StoredChannelEndpoint>(new ChannelWriteApplied<StoredChannelEndpoint>(stored));
        }
    }

    public Task<ChannelWriteResult<StoredChannelEndpoint>> DeleteAsync(TicketScope scope, ChannelKind kind, string configurationId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(configurationId);
        lock (gate)
        {
            var key = (scope, kind, configurationId);
            if (!endpoints.TryGetValue(key, out var existing))
                return Result<StoredChannelEndpoint>(ChannelWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredChannelEndpoint>(new ChannelWriteConflict(existing.Revision));
            endpoints.Remove(key);
            return Result<StoredChannelEndpoint>(new ChannelWriteApplied<StoredChannelEndpoint>(existing));
        }
    }

    public Task<StoredChannelConversationState?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(states.GetValueOrDefault((scope, conversation)));
    }

    public Task<ChannelWriteResult<StoredChannelConversationState>> WriteAsync(TicketScope scope, ChannelConversationState state, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        lock (gate)
        {
            var key = (scope, state.Conversation);
            states.TryGetValue(key, out var existing);
            if (existing is null && expectedRevision is not null)
                return Result<StoredChannelConversationState>(ChannelWriteNotFound.Instance);
            if (existing is not null && (existing.Revision != expectedRevision || existing.State.Inbox != state.Inbox || existing.State.Channel != state.Channel))
                return Result<StoredChannelConversationState>(new ChannelWriteConflict(existing.Revision));
            if (existing is null && states.Count >= MaximumRecords)
                return Result<StoredChannelConversationState>(new ChannelWriteRejected(ChannelWriteRejection.CapacityExceeded));
            var stored = new StoredChannelConversationState(scope, state, (existing?.Revision ?? 0) + 1);
            states[key] = stored;
            return Result<StoredChannelConversationState>(new ChannelWriteApplied<StoredChannelConversationState>(stored));
        }
    }

    public Task<IReadOnlyList<ConversationId>> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelKind channel, string externalThreadId, ContactId contact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(externalThreadId);
        lock (gate)
            return Task.FromResult<IReadOnlyList<ConversationId>>([.. states.Values
                .Where(stored => stored.Scope == scope && stored.State.Inbox == inbox && stored.State.Channel == channel && stored.State.Contact == contact
                    && string.Equals(stored.State.ExternalThreadId, externalThreadId, StringComparison.Ordinal))
                .Select(stored => stored.State.Conversation).OrderByDescending(id => id.Value, StringComparer.Ordinal).Take(IChannelConversationStateStore.MaximumThreadMatches)]);
    }

    public Task<bool> DeleteAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(states.Remove((scope, conversation)));
    }

    public Task<int> AddAsync(TicketScope scope, InboxId inbox, ConversationId conversation, IReadOnlyList<ChannelMessageReference> references, DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > IChannelThreadStore.MaximumReferencesPerCall || references.Any(reference => reference is null))
            throw new ArgumentException($"At most {IChannelThreadStore.MaximumReferencesPerCall} references per call.", nameof(references));
        var at = ChannelConversationState.Microseconds(recordedAt);
        lock (gate)
        {
            var added = 0;
            foreach (var reference in references)
            {
                var key = (scope, inbox, reference.Reference);
                if (this.references.ContainsKey(key) || this.references.Count >= MaximumRecords)
                    continue;
                this.references[key] = new StoredChannelMessageReference(conversation, inbox, reference, ++nextReference, at);
                added++;
            }
            var owned = this.references.Where(pair => pair.Key.Scope == scope && pair.Value.Conversation == conversation)
                .OrderBy(pair => pair.Value.Sequence).ToList();
            foreach (var pair in owned.Take(Math.Max(0, owned.Count - IChannelThreadStore.MaximumReferencesPerConversation)))
                this.references.Remove(pair.Key);
            return Task.FromResult(added);
        }
    }

    public Task<ConversationId?> FindConversationAsync(TicketScope scope, InboxId inbox, IReadOnlyList<string> references, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(references);
        if (references.Count > IChannelThreadStore.MaximumReferencesPerCall)
            throw new ArgumentException($"At most {IChannelThreadStore.MaximumReferencesPerCall} references per call.", nameof(references));
        lock (gate)
        {
            foreach (var reference in references)
                if (reference is not null && this.references.TryGetValue((scope, inbox, reference), out var stored))
                    return Task.FromResult<ConversationId?>(stored.Conversation);
            return Task.FromResult<ConversationId?>(null);
        }
    }

    public Task<IReadOnlyList<StoredChannelMessageReference>> ListAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredChannelMessageReference>>([.. references.Where(pair => pair.Key.Scope == scope && pair.Value.Conversation == conversation)
                .Select(pair => pair.Value).OrderBy(reference => reference.Sequence)]);
    }

    public Task<StoredChannelDispatch?> GetAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(dispatches.GetValueOrDefault((scope, conversation, message)));
    }

    public Task<ChannelWriteResult<StoredChannelDispatch>> WriteAsync(TicketScope scope, ChannelDispatch dispatch, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (gate)
        {
            var key = (scope, dispatch.Conversation, dispatch.Message);
            dispatches.TryGetValue(key, out var existing);
            if (existing is null && expectedRevision is not null)
                return Result<StoredChannelDispatch>(ChannelWriteNotFound.Instance);
            if (existing is not null && (existing.Revision != expectedRevision || existing.Dispatch.Inbox != dispatch.Inbox || existing.Dispatch.Channel != dispatch.Channel))
                return Result<StoredChannelDispatch>(new ChannelWriteConflict(existing.Revision));
            if (existing is null && dispatches.Count >= MaximumRecords)
                return Result<StoredChannelDispatch>(new ChannelWriteRejected(ChannelWriteRejection.CapacityExceeded));
            var stored = new StoredChannelDispatch(scope, dispatch, (existing?.Revision ?? 0) + 1);
            dispatches[key] = stored;
            return Result<StoredChannelDispatch>(new ChannelWriteApplied<StoredChannelDispatch>(stored));
        }
    }

    private bool AddressTaken(TicketScope scope, ChannelEndpoint endpoint, (TicketScope, ChannelKind, string)? self) =>
        endpoint.Address is { } address && endpoints.Any(pair => (self is null || pair.Key != self.Value) && pair.Value.Endpoint.Kind == endpoint.Kind
            && AccessPartition.From(pair.Key.Scope) == AccessPartition.From(scope) && string.Equals(pair.Value.Endpoint.Address, address, StringComparison.Ordinal));

    private static Task<ChannelWriteResult<T>> Result<T>(ChannelWriteResult<T> result) => Task.FromResult(result);
}
