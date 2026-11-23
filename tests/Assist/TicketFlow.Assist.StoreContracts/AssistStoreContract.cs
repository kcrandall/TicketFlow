using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Assist.StoreContracts;

/// <summary>Shared <see cref="IAssistStore"/> contract. <c>competing</c> is an independent client over the same storage.</summary>
public static class AssistStoreContract
{
    public static DateTimeOffset Noon { get; } = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    public static AssistUsageRecord Reservation(DateTimeOffset at, long tokens = 100, AssistFeature feature = AssistFeature.ConversationSummary, string model = "model-a", Guid? id = null) =>
        new(id ?? Guid.NewGuid(), feature, model, new AccessActor(AccessActorKind.User, "agent"), new ConversationId("c1"), AssistUsageState.Reserved, tokens, AssistUsage.Zero, at, null);

    public static string R(AssistReservationResult result) => result switch
    {
        AssistUsageReserved => "reserved",
        AssistQuotaExceeded exceeded => $"exceeded:{exceeded.Kind}",
    };

    public static string W(AssistWriteResult<StoredAssistSettings> result) => result switch
    {
        StoreApplied<StoredAssistSettings> applied => $"applied:{applied.Value.Revision}",
        StoreConflict conflict => $"conflict:{conflict.CurrentRevision}",
    };

