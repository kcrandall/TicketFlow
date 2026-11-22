using TicketFlow.Access;
using TicketFlow.Reporting.StoreContracts;

namespace TicketFlow.Reporting.Tests;

[TestClass]
public sealed class InMemoryReportingStoreTests
{
    [TestMethod]
    public async Task ProjectionContract()
    {
        var store = new InMemoryReportingStore();
        await ReportingStoreContract.VerifyProjectionAsync(store, store);
    }

    [TestMethod]
    public Task AggregateContract() => ReportingStoreContract.VerifyAggregatesAsync(new InMemoryReportingStore());

    [TestMethod]
    public Task FactsAndBacklogContract() => ReportingStoreContract.VerifyFactsAndBacklogAsync(new InMemoryReportingStore());

    [TestMethod]
    public Task RetentionAndRebuildContract() => ReportingStoreContract.VerifyRetentionAndRebuildAsync(new InMemoryReportingStore());

    [TestMethod]
    public async Task ConcurrentProjectionContract()
    {
        var store = new InMemoryReportingStore();
        await ReportingStoreContract.VerifyConcurrentProjectionsAsync(store, store);
    }

    [TestMethod]
    [DataRow(14)]
    [DataRow(12)]
    [DataRow(13)]
    public Task AccessFilteredParity(int seed) => AccessFilteredReportParity.VerifyAsync(new InMemoryReportingStore(), seed);

    [TestMethod]
    public Task RebuildHorizonContract() => ReportingStoreContract.VerifyRebuildHorizonAsync(new InMemoryReportingStore());

    [TestMethod]
    [DataRow(21)]
    [DataRow(22)]
    public Task LabelAggregatesMatchFacts(int seed) => ReportingStoreContract.VerifyLabelAggregatesMatchFactsAsync(new InMemoryReportingStore(), seed);

    [TestMethod]
    public async Task CapacityIsBounded()
    {
        var store = new InMemoryReportingStore(maximumFacts: 2);
        var scope = ReportingStoreContract.NewScope();
        var at = ReportingStoreContract.Day;
        Assert.AreEqual("rejected:CapacityExceeded", ReportingStoreContract.W(await store.ApplyAsync(scope, new ReportProjection(ReportingStoreContract.State(),
            [ReportingStoreContract.Count(ReportMetric.MessageSent, at), ReportingStoreContract.Count(ReportMetric.MessageSent, at), ReportingStoreContract.Count(ReportMetric.MessageSent, at)]), null)));
        Assert.IsNull(await store.GetConversationAsync(scope, ReportingStoreContract.State().Conversation));
        Assert.IsEmpty((await store.AggregateAsync(scope, ReportingStoreContract.Whole(at, at.AddDays(1)), AllowAllFilter.Instance)).Rows);
    }
}
