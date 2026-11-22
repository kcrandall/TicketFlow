using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Sla.Tests;

/// <summary>Store-independent tracker scenarios, run against the in-memory and PostgreSQL SLA stores.</summary>
internal static class SlaScenarios
{
    public static readonly DateTimeOffset Friday = new(2026, 3, 6, 20, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset Monday(int hour, int minute = 0) => new(2026, 3, 9, hour, minute, 0, TimeSpan.Zero);

    public static void AdvanceTo(SlaFixture fixture, DateTimeOffset instant) => fixture.Clock.Advance(instant - fixture.Clock.GetUtcNow());

    /// <summary>
    /// Three timer schedulers, four direct syncs (outbox redelivery), the dispatcher and concurrent label writers race at the warning and
    /// breach instants of two conversations; every clock must still produce exactly one applied, warning and breached event.
    /// </summary>
    public static async Task VerifyExactlyOnceUnderConcurrencyAsync(SlaFixture fixture)
    {
        string[] ids = ["c1", "c2"];
        foreach (var id in ids)
            await fixture.StartAsync(id);
        await fixture.DispatchAsync();
        foreach (var instant in new[] { Monday(14, 30), Monday(15, 30) })
        {
            AdvanceTo(fixture, instant);
            var label = 0;
            var tasks = new List<Task>();
            for (var index = 0; index < 3; index++)
                tasks.Add(Task.Run(fixture.RunTimersAsync));
            for (var index = 0; index < 4; index++)
            {
                var id = ids[index % ids.Length];
                tasks.Add(Task.Run(() => SyncIgnoringRacesAsync(fixture, id)));
            }
            for (var index = 0; index < 2; index++)
            {
                var id = ids[index % ids.Length];
                var name = $"race-{Interlocked.Increment(ref label)}-{instant.Hour}";
                tasks.Add(Task.Run(() => fixture.CommandAsync(service => service.UpdateLabelsAsync(fixture.Owner, new ConversationId(id), [new LabelId(name)], []))));
            }
            tasks.Add(Task.Run(fixture.DispatchAsync));
            await Task.WhenAll(tasks);
        }
        // Settle anything deferred by lost races.
        for (var round = 0; round < 3; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(2));
            await fixture.RunTimersAsync();
            await fixture.DispatchAsync();
        }
        foreach (var id in ids)
        {
            var events = await fixture.SlaEventsWithActorsAsync(id);
            Assert.IsTrue(events.All(item => item.Actor == SlaFixture.Worker));
            var kinds = events.Select(item => item.Event.Kind).ToList();
            Assert.AreEqual(1, kinds.Count(kind => kind == SlaEventCodec.AppliedKind), $"{id}: {string.Join(",", kinds)}");
            Assert.AreEqual(1, kinds.Count(kind => kind == SlaEventCodec.WarningKind), $"{id}: {string.Join(",", kinds)}");
            Assert.AreEqual(1, kinds.Count(kind => kind == SlaEventCodec.BreachedKind), $"{id}: {string.Join(",", kinds)}");
            Assert.AreEqual(events.Count, events.Select(item => item.Event.Key).Distinct().Count());
            Assert.AreEqual(0, (await fixture.Get<ISlaTrackerStore>().GetAsync(fixture.Scope, new ConversationId(id)))!.Tracker.Pending.Count);
        }
    }

    private static async Task SyncIgnoringRacesAsync(SlaFixture fixture, string id)
    {
        try
        {
            await fixture.InScopeAsync(async services =>
            {
                await services.GetRequiredService<SlaConversationTracker>().SyncAsync(fixture.Scope, new ConversationId(id));
                return 0;
            });
        }
        catch (InvalidOperationException)
        {
            // Deferred flushes and exhausted tracker races are retried by the timer; the assertions check the end state.
        }
    }
}
