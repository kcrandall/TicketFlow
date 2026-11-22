using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Reporting;

/// <summary>
/// Development-only <see cref="IReportingStore"/>: process-local, bounded (<see cref="MaximumFacts"/>), not durable. Every operation runs
/// under one lock, so projections are atomic and facts are counted exactly once. Aggregates scan the scope's cells.
/// </summary>
public sealed class InMemoryReportingStore : IReportingStore
{
    public const int DefaultMaximumFacts = 1_000_000;

    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, ConversationId Conversation), StoredReportConversation> states = [];
    private readonly Dictionary<(TicketScope Scope, Guid Source, ReportMetric Metric), ReportFact> facts = [];
    private readonly Dictionary<CellKey, CellValue> cells = [];

    public InMemoryReportingStore(int maximumFacts = DefaultMaximumFacts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFacts, 1);
        MaximumFacts = maximumFacts;
    }

    public int MaximumFacts { get; }

    public Task<StoredReportConversation?> GetConversationAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(conversation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(states.GetValueOrDefault((scope, conversation)));
    }

    public Task<ReportWriteResult> ApplyAsync(TicketScope scope, ReportProjection projection, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(projection);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = (scope, projection.State.Conversation);
            var current = states.GetValueOrDefault(key);
            if (current?.Revision != expectedRevision)
                return Task.FromResult<ReportWriteResult>(new ReportWriteConflict(current?.Revision));
            var inserted = new List<ReportFact>();
            var seen = new HashSet<(Guid, ReportMetric)>();
            foreach (var fact in projection.Facts)
                if (seen.Add((fact.SourceId, fact.Metric)) && !facts.ContainsKey((scope, fact.SourceId, fact.Metric)))
                    inserted.Add(fact);
            if (facts.Count + inserted.Count > MaximumFacts)
                return Task.FromResult<ReportWriteResult>(new ReportWriteRejected(ReportWriteRejection.CapacityExceeded));
            foreach (var fact in inserted)
            {
                facts[(scope, fact.SourceId, fact.Metric)] = fact;
                AddCells(scope, fact);
            }
            var stored = new StoredReportConversation(scope, projection.State, (current?.Revision ?? 0) + 1);
            states[key] = stored;
            return Task.FromResult<ReportWriteResult>(new ReportWriteApplied(stored, inserted.Count));
        }
    }

    public Task<ReportWriteResult> DeleteConversationAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(conversation);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var current = states.GetValueOrDefault((scope, conversation));
            if (current?.Revision != expectedRevision)
                return Task.FromResult<ReportWriteResult>(new ReportWriteConflict(current?.Revision));
            states.Remove((scope, conversation));
            return Task.FromResult<ReportWriteResult>(new ReportWriteApplied(current, 0));
        }
    }

    public Task<ReportAggregateResult> AggregateAsync(TicketScope scope, ReportAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        var metrics = query.Metrics.ToHashSet();
        var labelRows = query.UsesLabelRows;
        var restriction = query.Filter;
        var decisions = new Dictionary<(InboxId, TeamId?, UserId?), bool>();
        var groups = new Dictionary<(int Bucket, string? Group, ReportMetric Metric, int Bin), CellValue>();
        lock (gate)
        {
            foreach (var (key, value) in cells)
            {
                if (key.Scope != scope || !metrics.Contains(key.Metric) || key.Start < query.From || key.Start >= query.To || (key.Label.Length > 0) != labelRows)
                    continue;
                if ((restriction.Inbox is { } inbox && key.Inbox != inbox) || (restriction.Team is { } team && key.Team != team)
                    || (restriction.Agent is { } agent && key.Agent != agent) || (restriction.Channel is { } channel && key.Channel != channel)
                    || (restriction.Priority is { } priority && key.Priority != priority) || (restriction.Label is { } label && key.Label != label.Value))
                    continue;
                var resource = (key.Inbox, key.Team, key.Agent);
                if (!decisions.TryGetValue(resource, out var allowed))
                    decisions[resource] = allowed = AccessFilterEvaluator.Matches(filter, ReportResources.Rollup(scope, key.Inbox, key.Team, key.Agent));
                if (!allowed)
                    continue;
                var group = ReportGroups.Key(query.GroupBy, key.Inbox, key.Team, key.Agent, key.Channel, key.Priority, labelRows ? new LabelId(key.Label) : null);
                var bin = query.Histograms && ReportMetrics.IsDuration(key.Metric) ? key.Bin : 0;
                var groupKey = (ReportBuckets.IndexOf(query.Boundaries, key.Start), group, key.Metric, bin);
                if (!groups.TryGetValue(groupKey, out var total))
                    groups[groupKey] = total = new CellValue();
                total.Add(value);
            }
        }
        var rows = groups
            .OrderBy(pair => pair.Key.Bucket).ThenBy(pair => pair.Key.Group is not null).ThenBy(pair => pair.Key.Group, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Metric).ThenBy(pair => pair.Key.Bin)
            .Take(ReportAggregateQuery.MaximumRows + 1)
            .Select(pair => new ReportAggregateRow(pair.Key.Bucket, pair.Key.Group, pair.Key.Metric, pair.Key.Bin, pair.Value.Count, pair.Value.Sum, pair.Value.Minimum, pair.Value.Maximum))
            .ToList();
        var truncated = rows.Count > ReportAggregateQuery.MaximumRows;
        if (truncated)
            rows.RemoveAt(rows.Count - 1);
        return Task.FromResult(new ReportAggregateResult(rows, truncated));
    }

    public Task<long> CountBacklogAsync(TicketScope scope, ReportFilter query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult((long)states.Values.Count(stored => stored.Scope == scope && stored.State.IsOpen && query.Matches(stored.State.Dimensions())
                && AccessFilterEvaluator.Matches(filter, ReportResources.Conversation(scope, stored.State))));
    }

    public Task<ReportFactPage> ListFactsAsync(TicketScope scope, ReportFactQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        cancellationToken.ThrowIfCancellationRequested();
        List<ReportFact> page;
        lock (gate)
        {
            var matches = facts.Where(pair => pair.Key.Scope == scope).Select(pair => pair.Value)
                .Where(fact => fact.OccurredAt >= query.From && fact.OccurredAt < query.To && query.Filter.Matches(fact.Dimensions))
                .Where(fact => query.After is not { } after || ReportFactQuery.Compare(fact.OccurredAt, fact.SourceId, fact.Metric, after.OccurredAt, after.SourceId, after.Metric) > 0)
                .Where(fact => AccessFilterEvaluator.Matches(filter, ReportResources.Fact(scope, fact)))
                .ToList();
            matches.Sort(ReportFactQuery.Compare);
            page = [.. matches.Take(query.Limit + 1)];
        }
        var more = page.Count > query.Limit;
        if (more)
            page.RemoveAt(page.Count - 1);
        return Task.FromResult(new ReportFactPage(page, more ? new ReportFactCursor(page[^1].OccurredAt, page[^1].SourceId, page[^1].Metric) : null));
    }

    public Task<int> PruneFactsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        RequireLimit(limit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var doomed = facts.Where(pair => pair.Value.OccurredAt < olderThan).OrderBy(pair => pair.Value.OccurredAt).Take(limit).Select(pair => pair.Key).ToList();
            foreach (var key in doomed)
                facts.Remove(key);
            return Task.FromResult(doomed.Count);
        }
    }

    public Task<int> PruneRollupsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        RequireLimit(limit);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var doomed = cells.Keys.Where(key => key.Start < olderThan).OrderBy(key => key.Start).Take(limit).ToList();
            foreach (var key in doomed)
                cells.Remove(key);
            return Task.FromResult(doomed.Count);
        }
    }

    public Task<long> RebuildRollupsAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ReportStoreLimits.RequireRebuildRange(from, to);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            foreach (var key in cells.Keys.Where(key => key.Scope == scope && key.Start >= from && key.Start < to).ToList())
                cells.Remove(key);
            long counted = 0;
            foreach (var (key, fact) in facts)
                if (key.Scope == scope && fact.CellStart >= from && fact.CellStart < to)
                {
                    AddCells(scope, fact);
                    counted++;
                }
            return Task.FromResult(counted);
        }
    }

    private void AddCells(TicketScope scope, ReportFact fact)
    {
        var dimensions = fact.Dimensions;
        Add(string.Empty);
        foreach (var label in dimensions.Labels)
            Add(label.Value);

        void Add(string label)
        {
            var key = new CellKey(scope, fact.Metric, fact.CellStart, dimensions.Inbox, dimensions.Team, dimensions.Agent, dimensions.Channel, dimensions.Priority, label, fact.Bin);
            if (!cells.TryGetValue(key, out var value))
                cells[key] = value = new CellValue();
            value.Add(fact.Value);
        }
    }

    private static void Require(ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
    }

    private static void RequireLimit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ReportStoreLimits.MaximumPrune);
    }

    private readonly record struct CellKey(TicketScope Scope, ReportMetric Metric, DateTimeOffset Start, InboxId Inbox, TeamId? Team, UserId? Agent, string? Channel,
        ConversationPriority Priority, string Label, int Bin);

    private sealed class CellValue
    {
        public long Count { get; private set; }
        public long Sum { get; private set; }
        public long Minimum { get; private set; } = long.MaxValue;
        public long Maximum { get; private set; } = long.MinValue;

        public void Add(long value)
        {
            Count++;
            Sum += value;
            Minimum = Math.Min(Minimum, value);
            Maximum = Math.Max(Maximum, value);
        }

        public void Add(CellValue other)
        {
            Count += other.Count;
            Sum += other.Sum;
            Minimum = Math.Min(Minimum, other.Minimum);
            Maximum = Math.Max(Maximum, other.Maximum);
        }
    }
}
