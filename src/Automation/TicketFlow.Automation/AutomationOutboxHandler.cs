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
    private readonly AutomationOptions options;

    public AutomationOutboxHandler(IConversationStore conversations, IAutomationRuleStore rules, IAutomationExecutionStore executions, AutomationRunner runner,
        IOptions<AutomationOptions> options)
    {
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
        var eventRules = (await rules.ListAsync(change.Scope, cancellationToken).ConfigureAwait(false))
            .Select(stored => stored.Rule)
            .Where(rule => rule.Enabled && rule.Trigger.Value is not (ScheduledTrigger or ContactUpdatedTrigger))
            .ToList();
        if (eventRules.Count == 0)
            return;
        if (change.Kind == ConversationChangeKind.Started)
            await runner.RunAsync(new AutomationOccurrence(change.Scope, change.Conversation, AutomationEvent.ConversationCreated(), AutomationTriggerKeys.Started,
                false, null, null), eventRules, cancellationToken).ConfigureAwait(false);
        foreach (var summary in change.Events)
        {
            if (summary.Kind is ConversationEventKind.Note or ConversationEventKind.System or ConversationEventKind.Contact)
                continue;
            var read = await conversations.ReadTimelineAsync(change.Scope, change.Conversation, summary.Sequence - 1, 1, includeNotes: false, cancellationToken).ConfigureAwait(false);
            if (read.Count != 1 || read[0].Sequence != summary.Sequence)
                continue;
            var timelineEvent = read[0];
            var (derived, message) = AutomationEventDerivation.Derive(timelineEvent);
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
