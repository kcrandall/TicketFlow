using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla.StoreContracts;

/// <summary>
/// Behavioral contract every SLA store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the same
/// storage (for example two data sources), so fencing is verified across clients. Timer checks need the stores to share the supplied
/// <see cref="FakeTimeProvider"/>.
/// </summary>
public static class SlaStoreContract
{
    public static readonly DateTimeOffset At = TestClock.DefaultStart;

    public static string Describe<T>(SlaWriteResult<T> result) => result switch
    {
        SlaWriteApplied<T> => "applied",
        SlaWriteNotFound => "not-found",
        SlaWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
        SlaWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static SlaPolicy Policy(string id, string name = "Standard", int firstResponseHours = 4) => new(new SlaPolicyId(id), name,
    [
        new SlaRule("urgent", new SlaCondition([ConversationPriority.Urgent]), new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.CalendarTime),
        new SlaRule("default", SlaCondition.Always, new SlaTargets(TimeSpan.FromHours(firstResponseHours), TimeSpan.FromHours(8), TimeSpan.FromDays(3)),
            SlaClockBasis.BusinessHours, [new SlaEscalation(SlaMetric.FirstResponse, 75, [new NotifyTeamAction(new TeamId("billing"))])]),
    ], new SlaPausePolicy(PauseWhenPending: true, PauseWhenSnoozed: false), 80);

    /// <summary>A tracker with an explicit schedule (no clocks; the store contract does not interpret clocks).</summary>
    public static SlaTracker Tracker(string conversation, string inbox = "email", DateTimeOffset? nextCheckAt = null, DateTimeOffset? dueAt = null,
        DateTimeOffset? atRiskAt = null, bool breached = false, DateTimeOffset? breachedAt = null, string? team = null, string? assignee = null,
        IEnumerable<string>? labels = null, IEnumerable<KeyValuePair<string, string>>? attributes = null, long lastSequence = 1)
    {
        var urgency = dueAt is null ? breachedAt : breachedAt is null ? dueAt : dueAt < breachedAt ? dueAt : breachedAt;
        return new SlaTracker(new ConversationId(conversation), new InboxId(inbox), null,
            new SlaConversationFacts(ConversationStatusKind.Open, null, ConversationPriority.None, At, false), [], [], [], 0,
            new SlaAccessSnapshot(team is null ? null : new TeamId(team), assignee is null ? null : new UserId(assignee), (labels ?? []).Select(label => new LabelId(label)),
                attributes ?? []),
            lastSequence, At, new SlaSchedule(nextCheckAt, dueAt, atRiskAt, breached, breached || dueAt is not null ? urgency ?? dueAt ?? At : null));
    }

    public static async Task VerifyPoliciesAsync(ISlaPolicyStore primary, ISlaPolicyStore competing)
    {
        var scope = TestScopes.Create();
        var created = Assert.IsInstanceOfType<SlaWriteApplied<StoredSlaPolicy>>((await primary.CreateAsync(scope, Policy("standard"))).Value).Value;
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(scope, Policy("standard"))));
        var read = await competing.GetAsync(scope, new SlaPolicyId("standard"));
        Assert.IsNotNull(read);
        Assert.AreEqual(Policy("standard"), read.Policy);
        Assert.AreEqual(scope, read.Scope);

