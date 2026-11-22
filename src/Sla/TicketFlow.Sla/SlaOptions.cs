using TicketFlow.Core;

namespace TicketFlow.Sla;

public sealed class SlaOptions
{
    /// <summary>The outbox consumer name of <see cref="SlaConversationTracker"/>.</summary>
    public const string Consumer = "sla";

    /// <summary>
    /// The API client the tracker acts as when appending SLA timeline events (one <see cref="ApiClientPrincipal"/> per workspace scope).
    /// It needs a grant with <c>conversation:manage</c> in every workspace that uses SLAs. Default <c>sla-worker</c>.
    /// </summary>
    public ApiClientId WorkerClientId { get; set; } = new("sla-worker");

    /// <summary>Attempts when concurrent tracker writers win (1-64, default 16).</summary>
    public int MaximumWriteAttempts { get; set; } = 16;

    /// <summary>Timers claimed per scheduler run (1-100, default 50).</summary>
    public int TimerBatchSize { get; set; } = 50;

    /// <summary>Timer claim lease (default 1 minute, at most 1 hour).</summary>
    public TimeSpan TimerLease { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Delay before a timer whose evaluation failed is retried (default 1 minute).</summary>
    public TimeSpan TimerRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Conversations first seen with a longer timeline are adopted from their snapshot instead of replayed (1-10000, default 1000).</summary>
    public int MaximumReplayEvents { get; set; } = 1000;

    internal void Validate()
    {
        if (WorkerClientId == default || MaximumWriteAttempts is < 1 or > 64 || TimerBatchSize is < 1 or > ISlaTrackerStore.MaximumClaim
            || TimerLease <= TimeSpan.Zero || TimerLease > ISlaTrackerStore.MaximumLease || TimerRetryDelay <= TimeSpan.Zero || MaximumReplayEvents is < 1 or > 10_000)
            throw new InvalidOperationException("SLA options are invalid.");
    }
}
