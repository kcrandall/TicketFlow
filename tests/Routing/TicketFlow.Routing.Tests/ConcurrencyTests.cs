namespace TicketFlow.Routing.Tests;

[TestClass]
public sealed class ConcurrencyTests
{
    [TestMethod]
    public async Task RacingWorkersAssignExactlyOnceAndFairly()
    {
        using var fixture = await RoutingFixture.CreateAsync();
        await ConcurrencyScenarios.RacingWorkersAssignExactlyOnceAndFairlyAsync(fixture);
    }

    [TestMethod]
    public async Task RacingWorkersNeverExceedCapacity()
    {
        using var fixture = await RoutingFixture.CreateAsync();
        await ConcurrencyScenarios.RacingWorkersNeverExceedCapacityAsync(fixture);
    }
}
