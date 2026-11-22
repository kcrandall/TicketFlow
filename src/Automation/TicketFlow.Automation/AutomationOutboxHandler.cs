using Microsoft.Extensions.Options;
using TicketFlow.Conversations;

namespace TicketFlow.Automation;

/// <summary>
/// The automation consumer of the conversation outbox (consumer <c>automation</c>). For each change it loads the workspace's enabled
/// event-driven rules once, derives trigger events from the appended timeline events (read through the trusted store for their actor
/// and payload) — <c>Started</c> also raises <see cref="AutomationEventKind.ConversationCreated"/> — determines whether automation caused
/// each event (actor = the automation API client; cause looked up in the execution store's cause index) and runs
/// <see cref="AutomationRunner"/> with the trigger key <c>evt:&lt;sequence&gt;</c>. Delivery updates and deletions are ignored.
/// Idempotent: execution records are unique per (rule, conversation, trigger key) and resume after a crash.
/// </summary>
public sealed class AutomationOutboxHandler : IConversationOutboxHandler
{
    private readonly IConversationStore conversations;
    private readonly IAutomationRuleStore rules;
    private readonly IAutomationExecutionStore executions;
    private readonly AutomationRunner runner;
    private readonly AutomationRuleCache cache;
    private readonly AutomationOptions options;

    public AutomationOutboxHandler(IConversationStore conversations, IAutomationRuleStore rules, IAutomationExecutionStore executions, AutomationRunner runner,
        AutomationRuleCache cache, IOptions<AutomationOptions> options)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.executions = executions ?? throw new ArgumentNullException(nameof(executions));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
    }

    public string Consumer => AutomationOptions.Consumer;

    public async Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var change = entry.Change;
        if (change.Kind is ConversationChangeKind.DeliveryUpdated or ConversationChangeKind.Deleted)
            return;
        var eventRules = (await cache.GetAsync(rules, change.Scope, cancellationToken).ConfigureAwait(false))
            .Where(rule => rule.Enabled && rule.Trigger.Value is not (ScheduledTrigger or ContactUpdatedTrigger))
            .ToList();
        if (eventRules.Count == 0)
            return;
        if (change.Kind == ConversationChangeKind.Started)
            await runner.RunAsync(new AutomationOccurrence(change.Scope, change.Conversation, AutomationEvent.ConversationCreated(), AutomationTriggerKeys.Started,
                false, null, null), eventRules, cancellationToken).ConfigureAwait(false);
        var wanted = change.Events.Where(summary => summary.Kind is not (ConversationEventKind.Note or ConversationEventKind.System or ConversationEventKind.Contact))
            .Select(summary => summary.Sequence).ToHashSet();
        if (wanted.Count == 0)
            return;
        // One read covers the change's (contiguous, at most 16) appended events.
        var first = wanted.Min();
        var timeline = await conversations.ReadTimelineAsync(change.Scope, change.Conversation, first - 1, (int)(wanted.Max() - first + 1), includeNotes: false, cancellationToken)
            .ConfigureAwait(false);
        foreach (var timelineEvent in timeline.Where(item => wanted.Contains(item.Sequence)).OrderBy(item => item.Sequence))
        {
            var (derived, message) = AutomationEventDerivation.Derive(timelineEvent, options.SlaWorkerClientId);
            if (derived.Count == 0)
                continue;
            var causedByAutomation = AutomationEventDerivation.IsAutomationActor(timelineEvent.Actor, options.ClientId);
            var cause = causedByAutomation
                ? await executions.FindCauseAsync(change.Scope, change.Conversation, timelineEvent.Sequence, cancellationToken).ConfigureAwait(false)
                : null;
            foreach (var automationEvent in derived)
                await runner.RunAsync(new AutomationOccurrence(change.Scope, change.Conversation, automationEvent, AutomationTriggerKeys.TimelineEvent(timelineEvent.Sequence),
                    causedByAutomation, cause, message), eventRules, cancellationToken).ConfigureAwait(false);
        }
    }
}
