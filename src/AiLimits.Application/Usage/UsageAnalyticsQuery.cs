// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Usage;

public enum UsageTimeGrain
{
    Day,
    Week,
    Month
}

public enum UsageChartSeriesDimension
{
    Provider,
    Harness,
    Model
}

public enum UsageBreakdownDimension
{
    Provider,
    Harness,
    Project,
    Model
}

public sealed record UsageAnalyticsRecord(
    DateOnly Day,
    string ProviderKey,
    string ProviderLabel,
    string HarnessKey,
    string HarnessLabel,
    string ProjectKey,
    string ProjectLabel,
    string ModelKey,
    string ModelLabel,
    long Tokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    decimal? ApiEquivalentCostUsd);

public sealed record UsageAnalyticsQuery(
    DateOnly From,
    DateOnly Through,
    UsageTimeGrain TimeGrain,
    UsageBreakdownDimension Breakdown,
    IReadOnlyCollection<string>? Providers = null,
    IReadOnlyCollection<string>? Harnesses = null,
    IReadOnlyCollection<string>? Projects = null,
    IReadOnlyCollection<string>? Models = null,
    bool ComparePreviousPeriod = true,
    UsageChartSeriesDimension ChartSeries = UsageChartSeriesDimension.Provider);

public sealed record UsageFacetOption(string Key, string Label, long Tokens);

public sealed record UsageFacetSet(
    IReadOnlyList<UsageFacetOption> Providers,
    IReadOnlyList<UsageFacetOption> Harnesses,
    IReadOnlyList<UsageFacetOption> Projects,
    IReadOnlyList<UsageFacetOption> Models);

public sealed record UsageChartSegment(string Key, string Label, long Tokens, bool IsOthers);

public sealed record UsageChartLegendItem(string Key, string Label, long Tokens, bool IsOthers);

public sealed record UsageChartBucket(
    DateOnly From,
    DateOnly Through,
    long Tokens,
    IReadOnlyList<UsageChartSegment> Segments);

public sealed record UsageBreakdownItem(
    string Key,
    string Label,
    long Tokens,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    decimal? ApiEquivalentCostUsd,
    double SharePercent);

public sealed record UsageComparison(
    long CurrentTokens,
    long PreviousTokens,
    double? PercentChange,
    bool IsNew);

/// <summary>
/// What the matching rows are actually made of. Distinct from
/// <see cref="UsageFacetSet"/>, whose counts deliberately ignore their own
/// filter so a selection can be widened — those are "what you could pick",
/// these are "what you picked".
/// </summary>
public sealed record UsageComposition(
    int Providers,
    int Harnesses,
    int Projects,
    int Models,
    int ActiveBuckets);

/// <summary>
/// One model's usage across the window, with a per-bucket series aligned
/// index-for-index with <see cref="UsageAnalyticsResult.Chart"/> so the UI can
/// draw a sparkline against the same time axis as the main chart.
/// </summary>
/// <param name="BucketTokens">Tokens per chart bucket, same length and order as the chart.</param>
/// <param name="PeakBucketTokens">Largest single-bucket value, so a sparkline can scale itself.</param>
/// <param name="ActiveBuckets">Buckets with any usage, i.e. how many days/weeks this model was actually used.</param>
public sealed record UsageModelTrend(
    string Key,
    string Label,
    long Tokens,
    decimal? ApiEquivalentCostUsd,
    double SharePercent,
    IReadOnlyList<long> BucketTokens,
    long PeakBucketTokens,
    int ActiveBuckets,
    DateOnly? FirstUsed,
    DateOnly? LastUsed);

public sealed record UsageAnalyticsResult(
    DateOnly From,
    DateOnly Through,
    long TotalTokens,
    decimal? ApiEquivalentCostUsd,
    long PricedTokens,
    int MatchingRecordCount,
    UsageComparison? Comparison,
    UsageFacetSet Facets,
    IReadOnlyList<UsageChartBucket> Chart,
    IReadOnlyList<UsageChartLegendItem> ChartLegend,
    IReadOnlyList<UsageBreakdownItem> Breakdown,
    IReadOnlyList<UsageModelTrend> ModelTrends,
    UsageComposition Composition);

