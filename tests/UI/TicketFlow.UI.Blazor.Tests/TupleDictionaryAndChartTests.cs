using TicketFlow.Reporting;
using TicketFlow.UI.Blazor.Infrastructure;

namespace TicketFlow.UI.Blazor.Tests;

[TestClass]
public sealed class TupleDictionaryAndChartTests
{
    [TestMethod]
    public void TwoKeyTupleDictionaryExtensionIndexerAndHelpersWork()
    {
        var map = new Dictionary<(string Scope, int Id), string>();
        Assert.IsNull(map.GetValueOrDefault("a", 1));
        Assert.IsFalse(map.ContainsKey("a", 1));

        map["a", 1] = "first";
        map.Add("a", 2, "second");
        Assert.AreEqual("first", map["a", 1]);
        Assert.IsTrue(map.ContainsKey("a", 1));
        Assert.IsTrue(map.TryGetValue("a", 2, out var second));
        Assert.AreEqual("second", second);

        Assert.IsTrue(map.Remove("a", 1, out var removed));
        Assert.AreEqual("first", removed);
        Assert.IsTrue(map.Remove("a", 2));
        Assert.AreEqual(0, map.Count);
    }

    [TestMethod]
    public void ThreeKeyTupleDictionaryExtensionIndexerAndHelpersWork()
    {
        var map = new Dictionary<(string Scope, string Inbox, int Id), string>();
        Assert.IsNull(map.GetValueOrDefault("s", "in", 7));

        map["s", "in", 7] = "seven";
        map.Add("s", "in", 8, "eight");
        Assert.AreEqual("seven", map["s", "in", 7]);
        Assert.IsTrue(map.ContainsKey("s", "in", 8));
        Assert.IsTrue(map.TryGetValue("s", "in", 8, out var eight));
        Assert.AreEqual("eight", eight);

        Assert.IsTrue(map.Remove("s", "in", 7, out var removed));
        Assert.AreEqual("seven", removed);
        Assert.IsTrue(map.Remove("s", "in", 8));
        Assert.AreEqual(0, map.Count);
    }

    [TestMethod]
    public void CounterTupleDictionaryDefaultsMissingKeysToZero()
    {
        var counters = new Dictionary<(string Group, int Bucket), long>();
        Assert.AreEqual(0L, counters["a", 0]);
        Assert.AreEqual(0, counters.Count, "reading a missing counter must not insert an entry");

        counters["a", 0] += 5;
        counters["a", 0]++;
        counters["a", 1] += 2;

        Assert.AreEqual(6L, counters["a", 0]);
        Assert.AreEqual(2L, counters["a", 1]);
        Assert.AreEqual(0L, counters["b", 0]);
    }

    [TestMethod]
    public void UiChartsDailyAccumulatesPointsAndZeroFillsMissingBuckets()
    {
        var start = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset[] buckets = [start, start.AddDays(1), start.AddDays(2), start.AddDays(3)];
        var trend = new ReportTrend(start, buckets[^1], "UTC", ReportGranularity.Day, buckets,
        [
            new ReportTrendPoint(buckets[0], "a", ReportMetric.ConversationCreated, 3, 3, null, null),
            new ReportTrendPoint(buckets[0], "b", ReportMetric.ConversationCreated, 4, 4, null, null),
            new ReportTrendPoint(buckets[2], null, ReportMetric.ConversationResolved, 5, 5, null, null),
        ], false);

        var (labels, series) = UiCharts.Daily(trend,
        [
            (ReportMetric.ConversationCreated, "Created"),
            (ReportMetric.ConversationResolved, "Resolved"),
        ]);

        Assert.AreEqual(3, labels.Count);
        Assert.AreEqual(2, series.Count);
        CollectionAssert.AreEqual(new double[] { 7, 0, 0 }, series[0].Values.ToArray());
        CollectionAssert.AreEqual(new double[] { 0, 0, 5 }, series[1].Values.ToArray());
    }
}
