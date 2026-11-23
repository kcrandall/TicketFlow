using Microsoft.Extensions.Options;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>One triggering occurrence: the conversation, the event, its idempotency key and its automation cause (when automation caused it).</summary>
public sealed record AutomationOccurrence(
    TicketScope Scope,
    ConversationId Conversation,
    AutomationEvent Event,
    string TriggerKey,
    bool CausedByAutomation,
    AutomationCause? Cause,
    AutomationMessageFacts? Message)
{
    public override string ToString() => $"AutomationOccurrence {{ Conversation = {Conversation}, Event = {Event.Kind}, TriggerKey = {TriggerKey} }}";
}

/// <summary>Counts from one run over an occurrence.</summary>
public sealed record AutomationRunResult(int Executed, int Suppressed, int Skipped);

/// <summary>
/// The rule engine core shared by the outbox handler, scheduler and contact follower. For one occurrence it evaluates the supplied
/// rules in order (priority, then id) as the automation API client and, for each rule whose trigger fires and whose condition is true:
/// <list type="number">
/// <item>skips events caused by automation unless the rule opts in (<see cref="AutomationRule.RunOnAutomationEvents"/>);</item>
/// <item>records a <see cref="AutomationExecutionStatus.Suppressed"/> execution when the chain depth exceeds
/// <see cref="AutomationOptions.MaximumChainDepth"/> (an automation-caused event without a recorded cause counts as maximum depth) or the
/// conversation exceeded <see cref="AutomationOptions.RateLimit"/> executions within <see cref="AutomationOptions.RateWindow"/>;</item>
/// <item>otherwise begins (or resumes) the idempotent execution record for (rule, conversation, trigger key), executes the remaining
/// actions through <see cref="AutomationActionExecutor"/>, persisting each outcome (and the produced sequences into the cause index)
/// before the next action, appends the <c>automation.applied</c> marker and completes the record.</item>
/// </list>
/// Execution records of every firing rule are fetched in one query and the evaluation context is built once per occurrence, rebuilt
/// only after a rule ran actions (so later rules see earlier rules' changes). A finished record is never re-run; a record left
/// <see cref="AutomationExecutionStatus.Running"/> is taken over compare-and-set before it is resumed, and a record a concurrent runner just
/// created is left to that runner, so two runners never execute the same record.
/// </summary>
public sealed class AutomationRunner
{
    private const int MarkerScan = 32;

    private readonly AutomationContextBuilder contexts;
    private readonly AutomationActionExecutor executor;
    private readonly IAutomationExecutionStore executions;
    private readonly IConversationService conversations;
    private readonly IConversationStore store;
    private readonly AutomationOptions options;
    private readonly TimeProvider time;
    private readonly IAutomationAgentDirectory? agents;

    public AutomationRunner(AutomationContextBuilder contexts, AutomationActionExecutor executor, IAutomationExecutionStore executions, IConversationService conversations,
        IConversationStore store, IOptions<AutomationOptions> options, TimeProvider time, IAutomationAgentDirectory? agents = null)
    {
        this.contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.executions = executions ?? throw new ArgumentNullException(nameof(executions));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.agents = agents;
    }

    public PrincipalRef Principal(TicketScope scope) => new ApiClientPrincipal(scope, options.ClientId);

    public Task<AutomationRunResult> RunAsync(AutomationOccurrence occurrence, IEnumerable<AutomationRule> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(rules);
        var ordered = Order(rules);
        var firing = new List<AutomationRule>();
        foreach (var rule in ordered)
            if (rule.Enabled && occurrence.Event.Fires(rule.Trigger) && (!occurrence.CausedByAutomation || rule.RunOnAutomationEvents))
                firing.Add(rule);
        return RunFiringAsync(occurrence, firing, cache: null, cancellationToken);
    }

    internal Task<AutomationRunResult> RunCompiledAsync(AutomationOccurrence occurrence, IReadOnlyList<AutomationRule> orderedEnabledCandidates, AutomationContextCache? cache,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var firing = new List<AutomationRule>(orderedEnabledCandidates.Count);
        for (var i = 0; i < orderedEnabledCandidates.Count; i++)
        {
            var rule = orderedEnabledCandidates[i];
            if (rule.Enabled && occurrence.Event.Fires(rule.Trigger) && (!occurrence.CausedByAutomation || rule.RunOnAutomationEvents))
                firing.Add(rule);
        }
        return RunFiringAsync(occurrence, firing, cache, cancellationToken);
    }

