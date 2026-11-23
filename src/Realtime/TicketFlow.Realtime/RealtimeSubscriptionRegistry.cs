using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Realtime;

/// <summary>
/// The node-local <see cref="IRealtimeSubscriptions"/>. Subscriptions are authorized when made (point checks against current data).
/// Publications from the <see cref="IRealtimeBus"/> are queued (bounded, oldest dropped) and fanned out by one background worker, so
/// publishers never wait for delivery. The worker reads each conversation once per change, groups subscribers by principal, decides
/// in memory with a per-principal <see cref="IAccessDecisionContext"/> reloaded after <see cref="RealtimeOptions.DecisionContextLifetime"/>,
/// and hands filtered events to a bounded per-connection queue (oldest dropped) drained by a per-connection sender, so a slow or
/// failing client cannot delay others. Queue subscriptions re-check <c>inbox:read</c> whenever their filter is re-planned and are
/// removed when it is revoked. A delivered <see cref="VisitorSessionEndedEvent"/> disconnects (and, when supported, closes) each
/// connection of that session. Payloads are never logged.
/// </summary>
public sealed class RealtimeSubscriptionRegistry : IRealtimeSubscriptions, IDisposable
{
    private const int MaximumCachedConversations = 4096;

    private readonly ConcurrentDictionary<string, ConnectionEntry> connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<object, TopicBucket> topics = new();
    private readonly IServiceScopeFactory scopes;
    private readonly RealtimeOptions options;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly IDisposable busSubscription;
    private readonly Lock gate = new();
    private readonly Channel<RealtimePublication> publications;
    private readonly Task worker;

