using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Reporting.Tests;

/// <summary>Store-independent projector races, run against the in-memory and the PostgreSQL reporting stores.</summary>
internal static class ProjectorConcurrencyScenarios
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>Eight concurrent syncs of one conversation (separate DI scopes) produce exactly the facts of a single replay.</summary>
    public static async Task ConcurrentSyncsOfOneConversationAsync(ReportingFixture fixture)
    {
        await fixture.StartAsync("race", team: "billing");
        for (var round = 0; round < 12; round++)
        {
            await fixture.AssignAsync("race", round % 2 == 0 ? "ann" : "bob", Minute);
            await fixture.ReplyAsync("race", round % 2 == 0 ? fixture.Ann : fixture.Bob, Minute);
            await fixture.IncomingAsync("race", Minute);
        }
        await fixture.StatusAsync("race", ConversationStatus.Resolved, Minute);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => fixture.SyncAsync("race"))));
        await AssertMatchesSingleReplayAsync(fixture, "race");
    }

    /// <summary>
    /// A late consumer (direct first-sight syncs replaying from sequence 1) races the live outbox stream while the conversation keeps
    /// changing; the result equals a single replay and rollups count every fact once.
    /// </summary>
    public static async Task LateConsumerRacesLiveStreamAsync(ReportingFixture fixture)
    {
        await fixture.StartAsync("live", team: "billing");
        var done = 0;
        var writer = Task.Run(async () =>
        {
            for (var round = 0; round < 15; round++)
            {
                await fixture.ReplyAsync("live", fixture.Ann, Minute);
                await fixture.IncomingAsync("live", Minute);
                if (round % 5 == 4)
                    await fixture.AssignAsync("live", round % 2 == 0 ? "ann" : "bob", Minute);
            }
            Volatile.Write(ref done, 1);
        });
        var late = Task.Run(async () =>
        {
            while (Volatile.Read(ref done) == 0)
                await fixture.SyncAsync("live");
        });
        var live = Task.Run(async () =>
        {
            while (Volatile.Read(ref done) == 0)
                await fixture.DispatchAsync();
        });
        await Task.WhenAll(writer, late, live);
        var dispatched = await fixture.DispatchAsync();
        Assert.AreEqual(0, dispatched.DeadLettered);
        await fixture.SyncAsync("live");
        await AssertMatchesSingleReplayAsync(fixture, "live");
    }

    /// <summary>The fixture's facts for <paramref name="conversation"/> equal a fresh single replay into an empty store, and rollups hold each exactly once.</summary>
    private static async Task AssertMatchesSingleReplayAsync(ReportingFixture fixture, string conversation)
    {
        var reference = new InMemoryReportingStore();
        var projector = new ReportingProjector(fixture.Get<IConversationStore>(), fixture.Get<IInboxStore>(), fixture.Get<IBusinessCalendarStore>(), reference,
            Options.Create(new ReportingOptions()));
        await projector.SyncAsync(fixture.Scope, new ConversationId(conversation));
        static string Key(ReportFact fact) => $"{fact.SourceId}:{fact.Metric}:{fact.Value}:{fact.OccurredAt:O}:{fact.Dimensions.Agent}:{fact.Dimensions.Team}";
        var expected = (await reference.ListFactsAsync(fixture.Scope, new ReportFactQuery(ReportingFixture.Monday.AddDays(-1), ReportingFixture.Monday.AddDays(30), limit: 1000),
            AllowAllFilter.Instance)).Items.Select(Key).ToArray();
        var actual = (await fixture.FactsAsync()).Where(fact => fact.Conversation.Value == conversation).Select(Key).ToArray();
        Assert.IsGreaterThan(10, expected.Length);
        CollectionAssert.AreEquivalent(expected, actual);
        var rollups = await fixture.Store.AggregateAsync(fixture.Scope, StoreContracts.ReportingStoreContract.Whole(ReportingFixture.Monday.AddDays(-1), ReportingFixture.Monday.AddDays(30)),
            AllowAllFilter.Instance);
        Assert.AreEqual(actual.Length, rollups.Rows.Sum(row => row.Count), "each fact is rolled up exactly once");
        var state = (await fixture.Store.GetConversationAsync(fixture.Scope, new ConversationId(conversation)))!.State;
        Assert.AreEqual((await reference.GetConversationAsync(fixture.Scope, new ConversationId(conversation)))!.State, state);
    }
}