/// <summary>
/// Pure, deterministic query engine for the local usage ledger. Empty filter
/// sets mean "all". Each facet is calculated with its own filter removed so
/// users can broaden a selection without first clearing the current one.
/// </summary>
public static class UsageAnalyticsQueryEngine
{
    public static UsageAnalyticsResult Run(
        IReadOnlyList<UsageAnalyticsRecord> records,
        UsageAnalyticsQuery query)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(query);

        DateOnly from = query.From <= query.Through ? query.From : query.Through;
        DateOnly through = query.From <= query.Through ? query.Through : query.From;
        UsageAnalyticsQuery normalized = query with { From = from, Through = through };

        UsageAnalyticsRecord[] current = records
            .Where(record => record.Day >= from && record.Day <= through)
            .Where(record => MatchesFilters(record, normalized, null))
            .ToArray();

        long totalTokens = current.Sum(record => record.Tokens);
        decimal? totalCost = SumCost(current);
        long pricedTokens = current.Where(record => record.ApiEquivalentCostUsd.HasValue).Sum(record => record.Tokens);

        UsageChartProjection chart = BuildChart(
            current,
            from,
            through,
            normalized.TimeGrain,
            normalized.ChartSeries,
            HasExplicitChartSeries(normalized));

        return new UsageAnalyticsResult(
            from,
            through,
            totalTokens,
            totalCost,
            pricedTokens,
            current.Length,
            normalized.ComparePreviousPeriod ? BuildComparison(records, normalized, current) : null,
            BuildFacets(records, normalized),
            chart.Buckets,
            chart.Legend,
            BuildBreakdown(current, normalized.Breakdown),
            BuildModelTrends(current, chart.Buckets, normalized.TimeGrain),
            BuildComposition(current, chart.Buckets));
    }

    private static UsageComposition BuildComposition(
        IReadOnlyList<UsageAnalyticsRecord> records,
        IReadOnlyList<UsageChartBucket> buckets) =>
        new(
            Distinct(records, record => record.ProviderKey),
            Distinct(records, record => record.HarnessKey),
            Distinct(records, record => record.ProjectKey),
            Distinct(records, record => record.ModelKey),
            buckets.Count(bucket => bucket.Tokens > 0));

    private static int Distinct(
        IEnumerable<UsageAnalyticsRecord> records,
        Func<UsageAnalyticsRecord, string> key) =>
        records.Select(key).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// Per-model shape over the window, on the same buckets as the chart.
    /// Answers "which models did this harness actually run, and when" without
    /// making the user re-point the breakdown selector at Model.
    /// </summary>
    private static IReadOnlyList<UsageModelTrend> BuildModelTrends(
        IReadOnlyList<UsageAnalyticsRecord> records,
        IReadOnlyList<UsageChartBucket> buckets,
        UsageTimeGrain grain)
    {
        long total = Math.Max(1, records.Sum(record => record.Tokens));
        // Bucket lookup by start day, so a record maps to its column in O(1).
        Dictionary<DateOnly, int> bucketIndex = [];
        for (int index = 0; index < buckets.Count; index++)
        {
            bucketIndex[BucketStart(buckets[index].From, grain)] = index;
        }

        return records
            .GroupBy(record => record.ModelKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                UsageAnalyticsRecord[] values = group.ToArray();
                long[] series = new long[buckets.Count];
                foreach (UsageAnalyticsRecord record in values)
                {
                    if (bucketIndex.TryGetValue(BucketStart(record.Day, grain), out int index))
                    {
                        series[index] += record.Tokens;
                    }
                }
                long tokens = values.Sum(record => record.Tokens);
                return new UsageModelTrend(
                    group.Key,
                    DominantLabel(values, UsageBreakdownDimension.Model),
                    tokens,
                    SumCost(values),
                    100.0 * tokens / total,
                    series,
                    series.Length == 0 ? 0 : series.Max(),
                    series.Count(value => value > 0),
                    values.Min(record => record.Day),
                    values.Max(record => record.Day));
            })
            .Where(trend => trend.Tokens > 0)
            .OrderByDescending(trend => trend.Tokens)
            .ThenBy(trend => trend.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The label carried by the biggest slice of a key's records. Grouping is
    /// always by key alone; a key that somehow arrives with two spellings must
    /// still collapse to one row rather than double-count.
    /// </summary>
    private static string DominantLabel(
        IReadOnlyList<UsageAnalyticsRecord> records,
        UsageBreakdownDimension dimension) =>
        records
            .GroupBy(record => Label(record, dimension))
            .OrderByDescending(group => group.Sum(record => record.Tokens))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault() ?? string.Empty;

    private static UsageComparison BuildComparison(
        IReadOnlyList<UsageAnalyticsRecord> records,
        UsageAnalyticsQuery query,
        IReadOnlyList<UsageAnalyticsRecord> current)
    {
        int days = query.Through.DayNumber - query.From.DayNumber + 1;
        DateOnly previousThrough = query.From.AddDays(-1);
        DateOnly previousFrom = previousThrough.AddDays(1 - days);
        long currentTokens = current.Sum(record => record.Tokens);
        long previousTokens = records
            .Where(record => record.Day >= previousFrom && record.Day <= previousThrough)
            .Where(record => MatchesFilters(record, query, null))
            .Sum(record => record.Tokens);
        bool isNew = previousTokens == 0 && currentTokens > 0;
        double? change = previousTokens == 0
            ? currentTokens == 0 ? 0 : null
            : 100.0 * (currentTokens - previousTokens) / previousTokens;
        return new UsageComparison(currentTokens, previousTokens, change, isNew);
    }

    private static UsageFacetSet BuildFacets(
        IReadOnlyList<UsageAnalyticsRecord> records,
        UsageAnalyticsQuery query) =>
        new(
            BuildFacet(records, query, UsageBreakdownDimension.Provider),
            BuildFacet(records, query, UsageBreakdownDimension.Harness),
            BuildFacet(records, query, UsageBreakdownDimension.Project),
            BuildFacet(records, query, UsageBreakdownDimension.Model));

    private static IReadOnlyList<UsageFacetOption> BuildFacet(
        IReadOnlyList<UsageAnalyticsRecord> records,
        UsageAnalyticsQuery query,
        UsageBreakdownDimension dimension) =>
        records
            .Where(record => record.Day >= query.From && record.Day <= query.Through)
            .Where(record => MatchesFilters(record, query, dimension))
            // By key only. Filtering is keyed, so a key split across two label
            // spellings would list one provider twice and each row would
            // filter only part of its own usage.
            .GroupBy(record => Key(record, dimension), StringComparer.OrdinalIgnoreCase)
            .Select(group => new UsageFacetOption(
                group.Key,
                DominantLabel(group.ToArray(), dimension),
                group.Sum(record => record.Tokens)))
            .Where(option => option.Tokens > 0)
            .OrderByDescending(option => option.Tokens)
            .ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static UsageChartProjection BuildChart(
        IReadOnlyList<UsageAnalyticsRecord> records,
        DateOnly from,
        DateOnly through,
        UsageTimeGrain grain,
        UsageChartSeriesDimension dimension,
        bool explicitSelection)
    {
        // Segments below resolve by key, so the totals must be keyed the same
        // way: two totals sharing a key would each claim the key's whole sum
        // and the stack would render double the real height.
        SeriesTotal[] totals = records
            .GroupBy(record => SeriesKey(record, dimension), StringComparer.OrdinalIgnoreCase)
            .Select(group => new SeriesTotal(
                group.Key,
                DominantLabel(group.ToArray(), SeriesBreakdown(dimension)),
                group.Sum(record => record.Tokens)))
            .Where(item => item.Tokens > 0)
            .OrderByDescending(item => item.Tokens)
            .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        SeriesTotal[] primary = explicitSelection ? totals : totals.Take(3).ToArray();
        var legend = primary
            .Select(item => new UsageChartLegendItem(item.Key, item.Label, item.Tokens, false))
            .ToList();
        HashSet<string> primaryKeys = primary
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        long othersTokens = explicitSelection
            ? 0
            : totals.Where(item => !primaryKeys.Contains(item.Key)).Sum(item => item.Tokens);
        if (othersTokens > 0)
        {
            legend.Add(new UsageChartLegendItem("__others__", "Others", othersTokens, true));
        }

        Dictionary<DateOnly, UsageAnalyticsRecord[]> recordsByBucket = records
            .GroupBy(record => BucketStart(record.Day, grain))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var buckets = new List<UsageChartBucket>();
        for (DateOnly cursor = BucketStart(from, grain); cursor <= through; cursor = NextBucket(cursor, grain))
        {
            DateOnly visibleFrom = cursor < from ? from : cursor;
            DateOnly end = NextBucket(cursor, grain).AddDays(-1);
            DateOnly visibleThrough = end > through ? through : end;
            UsageAnalyticsRecord[] bucketRecords = recordsByBucket.GetValueOrDefault(cursor) ?? [];
            UsageChartSegment[] segments = legend
                .Select(item => new UsageChartSegment(
                    item.Key,
                    item.Label,
                    item.IsOthers
                        ? bucketRecords
                            .Where(record => !primaryKeys.Contains(SeriesKey(record, dimension)))
                            .Sum(record => record.Tokens)
                        : bucketRecords
                            .Where(record => string.Equals(
                                SeriesKey(record, dimension),
                                item.Key,
                                StringComparison.OrdinalIgnoreCase))
                            .Sum(record => record.Tokens),
                    item.IsOthers))
                .ToArray();
            buckets.Add(new UsageChartBucket(
                visibleFrom,
                visibleThrough,
                segments.Sum(segment => segment.Tokens),
                segments));
        }
        return new UsageChartProjection(buckets, legend);
    }

    private static bool HasExplicitChartSeries(UsageAnalyticsQuery query) => query.ChartSeries switch
    {
        UsageChartSeriesDimension.Provider => query.Providers is { Count: > 0 },
        UsageChartSeriesDimension.Harness => query.Harnesses is { Count: > 0 },
        _ => query.Models is { Count: > 0 }
    };

    private static UsageBreakdownDimension SeriesBreakdown(UsageChartSeriesDimension dimension) => dimension switch
    {
        UsageChartSeriesDimension.Provider => UsageBreakdownDimension.Provider,
        UsageChartSeriesDimension.Harness => UsageBreakdownDimension.Harness,
        _ => UsageBreakdownDimension.Model
    };

    private static string SeriesKey(
        UsageAnalyticsRecord record,
        UsageChartSeriesDimension dimension) =>
        Key(record, SeriesBreakdown(dimension));

    private static IReadOnlyList<UsageBreakdownItem> BuildBreakdown(
        IReadOnlyList<UsageAnalyticsRecord> records,
        UsageBreakdownDimension dimension)
    {
        long total = Math.Max(1, records.Sum(record => record.Tokens));
        return records
            .GroupBy(record => Key(record, dimension), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                UsageAnalyticsRecord[] values = group.ToArray();
                long tokens = values.Sum(record => record.Tokens);
                return new UsageBreakdownItem(
                    group.Key,
                    DominantLabel(values, dimension),
                    tokens,
                    values.Sum(record => record.InputTokens),
                    values.Sum(record => record.OutputTokens),
                    values.Sum(record => record.CacheReadTokens),
                    values.Sum(record => record.CacheWriteTokens),
                    values.Sum(record => record.ReasoningTokens),
                    SumCost(values),
                    100.0 * tokens / total);
            })
            .Where(item => item.Tokens > 0)
            .OrderByDescending(item => item.Tokens)
            .ThenBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool MatchesFilters(
        UsageAnalyticsRecord record,
        UsageAnalyticsQuery query,
        UsageBreakdownDimension? ignored) =>
        (ignored == UsageBreakdownDimension.Provider || Contains(query.Providers, record.ProviderKey))
        && (ignored == UsageBreakdownDimension.Harness || Contains(query.Harnesses, record.HarnessKey))
        && (ignored == UsageBreakdownDimension.Project || Contains(query.Projects, record.ProjectKey))
        && (ignored == UsageBreakdownDimension.Model || Contains(query.Models, record.ModelKey));

    private static bool Contains(IReadOnlyCollection<string>? selected, string value) =>
        selected is null || selected.Count == 0 || selected.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static string Key(UsageAnalyticsRecord record, UsageBreakdownDimension dimension) => dimension switch
    {
        UsageBreakdownDimension.Provider => record.ProviderKey,
        UsageBreakdownDimension.Harness => record.HarnessKey,
        UsageBreakdownDimension.Project => record.ProjectKey,
        _ => record.ModelKey
    };

    private static string Label(UsageAnalyticsRecord record, UsageBreakdownDimension dimension) => dimension switch
    {
        UsageBreakdownDimension.Provider => record.ProviderLabel,
        UsageBreakdownDimension.Harness => record.HarnessLabel,
        UsageBreakdownDimension.Project => record.ProjectLabel,
        _ => record.ModelLabel
    };

    private static DateOnly BucketStart(DateOnly day, UsageTimeGrain grain) => grain switch
    {
        UsageTimeGrain.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        UsageTimeGrain.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day
    };

    private static DateOnly NextBucket(DateOnly day, UsageTimeGrain grain) => grain switch
    {
        UsageTimeGrain.Week => day.AddDays(7),
        UsageTimeGrain.Month => day.AddMonths(1),
        _ => day.AddDays(1)
    };

    private sealed record SeriesTotal(string Key, string Label, long Tokens);

    private sealed record UsageChartProjection(
        IReadOnlyList<UsageChartBucket> Buckets,
        IReadOnlyList<UsageChartLegendItem> Legend);

    private static decimal? SumCost(IEnumerable<UsageAnalyticsRecord> records)
    {
        UsageAnalyticsRecord[] values = records.ToArray();
        return values.Any(record => record.ApiEquivalentCostUsd.HasValue)
            ? values.Where(record => record.ApiEquivalentCostUsd.HasValue).Sum(record => record.ApiEquivalentCostUsd.GetValueOrDefault())
            : null;
    }
}

