using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Reporting.StoreContracts;
using TicketFlow.Testing;

namespace TicketFlow.Reporting.Tests;

[TestClass]
public sealed class ModelTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [TestMethod]
    public void HistogramBinsAreLogLinearAndContiguous()
    {
        Assert.AreEqual(0, ReportHistogram.Bin(0));
        Assert.AreEqual(0, ReportHistogram.Bin(999_999));
        Assert.AreEqual(1, ReportHistogram.Bin(1_000_000));
        Assert.AreEqual(2, ReportHistogram.Bin(1_125_000));
        Assert.AreEqual(9, ReportHistogram.Bin(2_000_000));
        Assert.AreEqual(ReportHistogram.BinCount - 1, ReportHistogram.Bin(ReportFact.MaximumDuration.Ticks / 10 * 8));
        for (var bin = 0; bin < ReportHistogram.BinCount - 1; bin++)
        {
            Assert.AreEqual(ReportHistogram.Upper(bin), ReportHistogram.Lower(bin + 1), $"bin {bin}");
            Assert.AreEqual(bin, ReportHistogram.Bin(ReportHistogram.Lower(bin)));
            Assert.AreEqual(bin, ReportHistogram.Bin(ReportHistogram.Upper(bin) - 1));
            if (bin > 0)
                Assert.IsLessThanOrEqualTo(ReportHistogram.Lower(bin) / 8 + 1, ReportHistogram.Upper(bin) - ReportHistogram.Lower(bin), "width ≤ lower / 8");
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void PercentileEstimatesStayWithinHalfABinOfTheExactNearestRank(int seed)
    {
        var random = new Random(seed);
        var values = Enumerable.Range(0, 1 + random.Next(2000)).Select(_ => (long)(Math.Exp(random.NextDouble() * 18) * 1000)).ToList();
        var bins = values.GroupBy(ReportHistogram.Bin).Select(group => new ReportHistogramBin(group.Key, group.Count(), group.Min(), group.Max())).ToList();
        foreach (var fraction in new[] { 0.01, 0.25, 0.5, 0.9, 0.99, 1.0 })
        {
            var exact = ReportHistogram.ExactPercentile(values, fraction)!.Value;
            var estimate = ReportHistogram.Percentile(bins, fraction)!.Value;
            Assert.AreEqual(ReportHistogram.Bin(exact), ReportHistogram.Bin(estimate), $"seed {seed} p{fraction}: same bin");
            var tolerance = exact < 1_000_000 ? 500_000 : exact / 16.0;
            Assert.IsLessThanOrEqualTo(tolerance, Math.Abs(estimate - exact), $"seed {seed} p{fraction}: {estimate} vs {exact}");
            // Deterministic: bins in any order or split across cells give the same estimate.
            var split = bins.SelectMany(bin => bin.Count > 1
                ? new[] { bin with { Count = bin.Count / 2 }, bin with { Count = bin.Count - bin.Count / 2 } }
                : new[] { bin }).Reverse().ToList();
            Assert.AreEqual(estimate, ReportHistogram.Percentile(split, fraction));
        }
    }

    [TestMethod]
    public void PercentilesAreExactForSingleValuedBinsAndNearestRank()
    {
        long[] values = [30_000_000, 30_000_000, 60_000_000, 3_600_000_000];
        var bins = values.GroupBy(ReportHistogram.Bin).Select(group => new ReportHistogramBin(group.Key, group.Count(), group.Min(), group.Max())).ToList();
        Assert.AreEqual(30_000_000, ReportHistogram.Percentile(bins, 0.5));
        Assert.AreEqual(3_600_000_000, ReportHistogram.Percentile(bins, 0.9));
        Assert.AreEqual(60_000_000, ReportHistogram.Percentile(bins, 0.75));
        Assert.AreEqual(30_000_000, ReportHistogram.ExactPercentile(values, 0.5));
        Assert.IsNull(ReportHistogram.Percentile([], 0.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ReportHistogram.Percentile(bins, 0));
    }

    [TestMethod]
    public void DayBucketsFollowNewYorkDaylightSavingTransitions()
    {
        // 2026-03-08 spring forward (23-hour day), 2026-11-01 fall back (25-hour day).
        var spring = ReportBuckets.Boundaries(NewYork, new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero), ReportGranularity.Day);
        CollectionAssert.AreEqual(new[]
        {
            new DateTimeOffset(2026, 3, 7, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 10, 4, 0, 0, TimeSpan.Zero),
        }, spring.ToArray());
        Assert.AreEqual(TimeSpan.FromHours(23), spring[2] - spring[1]);
        var fall = ReportBuckets.Boundaries(NewYork, new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), ReportGranularity.Day);
        Assert.AreEqual(TimeSpan.FromHours(25), fall[1] - fall[0]);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), fall[0]);
    }

    [TestMethod]
    public void HourBucketsSkipTheSpringGapAndRepeatTheFallBackHour()
    {
        var spring = ReportBuckets.Boundaries(NewYork, new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.Zero), ReportGranularity.Hour);
        // Local 00:00, 01:00, 03:00 (02:00 skipped), 04:00, 05:00 — consecutive UTC hours.
        Assert.HasCount(5, spring);
        Assert.IsTrue(spring.Zip(spring.Skip(1)).All(pair => pair.Second - pair.First == TimeSpan.FromHours(1)));
        Assert.AreEqual(3, TimeZoneInfo.ConvertTime(spring[2], NewYork).Hour);
        var fall = ReportBuckets.Boundaries(NewYork, new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero), ReportGranularity.Hour);
        var localHours = fall.Take(fall.Count - 1).Select(boundary => TimeZoneInfo.ConvertTime(boundary, NewYork).Hour).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 1, 1, 2 }, localHours, "01:00 occurs twice");
    }

    [TestMethod]
    public void BucketsAreExactInHalfAndQuarterHourZonesAndHandleMidnightGaps()
    {
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var day = ReportBuckets.Boundaries(kolkata, new DateTimeOffset(2026, 3, 2, 20, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 2, 21, 0, 0, TimeSpan.Zero), ReportGranularity.Day);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 18, 30, 0, TimeSpan.Zero), day[0]);
        Assert.IsTrue(day.All(ReportBuckets.IsCellAligned));
        var kathmandu = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu");
        var hours = ReportBuckets.Boundaries(kathmandu, new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 2, 2, 0, 0, TimeSpan.Zero), ReportGranularity.Hour);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 23, 15, 0, TimeSpan.Zero), hours[0]);
        Assert.IsTrue(hours.All(ReportBuckets.IsCellAligned));
        // Chile springs forward at local midnight: 2026-09-06 00:00 does not exist, the day starts at 01:00 (-03:00).
        var santiago = TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");
        var start = ReportBuckets.LocalStart(santiago, new DateTime(2026, 9, 6));
        Assert.AreEqual(new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero), start);
    }

    [TestMethod]
    public void WeekAndMonthBucketsStartOnLocalMondaysAndFirsts()
    {
        var weeks = ReportBuckets.Boundaries(NewYork, new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero), ReportGranularity.Week);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 5, 0, 0, TimeSpan.Zero), weeks[0]);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), weeks[1], "the week containing the DST switch is 167 hours");
        var months = ReportBuckets.Boundaries(NewYork, new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), ReportGranularity.Month);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, months.Select(boundary => TimeZoneInfo.ConvertTime(boundary, NewYork)).Select(local => local.Month).ToArray());
        Assert.IsTrue(months.All(boundary => TimeZoneInfo.ConvertTime(boundary, NewYork) is { Day: 1, Hour: 0 }));
        Assert.ThrowsExactly<ArgumentException>(() => ReportBuckets.Boundaries(NewYork, months[0], months[0].AddDays(60), ReportGranularity.Hour));
        Assert.AreEqual(1, ReportBuckets.IndexOf(weeks, weeks[1]));
        Assert.AreEqual(-1, ReportBuckets.IndexOf(weeks, weeks[^1]));
    }

    [TestMethod]
    public void FactsValidateValuesAndDeriveCells()
    {
        var at = new DateTimeOffset(2026, 3, 2, 10, 29, 59, 123, TimeSpan.Zero).AddTicks(4567);
        var fact = ReportingStoreContract.Duration(ReportMetric.ResponseTime, at, TimeSpan.FromMinutes(5));
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 10, 15, 0, TimeSpan.Zero), fact.CellStart);
        Assert.AreEqual(0, fact.OccurredAt.UtcTicks % 10, "microsecond precision");
        Assert.AreEqual(TimeSpan.FromMinutes(5), fact.Duration);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReportFact(Guid.NewGuid(), ReportMetric.MessageSent, new ConversationId("c"), at, ReportingStoreContract.Dimensions(), 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ReportFact(Guid.NewGuid(), ReportMetric.ResponseTime, new ConversationId("c"), at, ReportingStoreContract.Dimensions(), -1));
        Assert.ThrowsExactly<ArgumentException>(() => ReportingStoreContract.Dimensions(channel: "Email Channel"));
        var scope = TestScopes.Create();
        Assert.AreEqual(ReportFactIds.Created(scope, new ConversationId("c1")), ReportFactIds.Created(scope, new ConversationId("c1")));
        Assert.AreNotEqual(ReportFactIds.Created(scope, new ConversationId("c1")), ReportFactIds.Created(TestScopes.LevelVariants(scope)[5], new ConversationId("c1")));
        Assert.ThrowsExactly<ArgumentException>(() => new ReportQuery(at, at.AddDays(367)));
        Assert.ThrowsExactly<ArgumentException>(() => new ReportTrendQuery(new ReportQuery(at, at.AddDays(41)), [ReportMetric.MessageSent], ReportGranularity.Hour));
        Assert.ThrowsExactly<ArgumentException>(() => new ReportAggregateQuery([at, at.AddHours(1)], [ReportMetric.MessageSent]), "unaligned boundaries");
        Assert.AreEqual("first-response-business-time", ReportMetricNames.Name(ReportMetric.FirstResponseBusinessTime));
    }

    [TestMethod]
    public void CsvFieldsAreQuotedAndFormulasNeutralized()
    {
        string Encode(string value)
        {
            var builder = new System.Text.StringBuilder();
            Csv.Append(builder, value);
            return builder.ToString();
        }

        Assert.AreEqual("plain", Encode("plain"));
        Assert.AreEqual("\"a,b\"", Encode("a,b"));
        Assert.AreEqual("\"say \"\"hi\"\"\"", Encode("say \"hi\""));
        Assert.AreEqual("'=HYPERLINK(1)", Encode("=HYPERLINK(1)"));
        Assert.AreEqual("\"'=1,2\"", Encode("=1,2"));
        Assert.AreEqual("'@x", Encode("@x"));
    }
}
