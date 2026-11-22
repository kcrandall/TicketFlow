using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.Tests;

/// <summary>Racing-worker scenarios shared by the in-memory and PostgreSQL routing store suites.</summary>
internal static class ConcurrencyScenarios
{
    /// <summary>
    /// <paramref name="workers"/> engines (each in its own DI scope, as separate hosts would be) route the same <paramref name="conversations"/>
    /// concurrently in different orders. Every conversation is assigned exactly once and round robin spreads them evenly.
    /// </summary>
    public static async Task RacingWorkersAssignExactlyOnceAndFairlyAsync(RoutingFixture fixture, int workers = 8, int conversations = 48)
    {
        string[] agents = ["ann", "bob", "cat", "dee"];
        foreach (var agent in agents)
            await fixture.OnlineAsync(agent, capacity: 100);
        var ids = Enumerable.Range(0, conversations).Select(index => $"race-{index:D3}").ToArray();
        foreach (var id in ids)
            await fixture.StartAsync(id);

        await RaceAsync(fixture, workers, ids);

        var counts = new Dictionary<string, int>();
        foreach (var id in ids)
        {
            var assignments = await fixture.AssignmentsAsync(id);
            Assert.AreEqual(1, assignments.Count, $"{id} must be assigned exactly once");
            var agent = assignments[0].Current!.Value.Value;
            counts[agent] = counts.GetValueOrDefault(agent) + 1;
        }
        Assert.AreEqual(agents.Length, counts.Count);
        Assert.IsTrue(counts.Values.Max() - counts.Values.Min() <= 1, "round robin is fair: " + string.Join(", ", counts.Select(pair => $"{pair.Key}={pair.Value}")));
        foreach (var agent in agents)
            Assert.AreEqual(counts[agent], (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId(agent)))[new ChannelKind("email")]);
    }

    /// <summary>Racing workers (engines, outbox dispatchers and maintenance) never push an agent past capacity; the rest stay queued.</summary>
    public static async Task RacingWorkersNeverExceedCapacityAsync(RoutingFixture fixture, int workers = 8, int conversations = 40)
    {
        await fixture.SetPolicyAsync(new RoutingPolicy(new RoutingPolicyId("main"), "Least busy", RoutingStrategy.LeastBusy));
        var capacities = new Dictionary<string, int> { ["ann"] = 3, ["bob"] = 5, ["cat"] = 2, ["dee"] = 4 };
        foreach (var (agent, capacity) in capacities)
            await fixture.OnlineAsync(agent, capacity: capacity);
        var ids = Enumerable.Range(0, conversations).Select(index => $"cap-{index:D3}").ToArray();
        foreach (var id in ids)
            await fixture.StartAsync(id);

        await Task.WhenAll(
            RaceAsync(fixture, workers, ids),
            Task.Run(async () =>
            {
                for (var round = 0; round < 5; round++)
                {
                    using var scope = fixture.Provider.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ConversationOutboxDispatcher>().RunOnceAsync();
                    await scope.ServiceProvider.GetRequiredService<RoutingMaintenanceWorker>().RunOnceAsync();
                }
            }));

        var assigned = 0;
        var perAgent = new Dictionary<string, int>();
        foreach (var id in ids)
        {
            var assignments = await fixture.AssignmentsAsync(id);
            Assert.IsTrue(assignments.Count <= 1, $"{id} must not be assigned twice");
            if (assignments.Count == 1)
            {
                assigned++;
                var agent = assignments[0].Current!.Value.Value;
                perAgent[agent] = perAgent.GetValueOrDefault(agent) + 1;
            }
        }
        Assert.AreEqual(capacities.Values.Sum(), assigned);
        foreach (var (agent, capacity) in capacities)
        {
            Assert.AreEqual(capacity, perAgent.GetValueOrDefault(agent), agent);
            Assert.AreEqual(capacity, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId(agent)))[new ChannelKind("email")]);
        }
        Assert.AreEqual(conversations - assigned, RoutingFixture.Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery(limit: 100))).Items.Count);
    }

    /// <summary>
    /// The queue snapshot still says "no team" while the conversation was moved to <c>billing</c> without the outbox being drained: the
    /// maintenance claim routes from the current conversation and only billing members are candidates.
    /// </summary>
    public static async Task StaleQueueSnapshotAsync(RoutingFixture fixture)
    {
        await fixture.StartAsync("stale");
        await fixture.DrainAsync();
        Assert.IsNull((await fixture.ConversationStore.GetAsync(fixture.Scope, new ConversationId("stale")))!.Conversation.Team);
        RoutingFixture.Applied(await fixture.Conversations.SetTeamAsync(fixture.Owner, new ConversationId("stale"), new TeamId("billing")));
        await fixture.OnlineAsync("cat");
        await fixture.OnlineAsync("dee");
        await fixture.OnlineAsync("bob");
        using (var scope = fixture.Provider.CreateScope())
            Assert.AreEqual(1, (await scope.ServiceProvider.GetRequiredService<RoutingMaintenanceWorker>().RunOnceAsync()).Routed);
        Assert.AreEqual(new UserId("bob"), await fixture.AssigneeAsync("stale"));
        Assert.AreEqual(1, (await fixture.AssignmentsAsync("stale")).Count);
        await fixture.DrainAsync();
        Assert.AreEqual(0, RoutingFixture.Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Count);
    }

    /// <summary>
    /// Right after the engine reserves a non-billing agent, someone moves the conversation to <c>billing</c>. The fenced
    /// <c>AssignAsync</c> conflicts, the reservation is released (load returns to zero) and the retry assigns a billing member.
    /// </summary>
    public static async Task TeamChangeBetweenReserveAndAssignAsync(RoutingFixture fixture, ReservationInterceptor interceptor)
    {
        await fixture.OnlineAsync("cat");
        await fixture.OnlineAsync("ann");
        await fixture.StartAsync("moving");
        var moved = 0;
        interceptor.AfterReserve = async reserved =>
        {
            if (Interlocked.Exchange(ref moved, 1) == 0)
                RoutingFixture.Applied(await fixture.Conversations.SetTeamAsync(fixture.Owner, reserved.Conversation, new TeamId("tier2")));
        };
        await fixture.OnlineAsync("dee");
        using (var scope = fixture.Provider.CreateScope())
            Assert.AreEqual("assigned:dee", RoutingFixture.Describe(await scope.ServiceProvider.GetRequiredService<IRoutingEngine>().RouteAsync(fixture.Scope, new ConversationId("moving"))));
        Assert.AreEqual(1, moved);
        Assert.AreEqual(1, (await fixture.AssignmentsAsync("moving")).Count);
        foreach (var agent in new[] { "ann", "cat" })
            Assert.AreEqual(0, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId(agent))).GetValueOrDefault(new ChannelKind("email")), agent);
        Assert.AreEqual(1, (await fixture.Assignments.GetLoadAsync(fixture.Scope, new UserId("dee")))[new ChannelKind("email")]);
    }

    /// <summary>Wraps an assignment store to run a callback after each successful reservation (before the engine assigns).</summary>
    public sealed class ReservationInterceptor
    {
        public Func<RoutingAssignment, Task>? AfterReserve { get; set; }

        public IRoutingAssignmentStore Wrap(IRoutingAssignmentStore inner) => new Store(inner, this);

        private sealed class Store(IRoutingAssignmentStore inner, ReservationInterceptor owner) : IRoutingAssignmentStore
        {
            public async Task<RoutingReservationResult> ReserveAsync(TicketScope scope, RoutingReservationRequest request, CancellationToken cancellationToken = default)
            {
                var result = await inner.ReserveAsync(scope, request, cancellationToken);
                if (result.Value is RoutingReserved reserved && owner.AfterReserve is { } callback)
                    await callback(reserved.Assignment);
                return result;
            }

            public Task<bool> ConfirmAsync(TicketScope scope, ConversationId conversation, UserId agent, long conversationRevision, CancellationToken cancellationToken = default) =>
                inner.ConfirmAsync(scope, conversation, agent, conversationRevision, cancellationToken);

            public Task<bool> ReleaseAsync(TicketScope scope, ConversationId conversation, UserId agent, CancellationToken cancellationToken = default) =>
                inner.ReleaseAsync(scope, conversation, agent, cancellationToken);

            public Task RecordAssignedAsync(TicketScope scope, RoutingAssignmentRecord record, CancellationToken cancellationToken = default) =>
                inner.RecordAssignedAsync(scope, record, cancellationToken);

            public Task<bool> ClearAsync(TicketScope scope, ConversationId conversation, long conversationRevision, CancellationToken cancellationToken = default) =>
                inner.ClearAsync(scope, conversation, conversationRevision, cancellationToken);

            public Task<RoutingAssignment?> GetAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default) =>
                inner.GetAsync(scope, conversation, cancellationToken);

            public Task<IReadOnlyList<RoutingAssignment>> ListForAgentAsync(TicketScope scope, UserId agent, ConversationId? after, int limit, CancellationToken cancellationToken = default) =>
                inner.ListForAgentAsync(scope, agent, after, limit, cancellationToken);

            public Task<IReadOnlyDictionary<ChannelKind, int>> GetLoadAsync(TicketScope scope, UserId agent, CancellationToken cancellationToken = default) =>
                inner.GetLoadAsync(scope, agent, cancellationToken);

            public Task<StickyOwner?> GetStickyOwnerAsync(TicketScope scope, ContactId contact, CancellationToken cancellationToken = default) =>
                inner.GetStickyOwnerAsync(scope, contact, cancellationToken);

            public Task<string?> GetCursorAsync(TicketScope scope, string cursorKey, CancellationToken cancellationToken = default) =>
                inner.GetCursorAsync(scope, cursorKey, cancellationToken);
        }
    }

    private static Task RaceAsync(RoutingFixture fixture, int workers, string[] ids) =>
        Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            using var scope = fixture.Provider.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IRoutingEngine>();
            var order = ids.OrderBy(id => HashCode.Combine(id, worker)).ToArray();
            foreach (var id in order)
                _ = await engine.RouteAsync(fixture.Scope, new ConversationId(id));
        })));
}