public sealed record UsageChartGeometry(
    double ItemWidth,
    double BarWidth,
    double ConsumedWidth);

/// <summary>
/// Shared chart sizing policy: every bucket receives an equal slice of the
/// current viewport while marks remain visually useful on very wide windows.
/// </summary>
public static class UsageChartLayout
{
    /// <summary>Bar pitch at which a full "d MMM" date label fits; below this only the day number does.</summary>
    public const double WideLabelWidth = 46;

    /// <summary>
    /// Which buckets get a date label, driven by the real bar pitch rather
    /// than a fixed every-nth rule: the axis has to stay readable both when
    /// seven bars fill the width and when a year of weeks is packed in. The
    /// last bucket always gets one — it is the value users look for — but
    /// never on top of a neighbour the stride just placed.
    /// </summary>
    public static bool[] LabelledBuckets(int itemCount, double itemWidth)
    {
        bool[] labelled = new bool[Math.Max(0, itemCount)];
        if (labelled.Length == 0)
        {
            return labelled;
        }

        double needed = itemWidth >= WideLabelWidth ? 62 : 30;
        double pitch = double.IsFinite(itemWidth) && itemWidth > 0 ? itemWidth : 1;
        int stride = Math.Max(1, (int)Math.Ceiling(needed / pitch));
        for (int index = 0; index < labelled.Length; index += stride)
        {
            labelled[index] = true;
        }

        int last = labelled.Length - 1;
        for (int index = Math.Max(0, last - stride + 1); index < last; index++)
        {
            labelled[index] = false;
        }
        labelled[last] = true;
        return labelled;
    }

    public static UsageChartGeometry Calculate(
        double availableWidth,
        int itemCount,
        double gap = 3)
    {
        if (itemCount <= 0 || !double.IsFinite(availableWidth) || availableWidth <= 0)
        {
            return new UsageChartGeometry(0, 0, 0);
        }

        double safeGap = Math.Max(0, gap);
        double gapWidth = safeGap * Math.Max(0, itemCount - 1);
        double itemWidth = Math.Max(4, (availableWidth - gapWidth) / itemCount);
        double barWidth = Math.Clamp(itemWidth * 0.68, 3, 48);
        double consumedWidth = itemWidth * itemCount + gapWidth;
        return new UsageChartGeometry(itemWidth, barWidth, consumedWidth);
    }
}
