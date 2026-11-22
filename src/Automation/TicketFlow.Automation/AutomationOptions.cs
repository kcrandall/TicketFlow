using TicketFlow.Core;

namespace TicketFlow.Automation;

public sealed class AutomationOptions
{
    /// <summary>The outbox consumer name of <see cref="AutomationOutboxHandler"/>.</summary>
    public const string Consumer = "automation";

    /// <summary>
    /// The API client rules act as (one <see cref="ApiClientPrincipal"/> per workspace scope). Its grants bound every automation: grant
    /// it a custom role with exactly the permissions rules may use (for example <c>conversation:read</c>, <c>conversation:assign</c>,
    /// <c>conversation:manage</c>, <c>conversation:reply</c>, <c>note:write</c>, <c>conversation:resolve</c>, <c>contact:read</c>).
    /// Default <c>automation-worker</c>.
    /// </summary>
    public ApiClientId ClientId { get; set; } = new("automation-worker");

    /// <summary>Longest automation chain: an execution triggered by an event an automation caused at depth n runs at depth n+1 (1-8, default 3).</summary>
    public int MaximumChainDepth { get; set; } = 3;

    /// <summary>Executions per conversation within <see cref="RateWindow"/> before further executions are suppressed (1-1000, default 50).</summary>
    public int RateLimit { get; set; } = 50;

    /// <summary>The per-conversation rate window (1 minute-1 day, default 1 hour).</summary>
    public TimeSpan RateWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Append an <c>automation.applied</c> marker after each rule execution (requires <c>conversation:manage</c>). Default true.</summary>
    public bool AppendMarkers { get; set; } = true;

    /// <summary>Append an <c>automation.macro-applied</c> marker after each macro run when the agent may (best effort). Default true.</summary>
    public bool AppendMacroMarkers { get; set; } = true;

    /// <summary>Scheduled rules claimed per scheduler run (1-100, default 20).</summary>
    public int ScheduleBatchSize { get; set; } = 20;

    /// <summary>Schedule claim lease (default 5 minutes, at most 1 hour).</summary>
    public TimeSpan ScheduleLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay before a failed scheduled run is retried (default 1 minute).</summary>
    public TimeSpan ScheduleRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Open conversations of a contact a <c>ContactUpdated</c> trigger fans out to (1-100, default 25).</summary>
    public int MaximumContactConversations { get; set; } = 25;

    /// <summary>Exact webhook hosts allowed (empty = any public host).</summary>
    public IList<string> AllowedWebhookHosts { get; } = [];

    /// <summary>Webhook ports allowed (default 443).</summary>
    public IList<int> AllowedWebhookPorts { get; } = [443];

    /// <summary>Webhook request timeout (1 second-2 minutes, default 10 seconds).</summary>
    public TimeSpan WebhookTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (ClientId == default || MaximumChainDepth is < 1 or > 8 || RateLimit is < 1 or > 1000 || RateWindow < TimeSpan.FromMinutes(1) || RateWindow > TimeSpan.FromDays(1)
            || ScheduleBatchSize is < 1 or > IAutomationRuleStore.MaximumClaim || ScheduleLease <= TimeSpan.Zero || ScheduleLease > IAutomationRuleStore.MaximumLease
            || ScheduleRetryDelay <= TimeSpan.Zero || MaximumContactConversations is < 1 or > 100 || WebhookTimeout < TimeSpan.FromSeconds(1)
            || WebhookTimeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Automation options are invalid.");
    }
}