    private async Task<AutomationRunResult> RunFiringAsync(AutomationOccurrence occurrence, List<AutomationRule> firing, AutomationContextCache? cache,
        CancellationToken cancellationToken)
    {
        if (firing.Count == 0)
            return new AutomationRunResult(0, 0, 0);
        int executed = 0, suppressed = 0, skipped = 0;
        var actor = Principal(occurrence.Scope);
        var depth = occurrence.CausedByAutomation ? (occurrence.Cause?.Depth ?? options.MaximumChainDepth) + 1 : 1;
        var ids = new AutomationRuleId[firing.Count];
        var combinedNeeds = AutomationContextNeeds.None;
        for (var i = 0; i < firing.Count; i++)
        {
            ids[i] = firing[i].Id;
            combinedNeeds |= AutomationContextNeedsHelper.ForRule(firing[i]);
        }
        // One query for every firing rule's execution record and one context per occurrence (rebuilt only after a rule ran actions).
        var records = await executions.FindManyAsync(occurrence.Scope, occurrence.Conversation, occurrence.TriggerKey, ids, cancellationToken)
            .ConfigureAwait(false);
        AutomationContext? context = null;
        for (var i = 0; i < firing.Count; i++)
        {
            var rule = firing[i];
            cancellationToken.ThrowIfCancellationRequested();
            var existing = records.GetValueOrDefault(rule.Id);
            if (existing is { Execution.Status: not AutomationExecutionStatus.Running })
            {
                skipped++;
                if (rule.StopProcessing && existing.Execution.Status == AutomationExecutionStatus.Completed)
                    break;
                continue;
            }
            if (context is null)
            {
                var (built, failure) = await contexts.BuildInternalAsync(actor, occurrence.Conversation, occurrence.Event, occurrence.Message, combinedNeeds, cache, cancellationToken)
                    .ConfigureAwait(false);
                if (built is null)
                    return new AutomationRunResult(executed, suppressed, skipped + (failure is null ? 0 : 1));
                context = built;
            }
            StoredAutomationExecution record;
            var resumed = existing is not null;
            if (existing is not null)
            {
                // A record left Running (crash, or a lost race): take it over compare-and-set so only one runner resumes it.
                var claimed = await SaveProgressAsync(occurrence.Scope, existing.Execution, existing.Revision, [], cancellationToken).ConfigureAwait(false);
                if (claimed is null)
                {
                    skipped++;
                    continue;
                }
                record = existing with { Revision = claimed.Value };
            }
            else
            {
                if (!AutomationConditionEvaluator.Matches(rule.Condition, context))
                    continue;
                var now = time.GetUtcNow();
                AutomationSuppression? suppression = depth > options.MaximumChainDepth ? AutomationSuppression.DepthExceeded
                    : await executions.CountSinceAsync(occurrence.Scope, occurrence.Conversation, now - options.RateWindow, options.RateLimit, cancellationToken).ConfigureAwait(false) >= options.RateLimit
                        ? AutomationSuppression.RateLimited : null;
                var execution = suppression is { } reason
                    ? new AutomationExecution(Guid.NewGuid(), rule.Id, occurrence.Conversation, occurrence.TriggerKey, occurrence.Event.Kind, Math.Min(depth, 64),
                        AutomationExecutionStatus.Suppressed, reason, now, now, rule.Actions.Count, [])
                    : new AutomationExecution(Guid.NewGuid(), rule.Id, occurrence.Conversation, occurrence.TriggerKey, occurrence.Event.Kind, depth,
                        AutomationExecutionStatus.Running, null, now, null, rule.Actions.Count, []);
                var begun = await executions.BeginAsync(occurrence.Scope, execution, cancellationToken).ConfigureAwait(false);
                (record, var created) = begun switch
                {
                    AutomationExecutionStarted started => (started.Stored, true),
                    AutomationExecutionExists exists => (exists.Stored, false),
                };
                if (!created)
                {
                    // A concurrent runner created the record and owns it; it is not resumed concurrently.
                    skipped++;
                    if (rule.StopProcessing && record.Execution.Status == AutomationExecutionStatus.Completed)
                        break;
                    continue;
                }
                if (record.Execution.Status == AutomationExecutionStatus.Suppressed)
                {
                    suppressed++;
                    continue;
                }
            }
            if (await ExecuteAsync(actor, rule, record, context, resumed, cancellationToken).ConfigureAwait(false))
                executed++;
            context = null;
            if (rule.StopProcessing)
                break;
        }
        return new AutomationRunResult(executed, suppressed, skipped);
    }

