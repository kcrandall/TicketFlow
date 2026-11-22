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
