using System.Globalization;
using TicketFlow.Reporting;
using TicketFlow.UI.Blazor.Components;

namespace TicketFlow.UI.Blazor.Infrastructure;

internal static class UiCharts
{
    /// <summary>Dense per-bucket series (missing buckets are zero) from a sparse trend.</summary>
    public static (IReadOnlyList<string> Labels, IReadOnlyList<AreaChart.ChartSeries> Series) Daily(ReportTrend trend, IReadOnlyList<(ReportMetric Metric, string Name)> metrics)
    {
        var bucketCount = trend.Buckets.Count > 1 ? trend.Buckets.Count - 1 : trend.Buckets.Count;
        var format = trend.Granularity == ReportGranularity.Hour ? "HH:mm" : "MMM d";
        var labels = new string[bucketCount];
        for (var i = 0; i < bucketCount; i++)
            labels[i] = trend.Buckets[i].ToString(format, CultureInfo.InvariantCulture);

        var totals = new Dictionary<(ReportMetric Metric, DateTimeOffset Bucket), long>(trend.Points.Count);
        for (var i = 0; i < trend.Points.Count; i++)
        {
            var point = trend.Points[i];
            var key = (point.Metric, point.BucketStart);
            totals[key] = totals.GetValueOrDefault(key) + point.Count;
        }

        var series = new AreaChart.ChartSeries[metrics.Count];
        for (var m = 0; m < metrics.Count; m++)
        {
            var (metric, name) = metrics[m];
            var values = new double[bucketCount];
            for (var b = 0; b < bucketCount; b++)
                values[b] = totals.GetValueOrDefault((metric, trend.Buckets[b]));
            series[m] = new AreaChart.ChartSeries(name, values);
        }
        return (labels, series);
    }
}
