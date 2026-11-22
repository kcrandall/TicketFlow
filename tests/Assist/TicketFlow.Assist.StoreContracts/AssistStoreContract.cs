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
        AssistWriteApplied<StoredAssistSettings> applied => $"applied:{applied.Value.Revision}",
        AssistWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
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
        Assert.AreEqual(new AssistUsageTotal(AssistFeature.Triage, "model-b", 1, 0, 5, 6, 7, 8, 0), triage);
        Assert.ThrowsExactly<ArgumentException>(() => store.SummarizeAsync(scope, Noon, Noon.AddDays(93)).GetAwaiter().GetResult());

        Assert.AreEqual(1, await store.PurgeUsageAsync(scope, Noon.AddMinutes(-1), 100));
        Assert.IsNull(await competing.GetUsageAsync(scope, old.Id));
        Assert.AreEqual(1, await store.PurgeUsageAsync(scope, Noon.AddHours(1), 1));
        Assert.AreEqual(1, await store.PurgeUsageAsync(scope, Noon.AddHours(1), 100), "reserved records are never purged");
        Assert.IsNotNull(await store.GetUsageAsync(scope, c.Id));
    }
}
