using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>Counts from one scheduler run.</summary>
public sealed record AutomationScheduleRunResult(int Fired, int Conversations, int Retried, int Lost);

/// <summary>
/// The cron scheduler: claims due scheduled rules (leased; concurrent schedulers never share a live lease), selects the bounded set of
/// conversations the rule targets (one claim at a time, so each lease covers a single rule's run) — through <see cref="IConversationStore.ListAsync"/> with the automation client's planned
/// <c>conversation:read</c> filter applied inside the query — and runs the rule for each with the trigger key <c>sched:&lt;slot&gt;</c>,
/// so a slot executes at most once per conversation. The next run is the first fire instant after <c>max(slot, now)</c>: missed slots
/// coalesce into one run. A run with failures is released for <see cref="AutomationOptions.ScheduleRetryDelay"/> and retried (finished
/// executions are skipped). No hosted service is included: hosts call <see cref="RunOnceAsync"/> in their own loop.
/// </summary>
public sealed class AutomationScheduler
{
    private readonly IAutomationRuleStore rules;
    private readonly IConversationStore conversations;
    private readonly IAccessQueryPlanner planner;
    private readonly AutomationRunner runner;
    private readonly AutomationOptions options;
    private readonly TimeProvider time;

    public AutomationScheduler(IAutomationRuleStore rules, IConversationStore conversations, IAccessQueryPlanner planner, AutomationRunner runner,
        IOptions<AutomationOptions> options, TimeProvider time)
    {
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<AutomationScheduleRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        int fired = 0, scanned = 0, retried = 0, lost = 0;
        // Claim one rule at a time, so a claim's lease covers only its own run (never the runs of rules queued behind it).
        for (var processed = 0; processed < options.ScheduleBatchSize; processed++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claims = await rules.ClaimDueSchedulesAsync(1, options.ScheduleLease, cancellationToken).ConfigureAwait(false);
            if (claims.Count == 0)
                break;
            var claim = claims[0];
            var stored = claim.Snapshot ?? await rules.GetAsync(claim.Scope, claim.Rule, cancellationToken).ConfigureAwait(false);
            if (stored?.Rule is not { Enabled: true, Trigger.Value: ScheduledTrigger trigger } rule)
            {
                if (!await rules.CompleteScheduleAsync(claim, null, cancellationToken).ConfigureAwait(false))
                    lost++;
                continue;
            }
            var failed = false;
            try
            {
                var selected = await SelectAsync(claim.Scope, trigger, cancellationToken).ConfigureAwait(false);
                var contextCache = selected.Count > 1 ? new AutomationContextCache() : null;
                AutomationRule[] singleRule = [rule];
                var triggerKey = AutomationTriggerKeys.Scheduled(claim.DueAt);
                for (var i = 0; i < selected.Count; i++)
                {
                    scanned++;
                    try
                    {
                        await runner.RunCompiledAsync(new AutomationOccurrence(claim.Scope, selected[i], AutomationEvent.Scheduled(), triggerKey,
                            false, null, null), singleRule, contextCache, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Exception details are not recorded: they may contain customer data. The slot is retried.
                        failed = true;
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed = true;
            }
            var now = time.GetUtcNow();
            if (failed)
            {
                if (await rules.ReleaseScheduleAsync(claim, now + options.ScheduleRetryDelay, cancellationToken).ConfigureAwait(false))
                    retried++;
                else
                    lost++;
                continue;
            }
            DateTimeOffset? next;
            try
            {
                next = trigger.Schedule.NextAfter(claim.DueAt > now ? claim.DueAt : now);
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                next = null;
            }
            if (await rules.CompleteScheduleAsync(claim, next, cancellationToken).ConfigureAwait(false))
                fired++;
            else
                lost++;
        }
        return new AutomationScheduleRunResult(fired, scanned, retried, lost);
    }

    private async Task<IReadOnlyList<ConversationId>> SelectAsync(TicketScope scope, ScheduledTrigger trigger, CancellationToken cancellationToken)
    {
        var filter = await planner.PlanAsync(new AccessQuery(runner.Principal(scope), AccessPermissions.ConversationRead, scope), cancellationToken).ConfigureAwait(false);
        var selected = new List<ConversationId>();
        foreach (var status in trigger.Statuses)
        {
            var query = new ConversationListQuery(inbox: trigger.Inbox, status: status, limit: Math.Min(ConversationListQuery.MaximumLimit, trigger.MaximumConversations));
            while (selected.Count < trigger.MaximumConversations)
            {
                var page = await conversations.ListAsync(scope, query, filter, cancellationToken).ConfigureAwait(false);
                foreach (var stored in page.Conversations)
                {
                    if (selected.Count >= trigger.MaximumConversations)
                        break;
                    selected.Add(stored.Id);
                }
                if (page.Next is null)
                    break;
                query = query.WithAfter(page.Next);
            }
        }
        return selected;
    }
}
