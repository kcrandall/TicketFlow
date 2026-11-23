using TicketFlow.Core;

namespace TicketFlow.Assist;

/// <summary>
/// Development-only <see cref="IAssistStore"/>: process-local, bounded (<see cref="MaximumRecordsPerScope"/> usage records per scope;
/// settled records from earlier UTC days are pruned first), not durable. One lock serializes every operation.
/// </summary>
public sealed class InMemoryAssistStore : IAssistStore
{
    public const int MaximumRecordsPerScope = 50_000;

    private readonly Lock gate = new();
    private readonly Dictionary<TicketScope, StoredAssistSettings> settings = [];
    private readonly Dictionary<TicketScope, List<AssistUsageRecord>> usage = [];

    public Task<StoredAssistSettings?> GetSettingsAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(settings.GetValueOrDefault(scope));
    }

    public Task<AssistWriteResult<StoredAssistSettings>> PutSettingsAsync(TicketScope scope, AssistSettings value, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var current = settings.GetValueOrDefault(scope)?.Revision ?? 0;
            if (current != expectedRevision)
                return Task.FromResult<AssistWriteResult<StoredAssistSettings>>(new StoreConflict(current));
            var stored = new StoredAssistSettings(scope, value, current + 1);
            settings[scope] = stored;
            return Task.FromResult<AssistWriteResult<StoredAssistSettings>>(new StoreApplied<StoredAssistSettings>(stored));
        }
    }

    public Task<AssistReservationResult> ReserveAsync(TicketScope scope, AssistUsageRecord reservation, long dailyTokenLimit, int requestsPerMinute, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.State != AssistUsageState.Reserved)
            throw new ArgumentException("Reservations start in the Reserved state.", nameof(reservation));
        ArgumentOutOfRangeException.ThrowIfLessThan(dailyTokenLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(requestsPerMinute, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!usage.TryGetValue(scope, out var records))
                usage[scope] = records = [];
            if (records.Any(record => record.Id == reservation.Id))
                throw new InvalidOperationException("A usage record with this identifier already exists.");
            var dayStart = AssistLimits.DayStart(reservation.CreatedAt);
            var dayEnd = dayStart.AddDays(1);
            if (records.Count >= MaximumRecordsPerScope)
                records.RemoveAll(record => record.State != AssistUsageState.Reserved && record.CreatedAt < dayStart);
            if (records.Count >= MaximumRecordsPerScope)
                return Task.FromResult<AssistReservationResult>(new AssistQuotaExceeded(AssistQuotaKind.DailyTokens, dayEnd));
            var tokens = records.Where(record => record.CreatedAt >= dayStart && record.CreatedAt < dayEnd).Sum(record => record.CountedTokens);
            if (tokens + reservation.ReservedTokens > dailyTokenLimit)
                return Task.FromResult<AssistReservationResult>(new AssistQuotaExceeded(AssistQuotaKind.DailyTokens, dayEnd));
            var windowStart = reservation.CreatedAt - TimeSpan.FromMinutes(1);
            var recent = records.Where(record => record.CreatedAt > windowStart && record.CreatedAt <= reservation.CreatedAt).Select(record => record.CreatedAt).Order().ToList();
            if (recent.Count >= requestsPerMinute)
                return Task.FromResult<AssistReservationResult>(new AssistQuotaExceeded(AssistQuotaKind.RequestsPerMinute, recent[recent.Count - requestsPerMinute] + TimeSpan.FromMinutes(1)));
            records.Add(reservation);
            return Task.FromResult<AssistReservationResult>(new AssistUsageReserved(reservation));
        }
    }

    public Task<AssistUsageRecord?> SettleAsync(TicketScope scope, Guid id, AssistUsageState state, AssistUsage value, DateTimeOffset completedAt, bool estimated = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(value);
        if (state is not (AssistUsageState.Completed or AssistUsageState.Failed))
            throw new ArgumentOutOfRangeException(nameof(state));
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!usage.TryGetValue(scope, out var records))
                return Task.FromResult<AssistUsageRecord?>(null);
            var index = records.FindIndex(record => record.Id == id);
            if (index < 0 || records[index].State != AssistUsageState.Reserved)
                return Task.FromResult<AssistUsageRecord?>(null);
            var settled = records[index].Settle(state, value, completedAt, estimated);
            records[index] = settled;
            return Task.FromResult<AssistUsageRecord?>(settled);
        }
    }

    public Task<int> SweepReservationsAsync(DateTimeOffset createdBefore, DateTimeOffset completedAt, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AssistLimits.MaximumPurgeBatch);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var stale = usage.Values.SelectMany(records => records.Select((record, index) => (records, record, index)))
                .Where(entry => entry.record.State == AssistUsageState.Reserved && entry.record.CreatedAt < createdBefore)
                .OrderBy(entry => entry.record.CreatedAt).Take(limit).ToList();
            foreach (var (records, record, index) in stale)
                records[index] = record.Settle(AssistUsageState.Failed, AssistUsage.Zero, completedAt, estimated: true);
            return Task.FromResult(stale.Count);
        }
    }

    public Task<AssistUsageRecord?> GetUsageAsync(TicketScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(usage.TryGetValue(scope, out var records) ? records.FirstOrDefault(record => record.Id == id) : null);
    }

    public Task<AssistUsageSummary> SummarizeAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        AssistLimits.RequireRange(from, to);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var records = usage.TryGetValue(scope, out var list) ? list.Where(record => record.CreatedAt >= from && record.CreatedAt < to).ToList() : [];
            var totals = records
                .GroupBy(record => (record.Feature, record.Model))
                .OrderBy(group => group.Key.Feature).ThenBy(group => group.Key.Model, StringComparer.Ordinal)
                .Select(group => new AssistUsageTotal(group.Key.Feature, group.Key.Model, group.LongCount(), group.LongCount(record => record.State == AssistUsageState.Failed),
                    group.Sum(record => record.Usage.InputTokens), group.Sum(record => record.Usage.OutputTokens), group.Sum(record => record.Usage.CacheReadInputTokens),
                    group.Sum(record => record.Usage.CacheCreationInputTokens), group.Where(record => record.State == AssistUsageState.Reserved).Sum(record => record.ReservedTokens),
                    group.Where(record => record.Estimated).Sum(record => record.ReservedTokens)))
                .ToList();
            return Task.FromResult(new AssistUsageSummary(from, to, totals));
        }
    }

    public Task<int> PurgeUsageAsync(TicketScope scope, DateTimeOffset before, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AssistLimits.MaximumPurgeBatch);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!usage.TryGetValue(scope, out var records))
                return Task.FromResult(0);
            var doomed = records.Where(record => record.State != AssistUsageState.Reserved && record.CreatedAt < before).OrderBy(record => record.CreatedAt).Take(limit).ToHashSet();
            records.RemoveAll(doomed.Contains);
            return Task.FromResult(doomed.Count);
        }
    }
}
