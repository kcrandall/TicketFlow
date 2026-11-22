using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Reporting;

/// <summary>
/// Durable reporting state. Contract:
/// <list type="bullet">
/// <item>Every scoped operation is confined to one <see cref="TicketScope"/>.</item>
/// <item><see cref="ApplyAsync"/> is atomic: the conversation state is created (when <c>expectedRevision</c> is
/// <see langword="null"/> and none exists) or replaced compare-and-set on revision; facts whose (scope, source id, metric) already
/// exist are ignored — even with different content — and every newly inserted fact is added to its rollup cells (one per-fact row
/// plus one row per carried label) in the same transaction. A fact is therefore counted exactly once however often it is applied,
/// and a late fact updates the cell of its own instant.</item>
/// <item>Rollup cells are keyed by (metric, 15-minute cell start, inbox, team, agent, channel, priority, label or none, histogram
/// bin) and keep count, sum, minimum and maximum.</item>
/// <item><see cref="AggregateAsync"/> and <see cref="CountBacklogAsync"/> include exactly the rows for which
/// <see cref="AccessFilterEvaluator.Matches"/> over <see cref="ReportResources.Rollup"/> (respectively
/// <see cref="ReportResources.Conversation"/>) is true, applying the filter in the query; <see cref="ListFactsAsync"/> likewise over
/// <see cref="ReportResources.Fact"/>.</item>
/// <item><see cref="PruneFactsAsync"/> and <see cref="PruneRollupsAsync"/> are store-wide retention deletes; facts are never updated.
/// <see cref="RebuildRollupsAsync"/> recomputes a scope's cells in a range from its facts.</item>
/// </list>
/// </summary>
public interface IReportingStore
{
    Task<StoredReportConversation?> GetConversationAsync(TicketScope scope, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<ReportWriteResult> ApplyAsync(TicketScope scope, ReportProjection projection, long? expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Deletes the conversation state (fenced); facts and rollups remain.</summary>
    Task<ReportWriteResult> DeleteConversationAsync(TicketScope scope, ConversationId conversation, long expectedRevision, CancellationToken cancellationToken = default);

    Task<ReportAggregateResult> AggregateAsync(TicketScope scope, ReportAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Open (not resolved or closed) conversation states matching <paramref name="query"/> (agent = assignee).</summary>
    Task<long> CountBacklogAsync(TicketScope scope, ReportFilter query, AccessFilter filter, CancellationToken cancellationToken = default);

    Task<ReportFactPage> ListFactsAsync(TicketScope scope, ReportFactQuery query, AccessFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Deletes at most <paramref name="limit"/> (1-10000) facts that occurred before <paramref name="olderThan"/>, across all scopes.</summary>
    Task<int> PruneFactsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default);

    /// <summary>Deletes at most <paramref name="limit"/> (1-10000) rollup rows whose cell starts before <paramref name="olderThan"/>, across all scopes.</summary>
    Task<int> PruneRollupsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the scope's rollup rows with cells in [<paramref name="from"/>, <paramref name="to"/>) (cell aligned, at most
    /// 31 days) by aggregates of its facts in that range; returns the number of facts counted. Facts already pruned cannot be restored.
    /// </summary>
    Task<long> RebuildRollupsAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

/// <summary>Store limits shared by implementations.</summary>
public static class ReportStoreLimits
{
    public const int MaximumPrune = 10_000;
    public static readonly TimeSpan MaximumRebuildRange = TimeSpan.FromDays(31);

    public static void RequireRebuildRange(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from || to - from > MaximumRebuildRange || !ReportBuckets.IsCellAligned(from) || !ReportBuckets.IsCellAligned(to))
            throw new ArgumentException("Rebuild ranges must be non-empty, cell aligned and at most 31 days.", nameof(to));
    }
}
