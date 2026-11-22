using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>
/// Access-checked rule management (<c>automation:read</c> / <c>automation:manage</c> at workspace level, before the store), dry runs,
/// replays and the execution log. Dry runs and replays build the context as the caller (<c>conversation:read</c>, masked view), never
/// write conversations or execution records, and render would-be replies and notes from what the caller may see.
/// </summary>
public sealed class AutomationRuleService(IAutomationRuleStore rules, IAutomationExecutionStore executions, IAccessEvaluator evaluator, AutomationContextBuilder contexts,
    AutomationActionExecutor executor, IConversationService conversations, Microsoft.Extensions.Options.IOptions<AutomationOptions> options, TimeProvider time) : IAutomationRuleService
{
    private readonly IAutomationRuleStore rules = rules ?? throw new ArgumentNullException(nameof(rules));
    private readonly IAutomationExecutionStore executions = executions ?? throw new ArgumentNullException(nameof(executions));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly AutomationContextBuilder contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    private readonly AutomationActionExecutor executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly IConversationService conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly AutomationOptions options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly TimeProvider time = time ?? throw new ArgumentNullException(nameof(time));

    public async Task<AutomationResult<StoredAutomationRule>> GetAsync(PrincipalRef actor, AutomationRuleId id, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Read, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return await rules.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } stored ? new AutomationValue<StoredAutomationRule>(stored) : AutomationNotFound.Instance;
    }

    public async Task<AutomationResult<IReadOnlyList<StoredAutomationRule>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Read, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return new AutomationValue<IReadOnlyList<StoredAutomationRule>>(await rules.ListAsync(scope, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AutomationResult<StoredAutomationRule>> CreateAsync(PrincipalRef actor, AutomationRule rule, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(rule);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        if (!TryNextRun(rule, out var next, out var error))
            return AutomationRefused.Invalid(error!);
        return AutomationGuards.Map(await rules.CreateAsync(scope, rule, next, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AutomationResult<StoredAutomationRule>> UpdateAsync(PrincipalRef actor, AutomationRule rule, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        if (!TryNextRun(rule, out var next, out var error))
            return AutomationRefused.Invalid(error!);
        return AutomationGuards.Map(await rules.UpdateAsync(scope, rule, expectedRevision, next, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AutomationResult<StoredAutomationRule>> DeleteAsync(PrincipalRef actor, AutomationRuleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Manage, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return AutomationGuards.Map(await rules.DeleteAsync(scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AutomationResult<AutomationDryRunResult>> DryRunAsync(PrincipalRef actor, AutomationDryRunRequest request, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Conversation == default || request.Event is null)
            throw new ArgumentException("A conversation and an event are required.", nameof(request));
        if (request.MessageBody is { Length: > ConversationMessageLimits.MaximumBodyBytes })
            return AutomationRefused.Invalid("The message body is too long.");
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Read, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        var message = request.MessageBody is { } body
            ? new AutomationMessageFacts(request.Event.Kind == AutomationEventKind.MessageSent ? MessageDirection.Outgoing : MessageDirection.Incoming, body)
            : null;
        var (context, failure) = await contexts.BuildAsync(actor, request.Conversation, request.Event, message, cancellationToken).ConfigureAwait(false);
        if (context is null)
            return failure == AutomationContextFailure.Forbidden ? AutomationRefused.Forbidden : AutomationNotFound.Instance;
        IReadOnlyList<AutomationRule> candidates = request.Draft is { } draft ? [draft] : await SelectAsync(scope, request.Rules, cancellationToken).ConfigureAwait(false);
        var (previews, stopped) = await PreviewAsync(scope, candidates, context, includeDisabledDraft: request.Draft is not null, cancellationToken).ConfigureAwait(false);
        return new AutomationValue<AutomationDryRunResult>(new AutomationDryRunResult(request.Conversation, previews, stopped, context.Now));
    }

    public async Task<AutomationResult<AutomationReplayResult>> ReplayAsync(PrincipalRef actor, AutomationReplayRequest request, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Read, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        var candidates = await SelectAsync(scope, request.Rules, cancellationToken).ConfigureAwait(false);
        var items = new List<AutomationReplayItem>();
        var scanned = 0;
        var truncated = false;
        long after = 0;
        while (true)
        {
            var page = await conversations.ReadTimelineAsync(actor, request.Conversation, after, 200, cancellationToken).ConfigureAwait(false);
            var timeline = page switch
            {
                TimelinePage found => found,
                ConversationNotFound or ConversationRefused => null,
            };
            if (timeline is null)
                return page.Value is ConversationRefused ? AutomationRefused.Forbidden : AutomationNotFound.Instance;
            foreach (var view in timeline.Events)
            {
                if (++scanned > AutomationReplayRequest.MaximumEvents)
                {
                    truncated = true;
                    break;
                }
                var timelineEvent = view.Event;
                if (timelineEvent.OccurredAt < request.From || timelineEvent.OccurredAt >= request.To)
                    continue;
                var (derived, message) = AutomationEventDerivation.Derive(timelineEvent);
                if (derived.Count == 0)
                    continue;
                var cause = AutomationEventDerivation.IsAutomationActor(timelineEvent.Actor, options.ClientId)
                    ? await executions.FindCauseAsync(scope, request.Conversation, timelineEvent.Sequence, cancellationToken).ConfigureAwait(false)
                    : null;
                var recorded = new List<StoredAutomationExecution>();
                foreach (var rule in candidates)
                    if (await executions.FindAsync(scope, rule.Id, request.Conversation, AutomationTriggerKeys.TimelineEvent(timelineEvent.Sequence), cancellationToken).ConfigureAwait(false) is { } execution)
                        recorded.Add(execution);
                foreach (var automationEvent in derived)
                {
                    var (context, failure) = await contexts.BuildAsync(actor, request.Conversation, automationEvent, message, cancellationToken).ConfigureAwait(false);
                    if (context is null)
                        return failure == AutomationContextFailure.Forbidden ? AutomationRefused.Forbidden : AutomationNotFound.Instance;
                    var (previews, _) = await PreviewAsync(scope, candidates, context, includeDisabledDraft: false, cancellationToken).ConfigureAwait(false);
                    items.Add(new AutomationReplayItem(timelineEvent.Sequence, timelineEvent.OccurredAt, automationEvent, cause,
                        [.. previews.Where(preview => preview.Match != AutomationMatch.NotTriggered)], recorded));
                }
            }
            if (truncated || timeline.NextAfterSequence is not { } next)
                break;
            after = next;
        }
        return new AutomationValue<AutomationReplayResult>(new AutomationReplayResult(request.Conversation, items, truncated));
    }

    public async Task<AutomationResult<AutomationExecutionPage>> ListExecutionsAsync(PrincipalRef actor, AutomationExecutionQuery query, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.Read, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return new AutomationValue<AutomationExecutionPage>(await executions.ListAsync(scope, query, cancellationToken).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<AutomationRule>> SelectAsync(TicketScope scope, IReadOnlyList<AutomationRuleId>? ids, CancellationToken cancellationToken)
    {
        var all = (await rules.ListAsync(scope, cancellationToken).ConfigureAwait(false)).Select(stored => stored.Rule);
        if (ids is not null)
            all = all.Where(rule => ids.Contains(rule.Id));
        return [.. AutomationRunner.Order(all)];
    }

    private async Task<(IReadOnlyList<AutomationRulePreview> Previews, AutomationRuleId? Stopped)> PreviewAsync(TicketScope scope, IReadOnlyList<AutomationRule> candidates,
        AutomationContext context, bool includeDisabledDraft, CancellationToken cancellationToken)
    {
        var previews = new List<AutomationRulePreview>();
        foreach (var rule in candidates)
        {
            if (!rule.Enabled && !includeDisabledDraft)
            {
                previews.Add(new AutomationRulePreview(rule.Id, rule.Name, AutomationMatch.Disabled, []));
                continue;
            }
            if (!context.Event.Fires(rule.Trigger))
            {
                previews.Add(new AutomationRulePreview(rule.Id, rule.Name, AutomationMatch.NotTriggered, []));
                continue;
            }
            var value = rule.Condition is null ? true : AutomationConditionEvaluator.Evaluate(rule.Condition, context);
            if (value != true)
            {
                previews.Add(new AutomationRulePreview(rule.Id, rule.Name, value is null ? AutomationMatch.Unknown : AutomationMatch.NotMatched, []));
                continue;
            }
            var actions = new List<AutomationActionPreview>();
            for (var index = 0; index < rule.Actions.Count; index++)
                actions.Add(await executor.PreviewAsync(scope, context, rule.Actions[index], index, null, cancellationToken).ConfigureAwait(false));
            previews.Add(new AutomationRulePreview(rule.Id, rule.Name, AutomationMatch.Matched, actions));
            if (rule.StopProcessing)
                return (previews, rule.Id);
        }
        return (previews, null);
    }

    private bool TryNextRun(AutomationRule rule, out DateTimeOffset? next, out string? error)
    {
        next = null;
        error = null;
        if (rule.Schedule is not { } schedule)
            return true;
        try
        {
            next = rule.Enabled ? schedule.NextAfter(time.GetUtcNow()) : null;
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = "The schedule time zone is unknown on this host.";
            return false;
        }
    }

    private static void RequireId(AutomationRuleId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A rule identifier is required.", nameof(id));
    }
}
