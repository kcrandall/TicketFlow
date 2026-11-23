using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Routing.Tests;
using TicketFlow.Testing;
using TicketFlow.Core;

namespace TicketFlow.Routing.Stores.Postgres.Tests;

/// <summary>The racing-worker scenarios with routing state in PostgreSQL (conversations stay in the in-memory development store).</summary>
[TestClass]
public sealed class PostgresRoutingEngineTests
{
    private static async Task<(PostgresTestDatabase Database, RoutingFixture Fixture)> CreateAsync(ConcurrencyScenarios.ReservationInterceptor? interceptor = null)
    {
        var database = await PostgresTestDatabase.CreateAsync("tf_routing_engine_", maximumPoolSize: 24);
        try
        {
            await new PostgresRoutingStore(database.First).InitializeAsync();
            var fixture = await RoutingFixture.CreateAsync(stores: builder =>
            {
                if (interceptor is not null)
                    builder.Services.AddSingleton<IRoutingAssignmentStore>(provider => interceptor.Wrap(provider.GetRequiredService<PostgresRoutingStore>()));
                builder.AddPostgresRoutingStores(database.First, useApplicationClock: true);
            });
            return (database, fixture);
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    [TestMethod]
    public async Task RacingWorkersAssignExactlyOnceAndFairly()
    {
        var (database, fixture) = await CreateAsync();
        await using (database)
        using (fixture)
            await ConcurrencyScenarios.RacingWorkersAssignExactlyOnceAndFairlyAsync(fixture);
    }

    [TestMethod]
    public async Task RacingWorkersNeverExceedCapacity()
    {
        var (database, fixture) = await CreateAsync();
        await using (database)
        using (fixture)
            await ConcurrencyScenarios.RacingWorkersNeverExceedCapacityAsync(fixture);
    }

    [TestMethod]
    public async Task StaleQueueSnapshotUsesTheCurrentTeam()
    {
        var (database, fixture) = await CreateAsync();
        await using (database)
        using (fixture)
            await ConcurrencyScenarios.StaleQueueSnapshotAsync(fixture);
    }

    [TestMethod]
    public async Task TeamChangeBetweenReserveAndAssignIsRetried()
    {
        var interceptor = new ConcurrencyScenarios.ReservationInterceptor();
        var (database, fixture) = await CreateAsync(interceptor);
        await using (database)
        using (fixture)
            await ConcurrencyScenarios.TeamChangeBetweenReserveAndAssignAsync(fixture, interceptor);
    }

    [TestMethod]
    public async Task OverflowAndOfflineReassignmentWithPostgresState()
    {
        var (database, fixture) = await CreateAsync();
        await using (database)
        using (fixture)
        {
            await fixture.SetPolicyAsync(new RoutingPolicy(new Workspaces.RoutingPolicyId("main"), "Main", RoutingStrategy.RoundRobin,
                overflow: [new OverflowRule(TimeSpan.FromMinutes(1), new OverflowToTeam(new Core.TeamId("tier2")))], onAgentOffline: OfflineReassignment.ReturnToQueue));
            await fixture.StartAsync("w1");
            await fixture.DrainAsync();
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.AreEqual(1, (await fixture.Maintenance.RunOnceAsync()).Overflowed);
            await fixture.DrainAsync();
            await fixture.OnlineAsync("dee");
            await fixture.OnlineAsync("ann");
            Assert.AreEqual(1, (await fixture.Maintenance.RunOnceAsync()).Routed);
            Assert.AreEqual(new Core.UserId("dee"), await fixture.AssigneeAsync("w1"));

            Assert.IsInstanceOfType<TicketValue<StoredAgentProfile>>((await fixture.Availability.SetStatusAsync(fixture.Lead, new Core.UserId("dee"), AgentStatus.Offline)).Value);
            Assert.AreEqual(1, (await fixture.Maintenance.RunOnceAsync()).Reassigned);
            // The conversation team is tier2 (dee only), so it waits in the queue.
            Assert.IsNull(await fixture.AssigneeAsync("w1"));
            Assert.AreEqual(1, RoutingFixture.Value(await fixture.Queue.ListAsync(fixture.Owner, new RoutingQueueQuery())).Items.Count);
        }
    }
}