    public static async Task VerifySettingsAsync(IAssistStore store, IAssistStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "settings-" + Guid.NewGuid().ToString("N"));
        Assert.IsNull(await store.GetSettingsAsync(scope));
        var settings = new AssistSettings(true, 5000, 10, new Dictionary<AssistFeature, string> { [AssistFeature.Triage] = "model-b" });
        Assert.AreEqual("conflict:0", W(await store.PutSettingsAsync(scope, settings, 1)));
        Assert.AreEqual("applied:1", W(await store.PutSettingsAsync(scope, settings, 0)));
        Assert.AreEqual("conflict:1", W(await competing.PutSettingsAsync(scope, settings, 0)));
        var stored = (await competing.GetSettingsAsync(scope))!;
        Assert.AreEqual(settings, stored.Settings);
        Assert.AreEqual(1, stored.Revision);
        Assert.AreEqual(scope, stored.Scope);
        var updated = new AssistSettings(false, 9000, 20);
        var outcomes = await Task.WhenAll(store.PutSettingsAsync(scope, updated, 1), competing.PutSettingsAsync(scope, updated, 1));
        CollectionAssert.AreEquivalent(new[] { "applied:2", "conflict:2" }, outcomes.Select(W).ToArray());
        Assert.AreEqual(updated, (await store.GetSettingsAsync(scope))!.Settings);
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await store.GetSettingsAsync(other));
    }

    public static async Task VerifyReservationsAsync(IAssistStore store, IAssistStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "quota-" + Guid.NewGuid().ToString("N"));
        var first = Reservation(Noon, 600);
        Assert.AreEqual("reserved", R(await store.ReserveAsync(scope, first, 1000, 100)));
        Assert.AreEqual("exceeded:DailyTokens", R(await competing.ReserveAsync(scope, Reservation(Noon.AddMinutes(5), 500), 1000, 100)));
        var exceeded = (AssistQuotaExceeded)(await competing.ReserveAsync(scope, Reservation(Noon.AddMinutes(5), 500), 1000, 100)).Value!;
        Assert.AreEqual(new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero), exceeded.RetryAt);

        // Settling with actual usage frees the reserved difference.
        var settled = await competing.SettleAsync(scope, first.Id, AssistUsageState.Completed, new AssistUsage(100, 50, 30, 20), Noon.AddSeconds(3));
        Assert.IsNotNull(settled);
        Assert.AreEqual(AssistUsageState.Completed, settled.State);
        Assert.AreEqual(200, settled.CountedTokens);
        Assert.IsNull(await store.SettleAsync(scope, first.Id, AssistUsageState.Failed, AssistUsage.Zero, Noon.AddSeconds(4)), "settles once");
        Assert.IsNull(await store.SettleAsync(scope, Guid.NewGuid(), AssistUsageState.Failed, AssistUsage.Zero, Noon));
        var read = (await store.GetUsageAsync(scope, first.Id))!;
        Assert.AreEqual(new AssistUsage(100, 50, 30, 20), read.Usage);
        Assert.AreEqual(Noon.AddSeconds(3), read.CompletedAt);
        Assert.AreEqual(first.Actor, read.Actor);
        Assert.AreEqual(first.Conversation, read.Conversation);
        Assert.AreEqual("reserved", R(await competing.ReserveAsync(scope, Reservation(Noon.AddMinutes(5), 800), 1000, 100)));
        Assert.AreEqual("exceeded:DailyTokens", R(await store.ReserveAsync(scope, Reservation(Noon.AddMinutes(6), 1), 1000, 100)));

        // A new UTC day starts a new token window.
        Assert.AreEqual("reserved", R(await store.ReserveAsync(scope, Reservation(Noon.AddDays(1), 900), 1000, 100)));

        // Requests per minute count records created in (now - 60 s, now].
        var rateScope = TestScopes.Create(workspaceId: "rate-" + Guid.NewGuid().ToString("N"));
        Assert.AreEqual("reserved", R(await store.ReserveAsync(rateScope, Reservation(Noon), 1_000_000, 2)));
        Assert.AreEqual("reserved", R(await competing.ReserveAsync(rateScope, Reservation(Noon.AddSeconds(10)), 1_000_000, 2)));
        var limited = (await store.ReserveAsync(rateScope, Reservation(Noon.AddSeconds(20)), 1_000_000, 2)).Value as AssistQuotaExceeded;
        Assert.IsNotNull(limited);
        Assert.AreEqual(AssistQuotaKind.RequestsPerMinute, limited.Kind);
        Assert.AreEqual(Noon.AddMinutes(1), limited.RetryAt);
        Assert.AreEqual("reserved", R(await store.ReserveAsync(rateScope, Reservation(Noon.AddSeconds(61)), 1_000_000, 2)));

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await store.GetUsageAsync(other, first.Id));
            Assert.IsNull(await store.SettleAsync(other, first.Id, AssistUsageState.Failed, AssistUsage.Zero, Noon));
            Assert.AreEqual("reserved", R(await store.ReserveAsync(other, Reservation(Noon, 1000), 1000, 1)));
        }
    }

    public static async Task VerifyConcurrentReservationsAsync(IAssistStore store, IAssistStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "race-" + Guid.NewGuid().ToString("N"));
        var attempts = Enumerable.Range(0, 20).Select(index => (index % 2 == 0 ? store : competing).ReserveAsync(scope, Reservation(Noon.AddMilliseconds(index), 100), 1000, 1000));
        var outcomes = await Task.WhenAll(attempts);
        Assert.AreEqual(10, outcomes.Count(outcome => outcome.Value is AssistUsageReserved));
        Assert.AreEqual(10, outcomes.Count(outcome => outcome.Value is AssistQuotaExceeded));
        var summary = await store.SummarizeAsync(scope, Noon.AddHours(-1), Noon.AddHours(1));
        Assert.AreEqual(10, summary.Totals.Single().Requests);
        Assert.AreEqual(1000, summary.Totals.Single().ReservedTokens);
    }

    /// <summary>Estimated settlement charges the reservation; partial and full releases adjust the day's counted tokens.</summary>
    public static async Task VerifyEstimatedSettlementAsync(IAssistStore store, IAssistStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "estimate-" + Guid.NewGuid().ToString("N"));
        var ambiguous = Reservation(Noon, 400);
        var released = Reservation(Noon.AddSeconds(1), 400);
        var actual = Reservation(Noon.AddSeconds(2), 200);
        foreach (var record in new[] { ambiguous, released, actual })
            Assert.AreEqual("reserved", R(await store.ReserveAsync(scope, record, 1000, 100)));
        Assert.AreEqual("exceeded:DailyTokens", R(await store.ReserveAsync(scope, Reservation(Noon.AddSeconds(3), 1), 1000, 100)));

        var charged = (await competing.SettleAsync(scope, ambiguous.Id, AssistUsageState.Failed, AssistUsage.Zero, Noon.AddSeconds(5), estimated: true))!;
        Assert.IsTrue(charged.Estimated);
        Assert.AreEqual(400, charged.CountedTokens);
        Assert.IsTrue((await store.GetUsageAsync(scope, ambiguous.Id))!.Estimated);
        Assert.AreEqual("exceeded:DailyTokens", R(await store.ReserveAsync(scope, Reservation(Noon.AddSeconds(6), 1), 1000, 100)), "estimates stay charged");

        await competing.SettleAsync(scope, released.Id, AssistUsageState.Failed, AssistUsage.Zero, Noon.AddSeconds(7));
        await competing.SettleAsync(scope, actual.Id, AssistUsageState.Completed, new AssistUsage(50, 50), Noon.AddSeconds(8));
        // Counted now: 400 (estimate) + 0 (released) + 100 (actual) = 500.
        Assert.AreEqual("reserved", R(await store.ReserveAsync(scope, Reservation(Noon.AddSeconds(9), 500), 1000, 100)));
        Assert.AreEqual("exceeded:DailyTokens", R(await competing.ReserveAsync(scope, Reservation(Noon.AddSeconds(10), 1), 1000, 100)));

        var summary = await store.SummarizeAsync(scope, Noon.AddHours(-1), Noon.AddHours(1));
        Assert.AreEqual(400, summary.Totals.Single().EstimatedTokens);
        Assert.AreEqual(500, summary.Totals.Single().ReservedTokens);
    }

    /// <summary>Abandoned reservations (any scope) are settled as Failed at their estimate, oldest first, in bounded batches.</summary>
    public static async Task VerifySweepAsync(IAssistStore store, IAssistStore competing)
    {
        var epoch = new DateTimeOffset(2001, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var first = TestScopes.Create(workspaceId: "sweep-a-" + Guid.NewGuid().ToString("N"));
        var second = TestScopes.Create(workspaceId: "sweep-b-" + Guid.NewGuid().ToString("N"));
        var stale = new[] { (first, Reservation(epoch, 300)), (second, Reservation(epoch.AddMinutes(1), 200)), (first, Reservation(epoch.AddMinutes(2), 100)) };
        var fresh = Reservation(epoch.AddHours(2), 50);
        foreach (var (scope, record) in stale)
            Assert.AreEqual("reserved", R(await store.ReserveAsync(scope, record, 1000, 100)));
        Assert.AreEqual("reserved", R(await store.ReserveAsync(first, fresh, 1000, 100)));

        var sweptFirst = await competing.SweepReservationsAsync(epoch.AddHours(1), epoch.AddHours(3), 2);
        Assert.AreEqual(2, sweptFirst, "first sweep");
        Assert.AreEqual(1, await store.SweepReservationsAsync(epoch.AddHours(1), epoch.AddHours(3), 100));
        Assert.AreEqual(0, await store.SweepReservationsAsync(epoch.AddHours(1), epoch.AddHours(3), 100));
        foreach (var (scope, record) in stale)
        {
            var swept = (await store.GetUsageAsync(scope, record.Id))!;
            Assert.AreEqual(AssistUsageState.Failed, swept.State);
            Assert.IsTrue(swept.Estimated);
            Assert.AreEqual(record.ReservedTokens, swept.CountedTokens);
            Assert.AreEqual(epoch.AddHours(3), swept.CompletedAt);
            Assert.IsNull(await store.SettleAsync(scope, record.Id, AssistUsageState.Completed, new AssistUsage(1, 1), epoch.AddHours(4)), "already settled");
        }
        Assert.AreEqual(AssistUsageState.Reserved, (await store.GetUsageAsync(first, fresh.Id))!.State);
        // Charges are unchanged by the sweep: 300 + 100 + 50 counted in the first workspace.
        Assert.AreEqual("exceeded:DailyTokens", R(await store.ReserveAsync(first, Reservation(epoch.AddHours(5), 551), 1000, 100)));
        Assert.AreEqual("reserved", R(await store.ReserveAsync(first, Reservation(epoch.AddHours(5), 550), 1000, 100)));
    }

    /// <summary>Concurrent reservations and settlements across two clients never let the counted tokens exceed the limit.</summary>
    public static async Task VerifyConcurrentReserveAndSettleAsync(IAssistStore store, IAssistStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "mixed-" + Guid.NewGuid().ToString("N"));
        var reserved = new System.Collections.Concurrent.ConcurrentBag<AssistUsageRecord>();
        async Task WorkerAsync(IAssistStore client, int worker)
        {
            for (var index = 0; index < 10; index++)
            {
                var record = Reservation(Noon.AddMilliseconds(worker * 100 + index), 100);
                if ((await client.ReserveAsync(scope, record, 1000, 10_000)).Value is AssistUsageReserved)
                {
                    reserved.Add(record);
                    await client.SettleAsync(scope, record.Id, AssistUsageState.Completed, new AssistUsage(40, 20), Noon.AddMinutes(1));
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => WorkerAsync(worker % 2 == 0 ? store : competing, worker)));
        var counted = 0L;
        foreach (var record in reserved)
            counted += (await store.GetUsageAsync(scope, record.Id))!.CountedTokens;
        Assert.IsTrue(counted <= 1000, $"counted {counted}");
        Assert.IsGreaterThan(10, reserved.Count, "settled actual usage (60) below the reservation (100) frees room for more reservations");
        var remaining = 1000 - counted;
        Assert.AreEqual(remaining >= 1 ? "reserved" : "exceeded:DailyTokens", R(await store.ReserveAsync(scope, Reservation(Noon.AddMinutes(2), Math.Max(1, remaining)), 1000, 10_000)));
        Assert.AreEqual("exceeded:DailyTokens", R(await competing.ReserveAsync(scope, Reservation(Noon.AddMinutes(3), 1), 1000, 10_000)));
    }

    public static async Task VerifySummaryAndPurgeAsync(IAssistStore store, IAssistStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "summary-" + Guid.NewGuid().ToString("N"));
        var a = Reservation(Noon, 10, AssistFeature.Triage, "model-b");
        var b = Reservation(Noon.AddMinutes(1), 10, AssistFeature.ConversationSummary, "model-a");
        var c = Reservation(Noon.AddMinutes(2), 10, AssistFeature.ConversationSummary, "model-a");
        var old = Reservation(Noon.AddDays(-3), 10, AssistFeature.ReplyDraft, "model-a");
        foreach (var record in new[] { a, b, c, old })
            Assert.AreEqual("reserved", R(await store.ReserveAsync(scope, record, 1_000_000, 100)));
        await competing.SettleAsync(scope, a.Id, AssistUsageState.Completed, new AssistUsage(5, 6, 7, 8), Noon);
        await competing.SettleAsync(scope, b.Id, AssistUsageState.Failed, AssistUsage.Zero, Noon);
        await competing.SettleAsync(scope, old.Id, AssistUsageState.Completed, new AssistUsage(1, 1), Noon);
        var summary = await competing.SummarizeAsync(scope, Noon.AddHours(-1), Noon.AddHours(1));
        Assert.AreEqual(2, summary.Totals.Count);
        var summaryTotal = summary.Totals[0];
        Assert.AreEqual(AssistFeature.ConversationSummary, summaryTotal.Feature);
        Assert.AreEqual(2, summaryTotal.Requests);
        Assert.AreEqual(1, summaryTotal.Failed);
        Assert.AreEqual(10, summaryTotal.ReservedTokens);
        var triage = summary.Totals[1];
        Assert.AreEqual(new AssistUsageTotal(AssistFeature.Triage, "model-b", 1, 0, 5, 6, 7, 8, 0, 0), triage);
        Assert.ThrowsExactly<ArgumentException>(() => store.SummarizeAsync(scope, Noon, Noon.AddDays(93)).GetAwaiter().GetResult());

        Assert.AreEqual(1, await store.PurgeUsageAsync(scope, Noon.AddMinutes(-1), 100));
        Assert.IsNull(await competing.GetUsageAsync(scope, old.Id));
        Assert.AreEqual(1, await store.PurgeUsageAsync(scope, Noon.AddHours(1), 1));
        Assert.AreEqual(1, await store.PurgeUsageAsync(scope, Noon.AddHours(1), 100), "reserved records are never purged");
        Assert.IsNotNull(await store.GetUsageAsync(scope, c.Id));
    }
}
