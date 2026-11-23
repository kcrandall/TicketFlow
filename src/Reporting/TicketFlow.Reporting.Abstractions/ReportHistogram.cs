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
        Span<long> counts = stackalloc long[BinCount];
        Span<long> mins = stackalloc long[BinCount];
        Span<long> maxs = stackalloc long[BinCount];
        var total = Accumulate(bins, counts, mins, maxs, out var minBin, out var maxBin, out _);
        if (total == 0)
            return null;
        return EvaluatePercentile(counts, mins, maxs, minBin, maxBin, total, fraction);
    }

    /// <summary>
    /// Two nearest-rank percentile estimates (<paramref name="first"/> and <paramref name="second"/> in (0, 1]) computed in a single
    /// merge pass over <paramref name="bins"/>, or <c>(null, null)</c> when they hold no values.
    /// </summary>
    public static (long? First, long? Second) Percentiles(IEnumerable<ReportHistogramBin> bins, double first, double second)
    {
        ArgumentNullException.ThrowIfNull(bins);
        if (!(first > 0 && first <= 1))
            throw new ArgumentOutOfRangeException(nameof(first));
        if (!(second > 0 && second <= 1))
            throw new ArgumentOutOfRangeException(nameof(second));
        Span<long> counts = stackalloc long[BinCount];
        Span<long> mins = stackalloc long[BinCount];
        Span<long> maxs = stackalloc long[BinCount];
        var total = Accumulate(bins, counts, mins, maxs, out var minBin, out var maxBin, out _);
        if (total == 0)
            return (null, null);
        return (
            EvaluatePercentile(counts, mins, maxs, minBin, maxBin, total, first),
            EvaluatePercentile(counts, mins, maxs, minBin, maxBin, total, second));
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
        Span<long> counts = stackalloc long[BinCount];
        Span<long> mins = stackalloc long[BinCount];
        Span<long> maxs = stackalloc long[BinCount];
        Accumulate(bins, counts, mins, maxs, out var minBin, out var maxBin, out var nonEmpty);
        if (nonEmpty == 0)
            return [];
        var result = new ReportHistogramBin[nonEmpty];
        var index = 0;
        for (var i = minBin; i <= maxBin; i++)
            if (counts[i] > 0)
                result[index++] = new ReportHistogramBin(i, counts[i], mins[i], maxs[i]);
        return result;
    }

    private static long Accumulate(IEnumerable<ReportHistogramBin> bins, Span<long> counts, Span<long> mins, Span<long> maxs,
        out int minBin, out int maxBin, out int nonEmpty)
    {
        minBin = BinCount;
        maxBin = -1;
        nonEmpty = 0;
        long total = 0;
        if (bins is IList<ReportHistogramBin> list)
        {
            for (var i = 0; i < list.Count; i++)
                total += AccumulateBin(list[i], counts, mins, maxs, ref minBin, ref maxBin, ref nonEmpty);
        }
        else
            foreach (var bin in bins)
                total += AccumulateBin(bin, counts, mins, maxs, ref minBin, ref maxBin, ref nonEmpty);
        return total;
    }

    private static long AccumulateBin(ReportHistogramBin bin, Span<long> counts, Span<long> mins, Span<long> maxs,
        ref int minBin, ref int maxBin, ref int nonEmpty)
    {
        Require(bin.Bin);
        if (bin.Count <= 0)
            return 0;
        var idx = bin.Bin;
        if (counts[idx] == 0)
        {
            counts[idx] = bin.Count;
            mins[idx] = bin.Minimum;
            maxs[idx] = bin.Maximum;
            nonEmpty++;
            if (idx < minBin)
                minBin = idx;
            if (idx > maxBin)
                maxBin = idx;
        }
        else
        {
            counts[idx] += bin.Count;
            if (bin.Minimum < mins[idx])
                mins[idx] = bin.Minimum;
            if (bin.Maximum > maxs[idx])
                maxs[idx] = bin.Maximum;
        }
        return bin.Count;
    }

    private static long EvaluatePercentile(ReadOnlySpan<long> counts, ReadOnlySpan<long> mins, ReadOnlySpan<long> maxs,
        int minBin, int maxBin, long total, double fraction)
    {
        var rank = Math.Max(1, (long)Math.Ceiling(fraction * total));
        long cumulative = 0;
        for (var i = minBin; i <= maxBin; i++)
        {
            var c = counts[i];
            if (c == 0)
                continue;
            cumulative += c;
            if (cumulative >= rank)
                return Math.Clamp((Lower(i) + Upper(i)) / 2, mins[i], maxs[i]);
        }
        return maxs[maxBin];
    }

    private static void Require(int bin)
    {
        if (bin is < 0 or >= BinCount)
            throw new ArgumentOutOfRangeException(nameof(bin));
    }
}
