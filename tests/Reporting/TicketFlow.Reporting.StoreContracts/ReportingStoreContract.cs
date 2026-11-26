using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Reporting.StoreContracts;

/// <summary>
/// Behavioral contract every reporting store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the
/// same storage. Each verification uses a fresh scope.
/// </summary>
public static class ReportingStoreContract
{
    public static readonly DateTimeOffset Day = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);

    public static TicketScope NewScope() => TestScopes.Create(workspaceId: "rp-" + Guid.NewGuid().ToString("N"));

    public static ReportConversationState State(string conversation = "c1", long sequence = 1, ConversationStatusKind status = ConversationStatusKind.Open,
        string inbox = "i0", string? team = "t0", string? assignee = "u0", params string[] labels) =>
        new(new ConversationId(conversation), new InboxId(inbox), "email", Day, status, assignee is null ? null : new UserId(assignee), team is null ? null : new TeamId(team),
            ConversationPriority.High, labels.Select(label => new LabelId(label)), null, null, 0, sequence);

    public static ReportDimensions Dimensions(string inbox = "i0", string? team = "t0", string? agent = "u0", string? channel = "email",
        ConversationPriority priority = ConversationPriority.High, params string[] labels) =>
        new(new InboxId(inbox), team is null ? null : new TeamId(team), agent is null ? null : new UserId(agent), channel, priority, labels.Select(label => new LabelId(label)));

    public static ReportFact Count(ReportMetric metric, DateTimeOffset at, string conversation = "c1", ReportDimensions? dimensions = null, Guid? source = null) =>
        new(source ?? Guid.NewGuid(), metric, new ConversationId(conversation), at, dimensions ?? Dimensions());

    public static ReportFact Duration(ReportMetric metric, DateTimeOffset at, TimeSpan value, string conversation = "c1", ReportDimensions? dimensions = null, Guid? source = null) =>
        new(source ?? Guid.NewGuid(), metric, new ConversationId(conversation), at, dimensions ?? Dimensions(), ReportFact.Microseconds(value));

    public static string W(ReportWriteResult result) => result switch
    {
        ReportWriteApplied applied => $"applied:{applied.Conversation.Revision}:{applied.InsertedFacts}",
        ReportWriteConflict conflict => $"conflict:{conflict.CurrentRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}",
        ReportWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static string B(ReportRebuildResult result) => result switch
    {
        ReportRebuilt rebuilt => $"rebuilt:{rebuilt.Facts}",
        ReportRebuildRefused refused => $"refused:{refused.FactHorizon:O}",
    };

    public static ReportAggregateQuery Whole(DateTimeOffset from, DateTimeOffset to, ReportDimension groupBy = ReportDimension.None, ReportFilter? filter = null, bool histograms = false,
        params ReportMetric[] metrics) =>
        new([from, to], metrics.Length == 0 ? ReportMetrics.All : metrics, filter, groupBy, histograms);

    public static long Total(ReportAggregateResult result, ReportMetric metric, string? group = null, bool anyGroup = true) =>
        result.Rows.Where(row => row.Metric == metric && (anyGroup || string.Equals(row.Group, group, StringComparison.Ordinal))).Sum(row => row.Count);

    public static async Task VerifyProjectionAsync(IReportingStore primary, IReportingStore competing)
    {
        var scope = NewScope();
        var id = new ConversationId("c1");
        Assert.IsNull(await primary.GetConversationAsync(scope, id));
        var created = Count(ReportMetric.ConversationCreated, Day.AddHours(9));
        var received = Count(ReportMetric.MessageReceived, Day.AddHours(9).AddMinutes(1));
        Assert.AreEqual("conflict:none", W(await primary.ApplyAsync(scope, new ReportProjection(State(), [created]), 1)));
        Assert.AreEqual("applied:1:2", W(await primary.ApplyAsync(scope, new ReportProjection(State(), [created, received, received]), null)));
        Assert.AreEqual("conflict:1", W(await competing.ApplyAsync(scope, new ReportProjection(State(), [created]), null)));
        var stored = (await competing.GetConversationAsync(scope, id))!;
        Assert.AreEqual(1, stored.Revision);
        Assert.AreEqual(State(), stored.State);
        Assert.AreEqual(scope, stored.Scope);

        // A replay of the same facts (even with different content) inserts nothing and leaves rollups untouched: exactly once per source.
        var tampered = new ReportFact(received.SourceId, received.Metric, received.Conversation, received.OccurredAt.AddDays(1), Dimensions(inbox: "i9"));
        var reply = Duration(ReportMetric.ResponseTime, Day.AddHours(10), TimeSpan.FromMinutes(59));
        var next = State(sequence: 3, assignee: "u1", labels: ["vip"]);
        Assert.AreEqual("applied:2:1", W(await competing.ApplyAsync(scope, new ReportProjection(next, [created, tampered, reply]), 1)));
        Assert.AreEqual("conflict:2", W(await primary.ApplyAsync(scope, new ReportProjection(next, [reply]), 1)));
        Assert.AreEqual("applied:3:0", W(await primary.ApplyAsync(scope, new ReportProjection(next, [reply, created]), 2)));
        Assert.AreEqual(next, (await primary.GetConversationAsync(scope, id))!.State);

        var aggregate = await primary.AggregateAsync(scope, Whole(Day, Day.AddDays(3)), AllowAllFilter.Instance);
        Assert.AreEqual(1, Total(aggregate, ReportMetric.ConversationCreated));
        Assert.AreEqual(1, Total(aggregate, ReportMetric.MessageReceived));
        Assert.AreEqual(1, Total(aggregate, ReportMetric.ResponseTime));
        Assert.AreEqual(TimeSpan.FromMinutes(59).Ticks / 10, aggregate.Rows.Single(row => row.Metric == ReportMetric.ResponseTime).Sum);
        var facts = await competing.ListFactsAsync(scope, new ReportFactQuery(Day, Day.AddDays(3)), AllowAllFilter.Instance);
        Assert.HasCount(3, facts.Items);
        Assert.AreEqual(received.OccurredAt, facts.Items.Single(fact => fact.SourceId == received.SourceId).OccurredAt, "the first fact content is kept");

        // Scope isolation.
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await competing.GetConversationAsync(other, id));
            Assert.IsEmpty((await competing.AggregateAsync(other, Whole(Day, Day.AddDays(3)), AllowAllFilter.Instance)).Rows);
            Assert.IsEmpty((await competing.ListFactsAsync(other, new ReportFactQuery(Day, Day.AddDays(3)), AllowAllFilter.Instance)).Items);
            Assert.AreEqual(0, await competing.CountBacklogAsync(other, ReportFilter.None, AllowAllFilter.Instance));
        }

        // Fenced delete keeps facts and rollups.
        Assert.AreEqual("conflict:3", W(await competing.DeleteConversationAsync(scope, id, 2)));
        Assert.AreEqual("applied:3:0", W(await competing.DeleteConversationAsync(scope, id, 3)));
        Assert.AreEqual("conflict:none", W(await primary.DeleteConversationAsync(scope, id, 3)));
        Assert.IsNull(await primary.GetConversationAsync(scope, id));
        Assert.AreEqual(1, Total(await primary.AggregateAsync(scope, Whole(Day, Day.AddDays(3)), AllowAllFilter.Instance), ReportMetric.ConversationCreated));
    }

    public static async Task VerifyAggregatesAsync(IReportingStore store)
    {
        var scope = NewScope();
        var facts = new List<ReportFact>
        {
            Count(ReportMetric.ConversationCreated, Day.AddHours(1), dimensions: Dimensions(labels: ["vip", "billing"])),
            Count(ReportMetric.ConversationCreated, Day.AddHours(1).AddMinutes(14), dimensions: Dimensions(inbox: "i1", team: null, agent: null, channel: null, priority: ConversationPriority.Low)),
            Count(ReportMetric.ConversationCreated, Day.AddHours(25), dimensions: Dimensions(team: "t1", agent: "u1", labels: ["vip"])),
            Duration(ReportMetric.FirstResponseTime, Day.AddHours(2), TimeSpan.FromSeconds(30)),
            Duration(ReportMetric.FirstResponseTime, Day.AddHours(2), TimeSpan.FromSeconds(30)),
            Duration(ReportMetric.FirstResponseTime, Day.AddHours(26), TimeSpan.FromHours(3), dimensions: Dimensions(agent: "u1")),
        };
        Assert.AreEqual("applied:1:6", W(await store.ApplyAsync(scope, new ReportProjection(State(), facts), null)));
        var boundaries = new[] { Day, Day.AddDays(1), Day.AddDays(2) };

        var daily = await store.AggregateAsync(scope, new ReportAggregateQuery(boundaries, [ReportMetric.ConversationCreated, ReportMetric.FirstResponseTime]), AllowAllFilter.Instance);
        CollectionAssert.AreEqual(new[] { "0:ConversationCreated:2:2", "0:FirstResponseTime:2:60000000", "1:ConversationCreated:1:1", "1:FirstResponseTime:1:10800000000" },
            daily.Rows.Select(row => $"{row.Bucket}:{row.Metric}:{row.Count}:{row.Sum}").ToArray());
        Assert.IsFalse(daily.Truncated);
        var first = daily.Rows.Single(row => row.Bucket == 0 && row.Metric == ReportMetric.FirstResponseTime);
        Assert.AreEqual((30_000_000L, 30_000_000L), (first.Minimum, first.Maximum));

        // Grouping keys: identifiers verbatim (nulls first), priority as its number, labels as label rows.
        string Groups(ReportAggregateResult result) => string.Join(",", result.Rows.Select(row => $"{row.Group ?? "-"}={row.Count}"));
        Assert.AreEqual("i0=2,i1=1", Groups(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), ReportDimension.Inbox, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance)));
        Assert.AreEqual("-=1,t0=1,t1=1", Groups(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), ReportDimension.Team, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance)));
        Assert.AreEqual("-=1,u0=1,u1=1", Groups(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), ReportDimension.Agent, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance)));
        Assert.AreEqual("-=1,email=2", Groups(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), ReportDimension.Channel, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance)));
        Assert.AreEqual("2=1,4=2", Groups(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), ReportDimension.Priority, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance)));
        Assert.AreEqual("billing=1,vip=2", Groups(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), ReportDimension.Label, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance)));

        // Dimension restrictions.
        Assert.AreEqual(2, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), filter: new ReportFilter(label: new LabelId("vip")), metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance), ReportMetric.ConversationCreated));
        Assert.AreEqual(1, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), filter: new ReportFilter(team: new TeamId("t1")), metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance), ReportMetric.ConversationCreated));
        Assert.AreEqual(1, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), filter: new ReportFilter(priority: ConversationPriority.Low, inbox: new InboxId("i1")), metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance), ReportMetric.ConversationCreated));
        Assert.AreEqual(2, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), filter: new ReportFilter(channel: "email", agent: new UserId("u0")), histograms: true), AllowAllFilter.Instance), ReportMetric.FirstResponseTime));

        // Histograms: duration rows split by bin, count rows keep bin 0.
        var histogram = await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), histograms: true, metrics: [ReportMetric.ConversationCreated, ReportMetric.FirstResponseTime]), AllowAllFilter.Instance);
        CollectionAssert.AreEqual(new[] { $"ConversationCreated:0:3", $"FirstResponseTime:{ReportHistogram.Bin(30_000_000)}:2", $"FirstResponseTime:{ReportHistogram.Bin(10_800_000_000)}:1" },
            histogram.Rows.Select(row => $"{row.Metric}:{row.Bin}:{row.Count}").ToArray());

        // Cells outside the boundaries are excluded; a late fact lands in its own (past) bucket.
        Assert.AreEqual(1, Total(await store.AggregateAsync(scope, Whole(Day.AddHours(1).AddMinutes(15), Day.AddDays(2), metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance), ReportMetric.ConversationCreated),
            "both 01:00-cell facts precede the range");
        var late = Count(ReportMetric.ConversationResolved, Day.AddMinutes(5));
        Assert.AreEqual("applied:2:1", W(await store.ApplyAsync(scope, new ReportProjection(State(sequence: 9), [late]), 1)));
        var withLate = await store.AggregateAsync(scope, new ReportAggregateQuery(boundaries, [ReportMetric.ConversationResolved]), AllowAllFilter.Instance);
        Assert.AreEqual("0:1", string.Join(",", withLate.Rows.Select(row => $"{row.Bucket}:{row.Count}")));

        // Access filters apply per rollup row (inbox, team, agent as assignee); labels are unknown on rollups.
        Assert.AreEqual(1, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), metrics: ReportMetric.ConversationCreated), new InboxInFilter([new InboxId("i1")])), ReportMetric.ConversationCreated));
        Assert.AreEqual(1, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), metrics: ReportMetric.ConversationCreated), new AssigneeIsFilter(new UserId("u1"))), ReportMetric.ConversationCreated));
        Assert.AreEqual(0, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), metrics: ReportMetric.ConversationCreated), new HasLabelFilter(new LabelId("vip"))), ReportMetric.ConversationCreated));
        Assert.AreEqual(2, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), metrics: ReportMetric.ConversationCreated),
            new OrFilter(new TeamInFilter([new TeamId("t0"), new TeamId("t1")]), new HasLabelFilter(new LabelId("vip")))), ReportMetric.ConversationCreated), "true OR unknown is true");
        Assert.AreEqual(0, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), metrics: ReportMetric.ConversationCreated), new ScopeFilter(TestScopes.LevelVariants(scope)[1])), ReportMetric.ConversationCreated));
        Assert.IsEmpty((await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2)), DenyAllFilter.Instance)).Rows);
    }

    public static async Task VerifyFactsAndBacklogAsync(IReportingStore store)
    {
        var scope = NewScope();
        var at = Day.AddHours(5);
        var sources = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToList();
        var facts = sources.Select((source, index) => Count(index % 2 == 0 ? ReportMetric.MessageReceived : ReportMetric.MessageSent, index < 4 ? at : at.AddMinutes(index), source: source,
            dimensions: Dimensions(agent: index < 5 ? "u0" : "u1", labels: index == 6 ? ["vip"] : []))).ToList();
        facts.Add(Count(ReportMetric.MessageSent, at, source: sources[0]));   // same source, other metric
        Assert.AreEqual("applied:1:8", W(await store.ApplyAsync(scope, new ReportProjection(State(), facts), null)));
        var expected = facts.ToList();
        expected.Sort(ReportFactQuery.Compare);
        var listed = new List<ReportFact>();
        ReportFactCursor? after = null;
        do
        {
            var page = await store.ListFactsAsync(scope, new ReportFactQuery(Day, Day.AddDays(1), limit: 3, after: after), AllowAllFilter.Instance);
            Assert.IsLessThanOrEqualTo(3, page.Items.Count);
            listed.AddRange(page.Items);
            after = page.Next;
        }
        while (after is not null);
        CollectionAssert.AreEqual(expected.Select(fact => (fact.SourceId, fact.Metric)).ToArray(), listed.Select(fact => (fact.SourceId, fact.Metric)).ToArray());
        Assert.AreEqual(expected[0].Dimensions, listed[0].Dimensions);
        Assert.HasCount(2, (await store.ListFactsAsync(scope, new ReportFactQuery(Day, Day.AddDays(1), new ReportFilter(agent: new UserId("u1"))), AllowAllFilter.Instance)).Items);
        Assert.HasCount(1, (await store.ListFactsAsync(scope, new ReportFactQuery(Day, Day.AddDays(1)), new HasLabelFilter(new LabelId("vip")))).Items, "fact labels are known");
        Assert.HasCount(1, (await store.ListFactsAsync(scope, new ReportFactQuery(Day, Day.AddDays(1), new ReportFilter(label: new LabelId("vip"))), AllowAllFilter.Instance)).Items);
        Assert.IsEmpty((await store.ListFactsAsync(scope, new ReportFactQuery(at.AddHours(1), Day.AddDays(1)), AllowAllFilter.Instance)).Items);

        // Backlog: open states matching the filter and the Access filter (labels known).
        await store.ApplyAsync(scope, new ReportProjection(State("c2", team: "t1", assignee: null, labels: ["vip"]), []), null);
        await store.ApplyAsync(scope, new ReportProjection(State("c3", status: ConversationStatusKind.Resolved), []), null);
        await store.ApplyAsync(scope, new ReportProjection(State("c4", status: ConversationStatusKind.Snoozed, inbox: "i1"), []), null);
        Assert.AreEqual(3, await store.CountBacklogAsync(scope, ReportFilter.None, AllowAllFilter.Instance));
        Assert.AreEqual(1, await store.CountBacklogAsync(scope, new ReportFilter(label: new LabelId("vip")), AllowAllFilter.Instance));
        Assert.AreEqual(2, await store.CountBacklogAsync(scope, new ReportFilter(agent: new UserId("u0")), AllowAllFilter.Instance));
        Assert.AreEqual(1, await store.CountBacklogAsync(scope, ReportFilter.None, new HasLabelFilter(new LabelId("vip"))));
        Assert.AreEqual(1, await store.CountBacklogAsync(scope, ReportFilter.None, new InboxInFilter([new InboxId("i1")])));
        Assert.AreEqual(0, await store.CountBacklogAsync(scope, ReportFilter.None, DenyAllFilter.Instance));
    }

    public static async Task VerifyRetentionAndRebuildAsync(IReportingStore store)
    {
        var scope = NewScope();
        var facts = new List<ReportFact>();
        for (var hour = 0; hour < 48; hour++)
        {
            facts.Add(Count(ReportMetric.ConversationCreated, Day.AddHours(hour), dimensions: Dimensions(labels: hour % 3 == 0 ? ["vip"] : [])));
            facts.Add(Duration(ReportMetric.ResponseTime, Day.AddHours(hour).AddMinutes(20), TimeSpan.FromMinutes(hour + 1)));
        }
        Assert.AreEqual("applied:1:96", W(await store.ApplyAsync(scope, new ReportProjection(State(), facts), null)));
        var query = Whole(Day, Day.AddDays(2), ReportDimension.Label, histograms: true);
        var plain = Whole(Day, Day.AddDays(2), histograms: true);
        var before = await store.AggregateAsync(scope, plain, AllowAllFilter.Instance);
        var beforeLabels = await store.AggregateAsync(scope, query, AllowAllFilter.Instance);

        Assert.AreEqual("rebuilt:96", B(await store.RebuildRollupsAsync(scope, Day, Day.AddDays(2))));
        CollectionAssert.AreEqual(before.Rows.ToArray(), (await store.AggregateAsync(scope, plain, AllowAllFilter.Instance)).Rows.ToArray());
        CollectionAssert.AreEqual(beforeLabels.Rows.ToArray(), (await store.AggregateAsync(scope, query, AllowAllFilter.Instance)).Rows.ToArray());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.RebuildRollupsAsync(scope, Day, Day.AddDays(40)));

        // Fact retention deletes facts only; rollup retention deletes cells only. Both are bounded and store-wide.
        var cutoff = Day.AddDays(1);
        Assert.AreEqual(10, await store.PruneFactsAsync(cutoff, 10));
        while (await store.PruneFactsAsync(cutoff, 1000) > 0)
        {
        }
        var remaining = await store.ListFactsAsync(scope, new ReportFactQuery(Day, Day.AddDays(2), limit: 1000), AllowAllFilter.Instance);
        Assert.HasCount(48, remaining.Items);
        Assert.IsTrue(remaining.Items.All(fact => fact.OccurredAt >= cutoff));
        CollectionAssert.AreEqual(before.Rows.ToArray(), (await store.AggregateAsync(scope, plain, AllowAllFilter.Instance)).Rows.ToArray(), "rollups outlive facts");
        while (await store.PruneRollupsAsync(cutoff, 1000) > 0)
        {
        }
        var after = await store.AggregateAsync(scope, plain, AllowAllFilter.Instance);
        Assert.AreEqual(24, Total(after, ReportMetric.ConversationCreated));
        Assert.AreEqual(24, Total(after, ReportMetric.ResponseTime));
    }

    /// <summary>Concurrent projections of many conversations through two clients share rollup cells; every fact is counted exactly once.</summary>
    public static async Task VerifyConcurrentProjectionsAsync(IReportingStore primary, IReportingStore competing)
    {
        var scope = NewScope();
        const int conversations = 24;
        var created = Enumerable.Range(0, conversations).Select(index => Count(ReportMetric.ConversationCreated, Day.AddHours(3), $"c{index}", Dimensions(labels: ["vip"]))).ToList();
        var replies = Enumerable.Range(0, conversations).Select(index => Duration(ReportMetric.ResponseTime, Day.AddHours(3).AddMinutes(1), TimeSpan.FromSeconds(index + 1), $"c{index}")).ToList();
        var results = await Task.WhenAll(Enumerable.Range(0, conversations * 2).Select(async index =>
        {
            var conversation = index % conversations;
            var store = index % 2 == 0 ? primary : competing;
            var projection = new ReportProjection(State($"c{conversation}"), [created[conversation], replies[conversation]]);
            // Both clients race to create the same state: exactly one wins, the other conflicts and inserts nothing.
            return W(await store.ApplyAsync(scope, projection, null));
        }));
        Assert.AreEqual(conversations, results.Count(result => string.Equals(result, "applied:1:2", StringComparison.Ordinal)));
        Assert.AreEqual(conversations, results.Count(result => string.Equals(result, "conflict:1", StringComparison.Ordinal)));
        var aggregate = await primary.AggregateAsync(scope, Whole(Day, Day.AddDays(1), histograms: true), AllowAllFilter.Instance);
        Assert.AreEqual(conversations, Total(aggregate, ReportMetric.ConversationCreated));
        Assert.AreEqual(conversations, Total(aggregate, ReportMetric.ResponseTime));
        Assert.AreEqual(Enumerable.Range(1, conversations).Sum() * 1_000_000L, aggregate.Rows.Where(row => row.Metric == ReportMetric.ResponseTime).Sum(row => row.Sum));
        Assert.AreEqual(conversations, Total(await competing.AggregateAsync(scope, Whole(Day, Day.AddDays(1), ReportDimension.Label), AllowAllFilter.Instance), ReportMetric.ConversationCreated));

        // Re-applying every projection concurrently on both clients (fenced on revision 1) counts nothing new.
        var replays = await Task.WhenAll(Enumerable.Range(0, conversations * 2).Select(async index =>
        {
            var conversation = index % conversations;
            var store = index % 2 == 0 ? primary : competing;
            return W(await store.ApplyAsync(scope, new ReportProjection(State($"c{conversation}", sequence: 2), [created[conversation], replies[conversation]]), 1));
        }));
        Assert.AreEqual(conversations, replays.Count(result => string.Equals(result, "applied:2:0", StringComparison.Ordinal)));
        Assert.AreEqual(conversations, Total(await primary.AggregateAsync(scope, Whole(Day, Day.AddDays(1)), AllowAllFilter.Instance), ReportMetric.ConversationCreated));
    }

    /// <summary>
    /// Fact prunes advance a monotonic, store-wide fact horizon; rebuilds of ranges starting before it are refused and change nothing,
    /// so rollups that outlive their facts are never wiped. Rebuilds at or after the horizon still work.
    /// </summary>
    public static async Task VerifyRebuildHorizonAsync(IReportingStore store)
    {
        var scope = NewScope();
        var facts = Enumerable.Range(0, 48).Select(hour => Count(ReportMetric.ConversationCreated, Day.AddHours(hour), dimensions: Dimensions(labels: hour % 2 == 0 ? ["vip"] : []))).ToList();
        Assert.AreEqual("applied:1:48", W(await store.ApplyAsync(scope, new ReportProjection(State(), facts), null)));
        var plain = Whole(Day, Day.AddDays(2), metrics: ReportMetric.ConversationCreated);
        var cutoff = Day.AddDays(1).AddMinutes(5);   // mid-cell: the 24:00 cell may have lost facts too
        var before = (await store.GetFactHorizonAsync()) ?? DateTimeOffset.MinValue;
        Assert.IsTrue(before <= cutoff, "other tests share the store; their horizons precede this cutoff");
        while (await store.PruneFactsAsync(cutoff, 1000) > 0)
        {
        }
        Assert.AreEqual(cutoff, await store.GetFactHorizonAsync());
        await store.PruneFactsAsync(Day, 10);
        Assert.AreEqual(cutoff, await store.GetFactHorizonAsync(), "the horizon never moves backwards");

        Assert.AreEqual($"refused:{cutoff:O}", B(await store.RebuildRollupsAsync(scope, Day, Day.AddDays(2))));
        Assert.AreEqual($"refused:{cutoff:O}", B(await store.RebuildRollupsAsync(scope, Day.AddDays(1), Day.AddDays(2))));
        Assert.AreEqual(48, Total(await store.AggregateAsync(scope, plain, AllowAllFilter.Instance), ReportMetric.ConversationCreated), "a refused rebuild changes nothing");
        Assert.AreEqual("rebuilt:23", B(await store.RebuildRollupsAsync(scope, Day.AddDays(1).AddMinutes(15), Day.AddDays(2))));
        Assert.AreEqual(48, Total(await store.AggregateAsync(scope, plain, AllowAllFilter.Instance), ReportMetric.ConversationCreated));
        // Label reports read facts, so they cover only what retention kept.
        Assert.AreEqual(11, Total(await store.AggregateAsync(scope, Whole(Day, Day.AddDays(2), ReportDimension.Label, metrics: ReportMetric.ConversationCreated), AllowAllFilter.Instance),
            ReportMetric.ConversationCreated));
    }

    /// <summary>
    /// Label groupings and filters (computed from facts) equal a reference computed from the facts with the rollup Access semantics
    /// (labels unknown), for every dimension, histogram bins, sums, minimums and maximums.
    /// </summary>
    public static async Task VerifyLabelAggregatesMatchFactsAsync(IReportingStore store, int seed)
    {
        var scope = NewScope();
        var random = new Random(seed);
        string[] labels = ["a", "b", "c", "d"];
        var facts = new List<ReportFact>();
        for (var index = 0; index < 300; index++)
        {
            var carried = labels.Where(_ => random.Next(3) == 0).ToArray();
            var dimensions = Dimensions($"i{random.Next(2)}", random.Next(3) == 0 ? null : $"t{random.Next(2)}", random.Next(3) == 0 ? null : $"u{random.Next(3)}",
                random.Next(2) == 0 ? "email" : null, (ConversationPriority)random.Next(1, 6), carried);
            var at = Day.AddMinutes(random.Next(3 * 24 * 60));
            facts.Add(random.Next(2) == 0
                ? Count(ReportMetric.MessageSent, at, $"bulk{index / 100}", dimensions)
                : Duration(ReportMetric.ResponseTime, at, TimeSpan.FromSeconds(random.Next(0, 20_000)), $"bulk{index / 100}", dimensions));
        }
        foreach (var chunk in facts.Chunk(100).Select((items, index) => (items, index)))
            Assert.StartsWith("applied:1:", W(await store.ApplyAsync(scope, new ReportProjection(State($"bulk{chunk.index}"), chunk.items), null)), StringComparison.Ordinal);
        var boundaries = new[] { Day, Day.AddDays(1), Day.AddDays(2), Day.AddDays(3) };
        AccessFilter[] filters = [AllowAllFilter.Instance, new TeamInFilter([new TeamId("t0")]), new OrFilter(new AssigneeIsFilter(new UserId("u1")), new HasLabelFilter(new LabelId("a")))];
        foreach (var filter in filters)
            foreach (var (groupBy, restriction) in new (ReportDimension, ReportFilter?)[]
                { (ReportDimension.Label, null), (ReportDimension.Agent, new ReportFilter(label: new LabelId("b"))), (ReportDimension.Label, new ReportFilter(channel: "email", label: new LabelId("c"))) })
                foreach (var histograms in new[] { false, true })
                {
                    var query = new ReportAggregateQuery(boundaries, [ReportMetric.MessageSent, ReportMetric.ResponseTime], restriction, groupBy, histograms);
                    var expected = facts
                        .Where(fact => new ReportFilter(query.Filter.Inbox, query.Filter.Team, query.Filter.Agent, query.Filter.Channel, query.Filter.Priority).Matches(fact.Dimensions)
                            && AccessFilterEvaluator.Matches(filter, ReportResources.Rollup(scope, fact.Dimensions.Inbox, fact.Dimensions.Team, fact.Dimensions.Agent)))
                        .SelectMany(fact => fact.Dimensions.Labels.Where(label => query.Filter.Label is not { } only || label == only).Select(label => (fact, label)))
                        .GroupBy(item => (Bucket: ReportBuckets.IndexOf(boundaries, item.fact.CellStart),
                            Group: ReportGroups.Key(groupBy, item.fact.Dimensions.Inbox, item.fact.Dimensions.Team, item.fact.Dimensions.Agent, item.fact.Dimensions.Channel,
                                item.fact.Dimensions.Priority, item.label),
                            item.fact.Metric, Bin: histograms && ReportMetrics.IsDuration(item.fact.Metric) ? item.fact.Bin : 0))
                        .OrderBy(group => group.Key.Bucket).ThenBy(group => group.Key.Group is not null).ThenBy(group => group.Key.Group, StringComparer.Ordinal)
                        .ThenBy(group => group.Key.Metric).ThenBy(group => group.Key.Bin)
                        .Select(group => new ReportAggregateRow(group.Key.Bucket, group.Key.Group, group.Key.Metric, group.Key.Bin, group.Count(), group.Sum(item => item.fact.Value),
                            group.Min(item => item.fact.Value), group.Max(item => item.fact.Value)))
                        .ToArray();
                    var actual = await store.AggregateAsync(scope, query, filter);
                    Assert.IsNotEmpty(expected);
                    CollectionAssert.AreEqual(expected, actual.Rows.ToArray(), $"seed {seed} {groupBy} histograms={histograms}");
                }
    }
}
