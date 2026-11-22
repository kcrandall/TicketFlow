using TicketFlow.Core;

namespace TicketFlow.Events;

public sealed class EventsOptions
{
    /// <summary>The outbox consumer name of <see cref="EventProjector"/>.</summary>
    public const string Consumer = "events";

    /// <summary>The CloudEvents <c>source</c> URI reference of rendered events (default <c>/ticketflow</c>).</summary>
    public string Source { get; set; } = "/ticketflow";

    /// <summary>
    /// The SLA worker API client (<c>SlaOptions.WorkerClientId</c>). Only <c>sla.*</c> timeline events appended by this actor become
    /// <c>ticketflow.sla.*</c> events; any other actor's are ignored. Default <c>sla-worker</c>.
    /// </summary>
    public ApiClientId SlaWorkerClientId { get; set; } = new("sla-worker");

    /// <summary>Subscriptions claimed per delivery run (1-100, default 20).</summary>
    public int DeliveryClaimLimit { get; set; } = 20;

    /// <summary>Delivery claim lease (default 1 minute, at most 1 hour). A sink call must finish within it.</summary>
    public TimeSpan DeliveryLease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A claim whose lease ends within this margin is skipped (default 5 seconds; shorter than the lease).</summary>
    public TimeSpan LeaseSafetyMargin { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Log events examined per delivery batch while looking for matching events (1-500, default 500).</summary>
    public int MaximumScan { get; set; } = 500;

    /// <summary>Consecutive failed attempts of a batch before it is dead-lettered (1-100, default 10).</summary>
    public int MaximumAttempts { get; set; } = 10;

    /// <summary>First retry delay, doubled per attempt (default 5 seconds).</summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Retry delay ceiling (default 15 minutes).</summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Events recorded longer ago are pruned by <see cref="EventRetentionWorker"/> (at least one hour; default 30 days).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Dead letters older than this are pruned by <see cref="EventRetentionWorker"/> (at least one hour; default 30 days).</summary>
    public TimeSpan DeadLetterRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Rows deleted per prune statement (1-10000, default 1000).</summary>
    public int PruneBatchSize { get; set; } = 1000;

    /// <summary>Prune statements per table per retention run (1-1000, default 10).</summary>
    public int MaximumPruneBatches { get; set; } = 10;

    /// <summary>Timeline events replayed to reconstruct a conversation's initial state for <c>conversation.created</c> (1-100000, default 10000).</summary>
    public int MaximumReplayEvents { get; set; } = 10_000;

    /// <summary>Survey responses read per survey follower run (1-10000, default 2000). A slice with more responses throws (shorten <see cref="SurveySlice"/>).</summary>
    public int MaximumSurveyResponsesPerRun { get; set; } = 2000;

    /// <summary>Time window the survey follower advances per run (1 minute to 1 day, default 1 hour).</summary>
    public TimeSpan SurveySlice { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Overlap the survey follower re-reads behind its checkpoint so responses committed late are not missed (0 to 1 hour, default 5
    /// minutes). Re-read responses are deduplicated by event id.
    /// </summary>
    public TimeSpan SurveyOverlap { get; set; } = TimeSpan.FromMinutes(5);

    internal CloudEventRenderOptions RenderOptions => new(Source);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source) || SlaWorkerClientId == default || DeliveryClaimLimit is < 1 or > IEventSubscriptionStore.MaximumClaim
            || DeliveryLease <= TimeSpan.Zero || DeliveryLease > IEventSubscriptionStore.MaximumLease || LeaseSafetyMargin < TimeSpan.Zero || LeaseSafetyMargin >= DeliveryLease
            || MaximumScan is < 1 or > IEventLog.MaximumRead || MaximumAttempts is < 1 or > 100 || InitialRetryDelay <= TimeSpan.Zero || MaximumRetryDelay < InitialRetryDelay
            || Retention < TimeSpan.FromHours(1) || DeadLetterRetention < TimeSpan.FromHours(1) || PruneBatchSize is < 1 or > IEventLog.MaximumPrune
            || MaximumPruneBatches is < 1 or > 1000 || MaximumReplayEvents is < 1 or > 100_000 || MaximumSurveyResponsesPerRun is < 1 or > 10_000
            || SurveySlice < TimeSpan.FromMinutes(1) || SurveySlice > TimeSpan.FromDays(1) || SurveyOverlap < TimeSpan.Zero || SurveyOverlap > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Event options are invalid.");
        _ = RenderOptions;
    }
}
