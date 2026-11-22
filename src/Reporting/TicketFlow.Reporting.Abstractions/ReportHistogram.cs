namespace TicketFlow.Reporting;

/// <summary>One histogram bin of a duration metric: its fact count and the smallest and largest value (microseconds) seen in it.</summary>
public readonly record struct ReportHistogramBin(int Bin, long Count, long Minimum, long Maximum);

/// <summary>
/// Deterministic log-linear histogram for duration percentiles. Bin 0 holds values under one second; every power-of-two range of
/// seconds [2^k, 2^(k+1)) is split into 8 equal sub-bins (k = 0…29), so a bin's width is at most 1/8 of its lower bound. Rollup cells
/// keep per-bin counts with the bin's minimum and maximum, so bins compose exactly across cells, dimensions and buckets.
/// <para>
/// Percentiles use the nearest-rank definition: the value at rank ⌈p·n⌉ of the sorted values. The estimate is the midpoint of the bin
/// holding that rank, clamped into the bin's observed [minimum, maximum]. It is exact when the bin holds one distinct value and
/// otherwise within half a bin width of the exact value: at most 6.25 % relative error from one second up, at most half a second below.
/// </para>
/// </summary>
public static class ReportHistogram
{
    public const int SubBins = 8;
    public const int MaximumExponent = 29;

    /// <summary>Number of bins (0 … <see cref="BinCount"/> − 1).</summary>
    public const int BinCount = 1 + (MaximumExponent + 1) * SubBins;

    private const long Second = 1_000_000;

    /// <summary>The bin of a non-negative duration in microseconds.</summary>
    public static int Bin(long microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        if (microseconds < Second)
            return 0;
        var exponent = Math.Min(System.Numerics.BitOperations.Log2((ulong)(microseconds / Second)), MaximumExponent);
        var lower = Second << exponent;
        var sub = (int)Math.Min((microseconds - lower) * SubBins / lower, SubBins - 1);
        return 1 + exponent * SubBins + sub;
    }

    /// <summary>The inclusive lower bound of <paramref name="bin"/> in microseconds.</summary>
    public static long Lower(int bin)
    {
        Require(bin);
        if (bin == 0)
            return 0;
        var exponent = (bin - 1) / SubBins;
        var lower = Second << exponent;
        return lower + lower * ((bin - 1) % SubBins) / SubBins;
    }

    /// <summary>The exclusive upper bound of <paramref name="bin"/> in microseconds (the last bin is open-ended; its upper bound is nominal).</summary>
    public static long Upper(int bin)
    {
        Require(bin);
        if (bin == 0)
            return Second;
        var exponent = (bin - 1) / SubBins;
        var lower = Second << exponent;
        return lower + lower * ((bin - 1) % SubBins + 1) / SubBins;
    }

    /// <summary>
    /// The nearest-rank percentile estimate (<paramref name="fraction"/> in (0, 1]) over <paramref name="bins"/> (any order; equal
    /// bins are merged), or <see langword="null"/> when they hold no values.
    /// </summary>
    public static long? Percentile(IEnumerable<ReportHistogramBin> bins, double fraction)
    {
        ArgumentNullException.ThrowIfNull(bins);
        if (!(fraction > 0 && fraction <= 1))
            throw new ArgumentOutOfRangeException(nameof(fraction));
        var merged = Merge(bins);
        var total = merged.Sum(bin => bin.Count);
        if (total == 0)
            return null;
        var rank = Math.Max(1, (long)Math.Ceiling(fraction * total));
        long cumulative = 0;
        foreach (var bin in merged)
        {
            cumulative += bin.Count;
            if (cumulative >= rank)
                return Math.Clamp((Lower(bin.Bin) + Upper(bin.Bin)) / 2, bin.Minimum, bin.Maximum);
        }
        return merged[^1].Maximum;
    }

    /// <summary>The exact nearest-rank percentile of <paramref name="values"/>, or <see langword="null"/> when empty (reference for tests).</summary>
    public static long? ExactPercentile(IEnumerable<long> values, double fraction)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!(fraction > 0 && fraction <= 1))
            throw new ArgumentOutOfRangeException(nameof(fraction));
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return null;
        return sorted[Math.Max(1, (long)Math.Ceiling(fraction * sorted.Length)) - 1];
    }

    /// <summary>Bins merged by index (counts summed, minimum and maximum combined) in ascending bin order, skipping empty bins.</summary>
    public static IReadOnlyList<ReportHistogramBin> Merge(IEnumerable<ReportHistogramBin> bins)
    {
        ArgumentNullException.ThrowIfNull(bins);
        var merged = new SortedDictionary<int, ReportHistogramBin>();
        foreach (var bin in bins)
        {
            Require(bin.Bin);
            if (bin.Count <= 0)
                continue;
            merged[bin.Bin] = merged.TryGetValue(bin.Bin, out var current)
                ? new(bin.Bin, current.Count + bin.Count, Math.Min(current.Minimum, bin.Minimum), Math.Max(current.Maximum, bin.Maximum))
                : bin;
        }
        return [.. merged.Values];
    }

    private static void Require(int bin)
    {
        if (bin is < 0 or >= BinCount)
            throw new ArgumentOutOfRangeException(nameof(bin));
    }
}
