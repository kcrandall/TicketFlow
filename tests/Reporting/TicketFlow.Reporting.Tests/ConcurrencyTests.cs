using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Reporting.Tests;

[TestClass]
public sealed class ConcurrencyTests
{
    [TestMethod]
    public async Task ConcurrentSyncsOfOneConversationCountOnce()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await ProjectorConcurrencyScenarios.ConcurrentSyncsOfOneConversationAsync(fixture);
    }

    [TestMethod]
    public async Task LateConsumerRacingTheLiveStreamCountsOnce()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await ProjectorConcurrencyScenarios.LateConsumerRacesLiveStreamAsync(fixture);
    }

    [TestMethod]
    public async Task SyncGivesUpAfterMaximumWriteAttempts()
    {
        using var fixture = await ReportingFixture.CreateAsync();
        await fixture.StartAsync("c1");
        var store = new AlwaysConflictingStore();
        var projector = new ReportingProjector(fixture.Get<IConversationStore>(), fixture.Get<IInboxStore>(), fixture.Get<IBusinessCalendarStore>(), store,
            Options.Create(new ReportingOptions { MaximumWriteAttempts = 3 }));
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => projector.SyncAsync(fixture.Scope, new ConversationId("c1")));
        StringAssert.Contains(error.Message, "kept winning", StringComparison.Ordinal);
        Assert.AreEqual(3, store.Applies);
    }

    /// <summary>A store whose every projection loses the revision race (a competing writer always wins).</summary>
    private sealed class AlwaysConflictingStore : IReportingStore
    {
        private readonly InMemoryReportingStore inner = new();

        public int Applies;

        public Task<StoredReportConversation?> GetConversationAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default) =>
            inner.GetConversationAsync(scope, conversation, cancellationToken);

        public Task<ReportWriteResult> ApplyAsync(TicketScope scope, ReportProjection projection, long? expectedRevision, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Applies);
            return Task.FromResult<ReportWriteResult>(new ReportWriteConflict(7));
        }

        public Task<ReportWriteResult> DeleteConversationAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.DeleteConversationAsync(scope, conversation, expectedRevision, cancellationToken);

        public Task<ReportAggregateResult> AggregateAsync(TicketScope scope, ReportAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default) =>
            inner.AggregateAsync(scope, query, filter, cancellationToken);

        public Task<long> CountBacklogAsync(TicketScope scope, ReportFilter query, AccessFilter filter, CancellationToken cancellationToken = default) =>
            inner.CountBacklogAsync(scope, query, filter, cancellationToken);

        public Task<ReportFactPage> ListFactsAsync(TicketScope scope, ReportFactQuery query, AccessFilter filter, CancellationToken cancellationToken = default) =>
            inner.ListFactsAsync(scope, query, filter, cancellationToken);

        public Task<int> PruneFactsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default) => inner.PruneFactsAsync(olderThan, limit, cancellationToken);

        public Task<int> PruneRollupsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default) => inner.PruneRollupsAsync(olderThan, limit, cancellationToken);

        public Task<ReportRebuildResult> RebuildRollupsAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            inner.RebuildRollupsAsync(scope, from, to, cancellationToken);

        public Task<DateTimeOffset?> GetFactHorizonAsync(CancellationToken cancellationToken = default) => inner.GetFactHorizonAsync(cancellationToken);
    }
}