    // Owned by the worker only.
    private readonly Dictionary<object, PrincipalAuthorization> principals = [];
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), (StoredConversation? Value, long Revision, long Sequence)> conversationCache = [];
    private long cacheSequence;
    private DateTimeOffset lastPrunedAt;

    private long pending;
    private long dropped;

    public RealtimeSubscriptionRegistry(IRealtimeBus bus, IServiceScopeFactory scopes, IOptions<RealtimeOptions> options, TimeProvider time, ILogger<RealtimeSubscriptionRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        this.scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.logger = logger ?? NullLogger<RealtimeSubscriptionRegistry>.Instance;
        publications = Channel.CreateBounded<RealtimePublication>(
            new BoundedChannelOptions(this.options.MaximumPendingPublications) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Dropped());
        worker = Task.Run(RunWorkerAsync);
        busSubscription = bus.Subscribe(DeliverAsync);
    }

    /// <summary>Connections currently registered on this node.</summary>
    public int ConnectionCount => connections.Count;

    /// <summary>Publications and connection events dropped because a queue was full (diagnostics).</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    public RealtimeConnectResult Connect(IRealtimeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (string.IsNullOrEmpty(connection.Id) || connection.Id.Length > 256 || connection.Subscriber is null)
            return new RealtimeSubscriptionRefused(RealtimeRefusal.Invalid);
        if (connections.Count >= options.MaximumConnections)
            return new RealtimeSubscriptionRefused(RealtimeRefusal.LimitReached);
        var entry = new ConnectionEntry(connection, Channel.CreateBounded<(RealtimeTopic, RealtimeEvent)>(
            new BoundedChannelOptions(options.ConnectionQueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, _ => Dropped()));
        if (!connections.TryAdd(connection.Id, entry))
            return new RealtimeSubscriptionRefused(RealtimeRefusal.Invalid);
        entry.Sender = Task.Run(() => RunSenderAsync(entry));
        return new RealtimeConnected(connection.Id);
    }

    public async Task<RealtimeSubscribeResult> SubscribeAsync(string connectionId, RealtimeTopic topic, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (topic is null)
            return new RealtimeSubscriptionRefused(RealtimeRefusal.Invalid);
        if (!connections.TryGetValue(connectionId, out var connection))
            return new RealtimeSubscriptionRefused(RealtimeRefusal.NotConnected);
        lock (gate)
        {
            if (connection.Topics.ContainsKey(topic))
                return new RealtimeSubscribed(topic);
            if (connection.Topics.Count >= options.MaximumSubscriptionsPerConnection)
                return new RealtimeSubscriptionRefused(RealtimeRefusal.LimitReached);
        }

        SubscriptionGrant? grant;
        RealtimeRefusal refusal;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            (grant, refusal) = await RealtimeDeliveryPolicy.AuthorizeAsync(connection.Connection.Subscriber, topic, services.GetRequiredService<IConversationStore>(),
                services.GetRequiredService<IAccessEvaluator>(), services.GetRequiredService<IAccessQueryPlanner>(), time, cancellationToken).ConfigureAwait(false);
        }
        if (grant is null)
            return new RealtimeSubscriptionRefused(refusal);

        lock (gate)
        {
            if (connection.Closed)
                return new RealtimeSubscriptionRefused(RealtimeRefusal.NotConnected);
            if (connection.Topics.ContainsKey(topic))
                return new RealtimeSubscribed(topic);
            if (connection.Topics.Count >= options.MaximumSubscriptionsPerConnection)
                return new RealtimeSubscriptionRefused(RealtimeRefusal.LimitReached);
            var entry = new SubscriptionEntry(connection, topic, grant);
            connection.Topics[topic] = entry;
            var bucket = topics.GetOrAdd(topic, _ => new TopicBucket());
            bucket.Add(connectionId, entry);
        }
        return new RealtimeSubscribed(topic);
    }

    public bool Unsubscribe(string connectionId, RealtimeTopic topic)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (topic is null || !connections.TryGetValue(connectionId, out var connection))
            return false;
        lock (gate)
        {
            if (!connection.Topics.Remove(topic))
                return false;
            RemoveFromTopic(topic, connectionId);
        }
        return true;
    }

    public void Disconnect(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (!connections.TryRemove(connectionId, out var connection))
            return;
        lock (gate)
        {
            connection.Closed = true;
            foreach (var key in connection.Topics.Keys)
                RemoveFromTopic(key, connectionId);
            connection.Topics.Clear();
        }
        connection.Queue.Writer.TryComplete();
    }

    public IReadOnlyList<RealtimeTopic> Topics(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        if (!connections.TryGetValue(connectionId, out var connection))
            return [];
        lock (gate)
            return [.. connection.Topics.Values.Select(entry => entry.Topic)];
    }

    /// <summary>Queues one publication for this node's fan-out worker. Returns without waiting for delivery.</summary>
    public Task DeliverAsync(RealtimePublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!topics.ContainsKey(publication.Topic))
            return Task.CompletedTask;
        Interlocked.Increment(ref pending);
        if (!publications.Writer.TryWrite(publication))
            Interlocked.Decrement(ref pending);
        return Task.CompletedTask;
    }

    /// <summary>Completes when every queued publication has been fanned out and every queued connection event sent or dropped (tests, diagnostics).</summary>
    public async Task IdleAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Interlocked.Read(ref pending) > 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Realtime fan-out did not become idle.");
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        busSubscription.Dispose();
        publications.Writer.TryComplete();
        foreach (var connection in connections.Values)
            connection.Queue.Writer.TryComplete();
    }

    private void Dropped()
    {
        Interlocked.Decrement(ref pending);
        Interlocked.Increment(ref dropped);
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var publication in publications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await FanOutAsync(publication).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Fail closed: a publication that cannot be authorized is not delivered.
                logger.LogWarning("Realtime fan-out failed ({Error}); the event was not delivered.", error.GetType().Name);
            }
            finally
            {
                Interlocked.Decrement(ref pending);
            }
        }
    }

    private async Task FanOutAsync(RealtimePublication publication)
    {
        if (!topics.TryGetValue(publication.Topic, out var bucket))
            return;
        var snapshot = bucket.Snapshot;
        if (snapshot.Length == 0)
            return;
        var realtimeEvent = publication.Event;

        SubscriptionEntry? singleAgent = null;
        List<SubscriptionEntry>? multipleAgents = null;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var target = snapshot[i];
            if (target.Connection.Closed)
                continue;
            var subscriber = target.Connection.Connection.Subscriber;
            if (subscriber is VisitorSubscriber visitor)
            {
                if (RealtimeDeliveryPolicy.ForVisitor(visitor, realtimeEvent) is { } visible)
                    Enqueue(target.Connection, target.Topic, visible);
            }
            else if (subscriber is AgentSubscriber)
            {
                if (singleAgent is null && multipleAgents is null)
                    singleAgent = target;
                else
                {
                    if (multipleAgents is null)
                    {
                        multipleAgents = new List<SubscriptionEntry>(snapshot.Length) { singleAgent! };
                        singleAgent = null;
                    }
                    multipleAgents.Add(target);
                }
            }
        }
        if (singleAgent is null && multipleAgents is null)
            return;

        var (decided, plain) = RealtimeDeliveryPolicy.WithoutConversation(publication.Topic, realtimeEvent);
        if (decided)
        {
            if (plain is { } direct)
            {
                if (singleAgent is not null)
                    Enqueue(singleAgent.Connection, singleAgent.Topic, direct);
                else
                    for (var i = 0; i < multipleAgents!.Count; i++)
                        Enqueue(multipleAgents[i].Connection, multipleAgents[i].Topic, direct);
            }
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var evaluator = services.GetRequiredService<IAccessEvaluator>();
        var conversation = await ConversationAsync(services.GetRequiredService<IConversationStore>(), realtimeEvent).ConfigureAwait(false);
        if (conversation is null)
        {
            // A deleted conversation can no longer be authorized: only subscribers already granted the conversation topic learn that it was deleted.
            if (realtimeEvent is ConversationChangedEvent { Kind: ConversationChangeKind.Deleted } && publication.Topic is ConversationTopic)
            {
                if (singleAgent is not null)
                    Enqueue(singleAgent.Connection, singleAgent.Topic, realtimeEvent);
                else
                    for (var i = 0; i < multipleAgents!.Count; i++)
                        Enqueue(multipleAgents[i].Connection, multipleAgents[i].Topic, realtimeEvent);
            }
            return;
        }

        var resource = ConversationAccess.Resource(conversation);
        var hasNotes = RealtimeDeliveryPolicy.HasNotes(realtimeEvent);
        RealtimeEvent? strippedEvent = null;
        var strippedComputed = false;
        RealtimeEvent? GetStripped()
        {
            if (!strippedComputed)
            {
                strippedComputed = true;
                strippedEvent = RealtimeDeliveryPolicy.StripNotes((ConversationChangedEvent)realtimeEvent);
            }
            return strippedEvent;
        }

        if (singleAgent is not null)
        {
            var principal = ((AgentSubscriber)singleAgent.Connection.Connection.Subscriber).Principal;
            var authorization = Authorization(principal);
            await DeliverToAgentAsync(singleAgent, principal, authorization, conversation.Scope, resource, realtimeEvent, hasNotes, GetStripped, evaluator, services).ConfigureAwait(false);
        }
        else
        {
            IAccessQueryPlanner? planner = null;
            // Group multi-tab agents by principal so authorization & queue filter checks run once per principal.
            var byPrincipal = new Dictionary<object, List<SubscriptionEntry>>();
            for (var i = 0; i < multipleAgents!.Count; i++)
            {
                var target = multipleAgents[i];
                var key = ((AgentSubscriber)target.Connection.Connection.Subscriber).Principal;
                if (!byPrincipal.TryGetValue(key, out var list))
                    byPrincipal[key] = list = [];
                list.Add(target);
            }
            foreach (var group in byPrincipal.Values)
            {
                var principal = ((AgentSubscriber)group[0].Connection.Connection.Subscriber).Principal;
                var authorization = Authorization(principal);
                RealtimeEvent? shared = null;
                var sharedDecided = false;
                bool? noteReadable = null;
                for (var i = 0; i < group.Count; i++)
                {
                    var target = group[i];
                    RealtimeEvent? filtered;
                    if (target.Topic is InboxQueueTopic queue)
                    {
                        planner ??= services.GetRequiredService<IAccessQueryPlanner>();
                        if (!await QueueAllowsAsync(target, queue, principal, resource, evaluator, planner).ConfigureAwait(false))
                            continue;
                        if (!hasNotes)
                            filtered = realtimeEvent;
                        else
                        {
                            noteReadable ??= await authorization.PermittedAsync(ConversationPermissions.NoteRead, conversation.Scope, resource, evaluator, time, options.DecisionContextLifetime, CancellationToken.None).ConfigureAwait(false);
                            filtered = noteReadable.Value ? realtimeEvent : GetStripped();
                        }
                    }
                    else
                    {
                        if (!sharedDecided)
                        {
                            sharedDecided = true;
                            if (await authorization.PermittedAsync(ConversationPermissions.Read, conversation.Scope, resource, evaluator, time, options.DecisionContextLifetime, CancellationToken.None).ConfigureAwait(false))
                            {
                                if (!hasNotes)
                                    shared = realtimeEvent;
                                else
                                {
                                    noteReadable ??= await authorization.PermittedAsync(ConversationPermissions.NoteRead, conversation.Scope, resource, evaluator, time, options.DecisionContextLifetime, CancellationToken.None).ConfigureAwait(false);
                                    shared = noteReadable.Value ? realtimeEvent : GetStripped();
                                }
                            }
                        }
                        filtered = shared;
                    }
                    if (filtered is { } delivered)
                        Enqueue(target.Connection, target.Topic, delivered);
                }
            }
        }
        PruneAuthorizations();
    }

    private async Task DeliverToAgentAsync(SubscriptionEntry target, PrincipalRef principal, PrincipalAuthorization authorization, TicketScope scope, AccessResource resource,
        RealtimeEvent realtimeEvent, bool hasNotes, Func<RealtimeEvent?> getStripped, IAccessEvaluator evaluator, IServiceProvider services)
    {
        RealtimeEvent? filtered;
        if (target.Topic is InboxQueueTopic queue)
        {
            var planner = services.GetRequiredService<IAccessQueryPlanner>();
            if (!await QueueAllowsAsync(target, queue, principal, resource, evaluator, planner).ConfigureAwait(false))
                return;
            filtered = !hasNotes || await authorization.PermittedAsync(ConversationPermissions.NoteRead, scope, resource, evaluator, time, options.DecisionContextLifetime, CancellationToken.None).ConfigureAwait(false)
                ? realtimeEvent
                : getStripped();
        }
        else
        {
            if (!await authorization.PermittedAsync(ConversationPermissions.Read, scope, resource, evaluator, time, options.DecisionContextLifetime, CancellationToken.None).ConfigureAwait(false))
                return;
            filtered = !hasNotes || await authorization.PermittedAsync(ConversationPermissions.NoteRead, scope, resource, evaluator, time, options.DecisionContextLifetime, CancellationToken.None).ConfigureAwait(false)
                ? realtimeEvent
                : getStripped();
        }
        if (filtered is { } delivered)
            Enqueue(target.Connection, target.Topic, delivered);
    }

    private async Task<bool> QueueAllowsAsync(SubscriptionEntry target, InboxQueueTopic queue, PrincipalRef principal, AccessResource resource, IAccessEvaluator evaluator,
        IAccessQueryPlanner planner)
    {
        var grant = target.Grant;
        if (grant.Filter is null || time.GetUtcNow() - grant.PlannedAt >= options.QueueFilterLifetime)
        {
            if (!await RealtimeDeliveryPolicy.InboxReadableAsync(principal, queue, evaluator, CancellationToken.None).ConfigureAwait(false))
            {
                // inbox:read was revoked: the queue subscription ends.
                lock (gate)
                    if (target.Connection.Topics.Remove(target.Topic))
                        RemoveFromTopic(target.Topic, target.Connection.Connection.Id);
                return false;
            }
            grant.Filter = await planner.PlanAsync(new AccessQuery(principal, ConversationPermissions.Read, queue.Scope), CancellationToken.None).ConfigureAwait(false);
            grant.PlannedAt = time.GetUtcNow();
        }
        return AccessFilterEvaluator.Matches(grant.Filter, resource);
    }

    /// <summary>The conversation, read once per change: consecutive publications of the same change (or older ones) reuse the snapshot.</summary>
    private async Task<StoredConversation?> ConversationAsync(IConversationStore store, RealtimeEvent realtimeEvent)
    {
        var key = (realtimeEvent.Scope, realtimeEvent.TargetConversation!.Value);
        if (realtimeEvent is ConversationChangedEvent changed && conversationCache.TryGetValue(key, out var cached) && cached.Revision >= changed.Revision)
            return cached.Value;
        var conversation = await store.GetAsync(key.Scope, key.Value, CancellationToken.None).ConfigureAwait(false);
        if (conversationCache.Count >= MaximumCachedConversations)
            EvictQuarterConversations();
        var revision = conversation?.Revision ?? (realtimeEvent is ConversationChangedEvent deleted ? deleted.Revision : 0);
        conversationCache[key] = (conversation, revision, ++cacheSequence);
        return conversation;
    }

    private void EvictQuarterConversations()
    {
        var evictCount = Math.Max(1, conversationCache.Count / 4);
        var entries = new List<((TicketScope, ConversationId) Key, long Seq)>(conversationCache.Count);
        foreach (var pair in conversationCache)
            entries.Add((pair.Key, pair.Value.Sequence));
        entries.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        for (var i = 0; i < evictCount; i++)
            conversationCache.Remove(entries[i].Key);
    }

    private PrincipalAuthorization Authorization(PrincipalRef principal)
    {
        if (!principals.TryGetValue(principal, out var authorization))
            principals[principal] = authorization = new PrincipalAuthorization(principal);
        return authorization;
    }

    private void PruneAuthorizations()
    {
        if (principals.Count < 1024)
            return;
        var now = time.GetUtcNow();
        if (now - lastPrunedAt < options.DecisionContextLifetime)
            return;
        lastPrunedAt = now;
        var cutoff = now - options.DecisionContextLifetime;
        List<object>? stale = null;
        foreach (var pair in principals)
            if (pair.Value.LoadedAt < cutoff)
                (stale ??= []).Add(pair.Key);
        if (stale is not null)
            for (var i = 0; i < stale.Count; i++)
                principals.Remove(stale[i]);
    }

    private void Enqueue(ConnectionEntry connection, RealtimeTopic topic, RealtimeEvent realtimeEvent)
    {
        if (connection.Closed)
            return;
        Interlocked.Increment(ref pending);
        if (!connection.Queue.Writer.TryWrite((topic, realtimeEvent)))
            Interlocked.Decrement(ref pending);
    }

    private async Task RunSenderAsync(ConnectionEntry entry)
    {
        var timeout = new CancellationTokenSource();
        try
        {
            await foreach (var (topic, realtimeEvent) in entry.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (entry.Closed)
                        continue;
                    try
                    {
                        timeout.CancelAfter(options.SendTimeout);
                        await entry.Connection.SendAsync(topic, realtimeEvent, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        logger.LogDebug("A realtime connection send failed ({Error}).", error.GetType().Name);
                    }
                    finally
                    {
                        if (!timeout.TryReset())
                        {
                            timeout.Dispose();
                            timeout = new CancellationTokenSource();
                        }
                    }
                    if (realtimeEvent is VisitorSessionEndedEvent)
                        await CloseAsync(entry).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }
        }
        finally
        {
            timeout.Dispose();
        }
    }

    /// <summary>
    /// Disconnects (and closes when supported) a connection after it received its session's end. Every connection of the session is
    /// subscribed to the session topic, so each one receives the event and closes itself.
    /// </summary>
    private async Task CloseAsync(ConnectionEntry entry)
    {
        Disconnect(entry.Connection.Id);
        if (entry.Connection is not IRealtimeConnectionLifetime lifetime)
            return;
        try
        {
            await lifetime.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            logger.LogDebug("Closing a realtime connection failed ({Error}).", error.GetType().Name);
        }
    }

    private void RemoveFromTopic(object key, string connectionId)
    {
        if (!topics.TryGetValue(key, out var bucket))
            return;
        if (bucket.Remove(connectionId))
            topics.TryRemove(key, out _);
    }

    private sealed class TopicBucket
    {
        private readonly Dictionary<string, SubscriptionEntry> entries = new(StringComparer.Ordinal);
        public volatile SubscriptionEntry[] Snapshot = [];

        public void Add(string connectionId, SubscriptionEntry entry)
        {
            entries[connectionId] = entry;
            Snapshot = [.. entries.Values];
        }

        public bool Remove(string connectionId)
        {
            entries.Remove(connectionId);
            Snapshot = entries.Count == 0 ? [] : [.. entries.Values];
            return entries.Count == 0;
        }
    }

    private sealed class ConnectionEntry(IRealtimeConnection connection, Channel<(RealtimeTopic, RealtimeEvent)> queue)
    {
        public IRealtimeConnection Connection { get; } = connection;
        public Channel<(RealtimeTopic, RealtimeEvent)> Queue { get; } = queue;
        public Task? Sender { get; set; }

        // Topics is mutated only under the registry gate.
        public Dictionary<object, SubscriptionEntry> Topics { get; } = [];
        public volatile bool Closed;
    }

    private sealed class SubscriptionEntry(ConnectionEntry connection, RealtimeTopic topic, SubscriptionGrant grant)
    {
        public ConnectionEntry Connection { get; } = connection;
        public RealtimeTopic Topic { get; } = topic;
        public SubscriptionGrant Grant { get; } = grant;
    }
}
