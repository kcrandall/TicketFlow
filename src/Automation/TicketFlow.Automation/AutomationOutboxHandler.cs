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
        var compiled = await cache.GetCompiledAsync(rules, change.Scope, cancellationToken).ConfigureAwait(false);
        if (!compiled.HasEventDrivenRules)
            return;
        if (change.Kind == ConversationChangeKind.Started)
        {
            var createdRules = compiled.ForEvent(AutomationEventKind.ConversationCreated);
            if (createdRules.Length > 0)
                await runner.RunCompiledAsync(new AutomationOccurrence(change.Scope, change.Conversation, AutomationEvent.ConversationCreated(), AutomationTriggerKeys.Started,
                    false, null, null), createdRules, cache: null, cancellationToken).ConfigureAwait(false);
        }
        long first = long.MaxValue, last = 0;
        HashSet<long>? wanted = null;
        for (var i = 0; i < change.Events.Count; i++)
        {
            var summary = change.Events[i];
            if (summary.Kind is ConversationEventKind.Note or ConversationEventKind.System or ConversationEventKind.Contact)
                continue;
            (wanted ??= []).Add(summary.Sequence);
            if (summary.Sequence < first)
                first = summary.Sequence;
            if (summary.Sequence > last)
                last = summary.Sequence;
        }
        if (wanted is null)
            return;
        // One read covers the change's (contiguous, at most 16) appended events.
        var timeline = await conversations.ReadTimelineAsync(change.Scope, change.Conversation, first - 1, (int)(last - first + 1), includeNotes: false, cancellationToken)
            .ConfigureAwait(false);
        for (var i = 0; i < timeline.Count; i++)
        {
            var timelineEvent = timeline[i];
            if (!wanted.Contains(timelineEvent.Sequence))
                continue;
            var (derived, message) = AutomationEventDerivation.Derive(timelineEvent, options.SlaWorkerClientId);
            if (derived.Count == 0)
                continue;
            var anyCandidates = false;
            for (var j = 0; j < derived.Count; j++)
                if (compiled.ForEvent(derived[j].Kind).Length > 0)
                {
                    anyCandidates = true;
                    break;
                }
            if (!anyCandidates)
                continue;
            var causedByAutomation = AutomationEventDerivation.IsAutomationActor(timelineEvent.Actor, options.ClientId);
            var cause = causedByAutomation
                ? await executions.FindCauseAsync(change.Scope, change.Conversation, timelineEvent.Sequence, cancellationToken).ConfigureAwait(false)
                : null;
            var triggerKey = AutomationTriggerKeys.TimelineEvent(timelineEvent.Sequence);
            for (var j = 0; j < derived.Count; j++)
            {
                var automationEvent = derived[j];
                var candidates = compiled.ForEvent(automationEvent.Kind);
                if (candidates.Length > 0)
                    await runner.RunCompiledAsync(new AutomationOccurrence(change.Scope, change.Conversation, automationEvent, triggerKey,
                        causedByAutomation, cause, message), candidates, cache: null, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