        var updated = Assert.IsInstanceOfType<SlaWriteApplied<StoredSlaPolicy>>((await competing.UpdateAsync(scope, Policy("standard", "Renamed"), 1)).Value).Value;
        Assert.AreEqual(2, updated.Revision);
        Assert.AreEqual("conflict:2", Describe(await primary.UpdateAsync(scope, Policy("standard", "Stale"), 1)));
        Assert.AreEqual("not-found", Describe(await primary.UpdateAsync(scope, Policy("missing"), 1)));
        Assert.AreEqual("Renamed", (await primary.GetAsync(scope, new SlaPolicyId("standard")))!.Policy.Name);

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, new SlaPolicyId("standard")));
            Assert.AreEqual(0, (await primary.ListAsync(other)).Count);
            Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(other, new SlaPolicyId("standard"), 2)));
        }

        await primary.CreateAsync(scope, Policy("alpha"));
        CollectionAssert.AreEqual(new[] { "alpha", "standard" }, (await competing.ListAsync(scope)).Select(item => item.Policy.Id.Value).ToArray());
        Assert.AreEqual("conflict:2", Describe(await primary.DeleteAsync(scope, new SlaPolicyId("standard"), 1)));
        Assert.AreEqual("applied", Describe(await primary.DeleteAsync(scope, new SlaPolicyId("standard"), 2)));
        Assert.IsNull(await competing.GetAsync(scope, new SlaPolicyId("standard")));

        var capacityScope = TestScopes.Create(workspaceId: "capacity");
        await Task.WhenAll(Enumerable.Range(0, ISlaPolicyStore.MaximumPoliciesPerWorkspace + 8).Select(index =>
            (index % 2 == 0 ? primary : competing).CreateAsync(capacityScope, Policy($"p{index:D3}"))));
        Assert.AreEqual(ISlaPolicyStore.MaximumPoliciesPerWorkspace, (await primary.ListAsync(capacityScope)).Count);
        Assert.AreEqual("rejected:CapacityExceeded", Describe(await primary.CreateAsync(capacityScope, Policy("overflow"))));
    }

    public static async Task VerifyTrackersAsync(ISlaTrackerStore primary, ISlaTrackerStore competing)
    {
        var scope = TestScopes.Create();
        var tracker = Tracker("c1", team: "billing", labels: ["vip"], attributes: [new("tier", "gold")]);
        var created = Assert.IsInstanceOfType<SlaWriteApplied<StoredSlaTracker>>((await primary.WriteAsync(scope, tracker, null)).Value).Value;
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual("conflict:1", Describe(await competing.WriteAsync(scope, tracker, null)));
        var read = await competing.GetAsync(scope, new ConversationId("c1"));
        Assert.IsNotNull(read);
        Assert.AreEqual(SlaTrackerCodec.Serialize(tracker), SlaTrackerCodec.Serialize(read.Tracker));
        Assert.AreEqual(scope, read.Scope);

        Assert.AreEqual("applied", Describe(await competing.WriteAsync(scope, Tracker("c1", lastSequence: 2), 1)));
        Assert.AreEqual("conflict:2", Describe(await primary.WriteAsync(scope, Tracker("c1", lastSequence: 3), 1)));
        Assert.AreEqual("not-found", Describe(await primary.WriteAsync(scope, Tracker("missing"), 1)));
        Assert.AreEqual(2, (await primary.GetAsync(scope, new ConversationId("c1")))!.Tracker.LastSequence);

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, new ConversationId("c1")));
            Assert.AreEqual("not-found", Describe(await primary.WriteAsync(other, Tracker("c1"), 2)));
            Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(other, new ConversationId("c1"), 2)));
        }

        // Concurrent compare-and-set writers: exactly one wins per revision.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => (index % 2 == 0 ? primary : competing).WriteAsync(scope, Tracker("c1", lastSequence: 10 + index), 2)));
        Assert.AreEqual(1, results.Count(result => result.Value is SlaWriteApplied<StoredSlaTracker>));

        Assert.AreEqual("conflict:3", Describe(await primary.DeleteAsync(scope, new ConversationId("c1"), 2)));
        Assert.AreEqual("applied", Describe(await competing.DeleteAsync(scope, new ConversationId("c1"), 3)));
        Assert.IsNull(await primary.GetAsync(scope, new ConversationId("c1")));
        Assert.AreEqual("applied", Describe(await primary.WriteAsync(scope, tracker, null)), "a deleted tracker can be recreated");
    }

    public static async Task VerifyTimersAsync(ISlaTrackerStore primary, ISlaTrackerStore competing, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "timers");
        var other = TestScopes.Create(workspaceId: "timers-other");
        var now = clock.GetUtcNow();
        await primary.WriteAsync(scope, Tracker("late", nextCheckAt: now.AddMinutes(10)), null);
        await primary.WriteAsync(scope, Tracker("early", nextCheckAt: now.AddMinutes(5)), null);
        await primary.WriteAsync(other, Tracker("other", nextCheckAt: now.AddMinutes(7)), null);
        await primary.WriteAsync(scope, Tracker("never"), null);

        Assert.AreEqual(0, (await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Count, "nothing is due yet");
        clock.Advance(TimeSpan.FromMinutes(8));
        var claims = await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        CollectionAssert.AreEqual(new[] { "early", "other" }, claims.Select(claim => claim.Conversation.Value).ToArray(), "earliest first, across scopes");
        Assert.AreEqual(other, claims[1].Scope);
        Assert.AreEqual(now.AddMinutes(5), claims[0].DueAt);
        Assert.AreEqual(0, (await competing.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Count, "a live lease blocks other claimers");

        // Release requires the current fence and a live lease.
        Assert.IsFalse(await competing.ReleaseAsync(claims[0] with { Fence = claims[0].Fence + 1 }, clock.GetUtcNow()));
        Assert.IsTrue(await competing.ReleaseAsync(claims[0], clock.GetUtcNow().AddMinutes(1)));
        Assert.IsFalse(await primary.ReleaseAsync(claims[0], clock.GetUtcNow()), "a released claim cannot be released again");
        Assert.AreEqual(0, (await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Count, "released for one minute");

        // Lease expiry makes the timer claimable again with a higher fence; the old claim is stale.
        clock.Advance(TimeSpan.FromMinutes(1.5));
        var again = await competing.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        CollectionAssert.AreEquivalent(new[] { "early", "other" }, again.Select(claim => claim.Conversation.Value).ToArray());
        var early = again.Single(claim => claim.Conversation.Value == "early");
        Assert.IsTrue(early.Fence > claims[0].Fence);
        Assert.IsFalse(await primary.ReleaseAsync(claims[1], clock.GetUtcNow()), "the expired claim's fence is stale");

        // A tracker write reschedules the timer and clears the lease.
        var stored = (await primary.GetAsync(scope, new ConversationId("early")))!;
        await primary.WriteAsync(scope, Tracker("early", nextCheckAt: clock.GetUtcNow()), stored.Revision);
        clock.Advance(TimeSpan.FromMinutes(0.75));
        var rescheduled = await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1));
        CollectionAssert.AreEqual(new[] { "early", "late" }, rescheduled.Select(claim => claim.Conversation.Value).ToArray());
        Assert.IsTrue(rescheduled[0].Fence > early.Fence, "fences never repeat");
        stored = (await primary.GetAsync(scope, new ConversationId("early")))!;
        await competing.WriteAsync(scope, Tracker("early"), stored.Revision);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.IsFalse((await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Any(claim => claim.Conversation.Value is "early" or "never"), "no next check, no timer");

        await primary.DeleteAsync(scope, new ConversationId("late"), 1);
        Assert.IsFalse((await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Any(claim => claim.Conversation.Value == "late"), "deleting a tracker deletes its timer");
    }

    public static async Task VerifyConcurrentClaimsAsync(IReadOnlyList<ISlaTrackerStore> clients, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "concurrent");
        var due = clock.GetUtcNow();
        for (var index = 0; index < 60; index++)
            await clients[index % clients.Count].WriteAsync(scope, Tracker($"t{index:D2}", nextCheckAt: due.AddSeconds(-index)), null);
        var claims = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() => clients[index % clients.Count].ClaimDueAsync(10, TimeSpan.FromMinutes(5)))));
        var all = claims.SelectMany(batch => batch).ToList();
        Assert.AreEqual(all.Count, all.Select(claim => claim.Conversation.Value).Distinct().Count(), "no timer is shared by two live leases");
        for (var batch = await clients[0].ClaimDueAsync(10, TimeSpan.FromMinutes(5)); batch.Count > 0; batch = await clients[0].ClaimDueAsync(10, TimeSpan.FromMinutes(5)))
            all.AddRange(batch);
        Assert.AreEqual(60, all.Count, "every due timer is claimed");
        Assert.AreEqual(60, all.Select(claim => claim.Conversation.Value).Distinct().Count(), "and exactly once");
    }

    public static async Task VerifyRiskListAsync(ISlaTrackerStore store)
    {
        var scope = TestScopes.Create(workspaceId: "risk");
        var now = At.AddHours(10);
        // ok: due later, not at risk; risky: past at-risk; overdue: due passed but timer not fired; breached: flagged; paused: nothing due.
        await store.WriteAsync(scope, Tracker("ok", dueAt: now.AddHours(3), atRiskAt: now.AddHours(2), team: "billing"), null);
        await store.WriteAsync(scope, Tracker("risky", dueAt: now.AddHours(1), atRiskAt: now.AddMinutes(-5), team: "billing", labels: ["vip"]), null);
        await store.WriteAsync(scope, Tracker("risky-sales", inbox: "chat", dueAt: now.AddMinutes(30), atRiskAt: now.AddMinutes(-1), team: "sales"), null);
        await store.WriteAsync(scope, Tracker("overdue", dueAt: now.AddMinutes(-1), atRiskAt: now.AddMinutes(-30), assignee: "agent"), null);
        await store.WriteAsync(scope, Tracker("breached", breached: true, breachedAt: now.AddHours(-2), attributes: [new("tier", "gold")]), null);
        await store.WriteAsync(scope, Tracker("paused"), null);
        await store.WriteAsync(TestScopes.Create(workspaceId: "risk-other"), Tracker("foreign", breached: true, breachedAt: now.AddHours(-3)), null);

        async Task<string[]> Ids(SlaRiskQuery query, AccessFilter? filter = null) =>
            [.. (await store.ListAsync(scope, query, now, filter ?? AllowAllFilter.Instance)).Trackers.Select(item => item.Conversation.Value)];

        CollectionAssert.AreEqual(new[] { "breached", "overdue", "risky-sales", "risky" }, await Ids(new SlaRiskQuery()));
        CollectionAssert.AreEqual(new[] { "breached", "overdue" }, await Ids(new SlaRiskQuery(SlaRiskFilter.Breached)));
        CollectionAssert.AreEqual(new[] { "risky-sales", "risky" }, await Ids(new SlaRiskQuery(SlaRiskFilter.AtRisk)));
        CollectionAssert.AreEqual(new[] { "risky-sales" }, await Ids(new SlaRiskQuery(inbox: new InboxId("chat"))));
        Assert.AreEqual(0, (await Ids(new SlaRiskQuery(), DenyAllFilter.Instance)).Length);

        // Keyset paging.
        var first = await store.ListAsync(scope, new SlaRiskQuery(limit: 3), now, AllowAllFilter.Instance);
        CollectionAssert.AreEqual(new[] { "breached", "overdue", "risky-sales" }, first.Trackers.Select(item => item.Conversation.Value).ToArray());
        Assert.IsNotNull(first.Next);
        var second = await store.ListAsync(scope, new SlaRiskQuery(limit: 3, after: first.Next), now, AllowAllFilter.Instance);
        CollectionAssert.AreEqual(new[] { "risky" }, second.Trackers.Select(item => item.Conversation.Value).ToArray());
        Assert.IsNull(second.Next);

        // Access filters run inside the query and agree with the reference evaluator.
        AccessFilter[] filters =
        [
            new ScopeFilter(scope),
            new TeamInFilter([new TeamId("billing")]),
            new InboxInFilter([new InboxId("chat")]),
            new AssigneeIsFilter(new UserId("agent")),
            new HasLabelFilter(new LabelId("vip")),
            new AttributeEqualsFilter("tier", "gold"),
            new NotFilter(new TeamInFilter([new TeamId("billing")])),
            new NotFilter(new HasLabelFilter(new LabelId("vip"))),
            new OrFilter(new TeamInFilter([new TeamId("sales")]), new AttributeInFilter("tier", ["gold", "silver"])),
            new AndFilter(new ScopeFilter(scope), new NotFilter(new AttributeEqualsFilter("tier", "gold"))),
        ];
        var all = (await store.ListAsync(scope, new SlaRiskQuery(limit: 100), now, AllowAllFilter.Instance)).Trackers;
        foreach (var filter in filters)
        {
            var expected = all.Where(item => AccessFilterEvaluator.Matches(filter, item.Tracker.Resource(scope))).Select(item => item.Conversation.Value).ToArray();
            CollectionAssert.AreEqual(expected, await Ids(new SlaRiskQuery(limit: 100), filter), filter.GetType().Name);
        }
    }
}
