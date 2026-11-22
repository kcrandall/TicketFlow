using TicketFlow.Core;

namespace TicketFlow.Assist;

/// <summary>
/// Durable assistant settings and usage. Contract:
/// <list type="bullet">
/// <item>Every operation is confined to one <see cref="TicketScope"/>. Settings writes are compare-and-set on revision; expected
/// revision 0 creates the record (conflict when one exists).</item>
/// <item><see cref="ReserveAsync"/> is atomic per workspace: it sums <see cref="AssistUsageRecord.CountedTokens"/> of records created in
/// the UTC day of <c>record.CreatedAt</c> and counts records created in <c>(CreatedAt - 60 s, CreatedAt]</c>; when adding the record
/// would exceed <see cref="AssistSettings.DailyTokenLimit"/> or reach more than <see cref="AssistSettings.RequestsPerMinute"/>, nothing is
/// written and <see cref="AssistQuotaExceeded"/> is returned. Concurrent reservations serialize so limits cannot be raced.</item>
/// <item><see cref="SettleAsync"/> moves a Reserved record to Completed or Failed with actual usage exactly once; settling a missing or
/// already settled record returns <see langword="null"/> and writes nothing.</item>
/// <item>Records hold identifiers and counts only, never prompts or outputs.</item>
/// </list>
/// </summary>
public interface IAssistStore
{
    Task<StoredAssistSettings?> GetSettingsAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<AssistWriteResult<StoredAssistSettings>> PutSettingsAsync(TicketScope scope, AssistSettings settings, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AssistReservationResult> ReserveAsync(TicketScope scope, AssistUsageRecord reservation, long dailyTokenLimit, int requestsPerMinute, CancellationToken cancellationToken = default);

    Task<AssistUsageRecord?> SettleAsync(TicketScope scope, Guid id, AssistUsageState state, AssistUsage usage, DateTimeOffset completedAt, CancellationToken cancellationToken = default);

    Task<AssistUsageRecord?> GetUsageAsync(TicketScope scope, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Totals of records created in <c>[from, to)</c> (at most <see cref="AssistLimits.MaximumSummaryRange"/>).</summary>
    Task<AssistUsageSummary> SummarizeAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>Deletes at most <paramref name="limit"/> (1-10000) settled records created before <paramref name="before"/>; returns the count.</summary>
    Task<int> PurgeUsageAsync(TicketScope scope, DateTimeOffset before, int limit, CancellationToken cancellationToken = default);
}
