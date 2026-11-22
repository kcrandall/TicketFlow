using System.Globalization;
using TicketFlow.Reporting;
using TicketFlow.UI.Blazor.Components;

namespace TicketFlow.UI.Blazor.Infrastructure;

internal static class UiCharts
{
    /// <summary>Dense per-bucket series (missing buckets are zero) from a sparse trend.</summary>
    public static (IReadOnlyList<string> Labels, IReadOnlyList<AreaChart.ChartSeries> Series) Daily(ReportTrend trend, IReadOnlyList<(ReportMetric Metric, string Name)> metrics)
    {
        var buckets = trend.Buckets.Take(Math.Max(0, trend.Buckets.Count - 1)).ToList();
        if (buckets.Count == 0)
            buckets = [.. trend.Buckets];
        var labels = buckets.Select(bucket => bucket.ToString(trend.Granularity == ReportGranularity.Hour ? "HH:mm" : "MMM d", CultureInfo.InvariantCulture)).ToList();
        var series = metrics.Select(metric => new AreaChart.ChartSeries(metric.Name, [.. buckets.Select(bucket =>
            (double)trend.Points.Where(point => point.Metric == metric.Metric && point.BucketStart == bucket).Sum(point => point.Count))])).ToList();
        return (labels, series);
    }
}
