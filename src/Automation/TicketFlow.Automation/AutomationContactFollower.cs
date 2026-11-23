using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>
/// Follows the durable contact change log (<see cref="IContactStore.ReadChangesAsync"/>) for <see cref="ContactUpdatedTrigger"/> rules.
/// <see cref="CatchUpAsync"/> reads changes after the scope's checkpoint (stored compare-and-set in the execution store under
/// <see cref="CheckpointName"/>), and for every <c>Updated</c> change runs the workspace's contact rules on the contact's most recently
/// active non-closed conversations (at most <see cref="AutomationOptions.MaximumContactConversations"/>, filtered by the automation
/// client's <c>conversation:read</c> plan) with the trigger key <c>contact:&lt;sequence&gt;</c>. Idempotent; hosts call it per scope in
/// their loop (E11 hosts it).
/// </summary>
public sealed class AutomationContactFollower
{
    public const string CheckpointName = "contacts";
    public const int MaximumBatch = 500;

    private readonly IContactStore contacts;
    private readonly IConversationStore conversations;
    private readonly IAutomationRuleStore rules;
    private readonly IAutomationExecutionStore executions;
    private readonly IAccessQueryPlanner planner;
    private readonly AutomationRunner runner;
    private readonly AutomationRuleCache cache;
    private readonly AutomationOptions options;

    public AutomationContactFollower(IContactStore contacts, IConversationStore conversations, IAutomationRuleStore rules, IAutomationExecutionStore executions,
        IAccessQueryPlanner planner, AutomationRunner runner, AutomationRuleCache cache, IOptions<AutomationOptions> options)
    {
        this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.rules = rules ?? throw new ArgumentNullException(nameof(rules));
        this.executions = executions ?? throw new ArgumentNullException(nameof(executions));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
    }

    /// <summary>Processes up to <paramref name="limit"/> contact changes of <paramref name="scope"/>; returns the number consumed.</summary>
    public async Task<int> CatchUpAsync(TicketScope scope, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumBatch);
        var checkpoint = await executions.GetCheckpointAsync(scope, CheckpointName, cancellationToken).ConfigureAwait(false);
        var changes = await contacts.ReadChangesAsync(scope, checkpoint ?? 0, limit, cancellationToken).ConfigureAwait(false);
        if (changes.Count == 0)
            return 0;
        var compiled = await cache.GetCompiledAsync(rules, scope, cancellationToken).ConfigureAwait(false);
        var contactRules = compiled.ForEvent(AutomationEventKind.ContactUpdated);
        if (contactRules.Length > 0)
        {
            var filter = await planner.PlanAsync(new AccessQuery(runner.Principal(scope), AccessPermissions.ConversationRead, scope), cancellationToken).ConfigureAwait(false);
            var contextCache = new AutomationContextCache();
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.Event.Kind != ContactChangeKind.Updated)
                    continue;
                var page = await conversations.ListAsync(scope, new ConversationListQuery(contact: change.Event.Contact, limit: options.MaximumContactConversations), filter,
                    cancellationToken).ConfigureAwait(false);
                var triggerKey = AutomationTriggerKeys.ContactChange(change.Sequence);
                for (var j = 0; j < page.Conversations.Count; j++)
                {
                    var stored = page.Conversations[j];
                    if (stored.Conversation.Status.Kind == ConversationStatusKind.Closed)
                        continue;
                    await runner.RunCompiledAsync(new AutomationOccurrence(scope, stored.Id, AutomationEvent.ContactUpdated(), triggerKey,
                        false, null, null), contactRules, contextCache, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        // A lost race means another follower advanced the checkpoint over the same changes (executions are idempotent).
        _ = await executions.SetCheckpointAsync(scope, CheckpointName, changes[^1].Sequence, checkpoint, cancellationToken).ConfigureAwait(false);
        return changes.Count;
    }
}