    /// <summary>Runs the remaining actions of a running record; false when a concurrent runner owns the record.</summary>
    private async Task<bool> ExecuteAsync(PrincipalRef actor, AutomationRule rule, StoredAutomationExecution record, AutomationContext context, bool resumed,
        CancellationToken cancellationToken)
    {
        var scope = record.Scope;
        var execution = record.Execution;
        var revision = record.Revision;
        var outcomes = new List<AutomationActionOutcome>(rule.Actions.Count);
        for (var i = 0; i < execution.Outcomes.Count; i++)
            outcomes.Add(execution.Outcomes[i]);
        var actionNeeds = AutomationContextNeedsHelper.ForActions(rule.Actions);
        var agentName = (actionNeeds & AutomationContextNeeds.AgentName) != 0 && context.Conversation.Assignee is { } assignee && agents is not null
            ? await agents.GetDisplayNameAsync(scope, assignee, cancellationToken).ConfigureAwait(false)
            : null;
        var conversationDirty = false;
        for (var index = outcomes.Count; index < rule.Actions.Count && index < execution.ActionCount; index++)
        {
            var action = rule.Actions[index];
            if (conversationDirty && AutomationContextNeedsHelper.ActionReadsMutableConversationState(action))
            {
                if (await contexts.RefreshConversationAsync(actor, context, cancellationToken).ConfigureAwait(false) is { } refreshed)
                    context = refreshed;
                conversationDirty = false;
            }
            var outcome = await executor.ExecuteAsync(actor, new AutomationActionRequest(action, context, $"{execution.Id:N}-{index}", agentName, rule.Id),
                cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);
            execution = execution.WithProgress(outcomes);
            IReadOnlyList<long> newSequences = outcome.Value is AutomationActionApplied applied ? applied.Sequences : [];
            if (newSequences.Count > 0)
                conversationDirty = true;
            var saved = await SaveProgressAsync(scope, execution, revision, newSequences, cancellationToken).ConfigureAwait(false);
            if (saved is null)
                return false;
            revision = saved.Value;
        }
        long? marker = null;
        if (options.AppendMarkers)
            marker = await AppendMarkerAsync(actor, context.Conversation.Id, execution, outcomes, resumed, cancellationToken).ConfigureAwait(false);
        execution = execution.Complete(time.GetUtcNow(), marker);
        IReadOnlyList<long> markerSequences = marker is { } seq ? [seq] : [];
        return await SaveProgressAsync(scope, execution, revision, markerSequences, cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<long?> SaveProgressAsync(TicketScope scope, AutomationExecution execution, long revision, IReadOnlyList<long> newSequences,
        CancellationToken cancellationToken)
    {
        var result = executions is IIncrementalAutomationExecutionStore incremental
            ? await incremental.UpdateProgressAsync(scope, execution, revision, newSequences, cancellationToken).ConfigureAwait(false)
            : await executions.UpdateAsync(scope, execution, revision, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            StoreApplied<StoredAutomationExecution> applied => applied.Value.Revision,
            StoreNotFound or StoreConflict => null,
            StoreRejected<AutomationWriteRejection> rejected => throw new InvalidOperationException($"The execution store rejected the write: {rejected.Reason}."),
        };
    }

    private async Task<long?> AppendMarkerAsync(PrincipalRef actor, ConversationId conversation, AutomationExecution execution, IReadOnlyList<AutomationActionOutcome> outcomes,
        bool resumed, CancellationToken cancellationToken)
    {
        // Only crash-resumed executions could have already appended a marker before completing the record.
        if (resumed)
        {
            var head = (await store.GetAsync(actor.Scope, conversation, cancellationToken).ConfigureAwait(false))?.Conversation.LastSequence ?? 0;
            var recent = await store.ReadTimelineAsync(actor.Scope, conversation, Math.Max(0, head - MarkerScan), MarkerScan, includeNotes: false, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < recent.Count; i++)
                if (recent[i].Event.Value is ExtensionEvent extension && AutomationEventCodec.TryDecodeApplied(extension, out _, out var id, out _) && id == execution.Id)
                    return recent[i].Sequence;
        }
        var result = await conversations.AppendExtensionAsync(actor, conversation, AutomationEventCodec.Applied(execution, outcomes), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.Value is ConversationApplied { Events.Count: > 0 } applied ? applied.Events[0].Sequence : null;
    }

    internal static IEnumerable<AutomationRule> Order(IEnumerable<AutomationRule> rules) =>
        rules.OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id.Value, StringComparer.Ordinal);
}
