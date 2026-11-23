using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>
/// Access-checked macros. Shared macros are visible to every principal with <c>macro:run</c> and managed with <c>macro:manage</c>;
/// personal macros are visible to and managed by their owner (a user principal) with <c>macro:run</c> — another user's personal macro
/// is reported as not found. <see cref="RunAsync"/> checks existence, then <c>macro:run</c> on the live conversation resource, then
/// executes each action as the agent through <see cref="IConversationService"/> (so every action needs the agent's own permission),
/// stopping at the first refused or failed action, and appends a best-effort <c>automation.macro-applied</c> marker.
/// </summary>
public sealed class MacroService(IMacroStore macros, IAccessEvaluator evaluator, IConversationStore conversations, IConversationService conversationService,
    AutomationContextBuilder contexts, AutomationActionExecutor executor, IOptions<AutomationOptions> options, IAutomationAgentDirectory? agents = null) : IMacroService
{
    private readonly IMacroStore macros = macros ?? throw new ArgumentNullException(nameof(macros));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IConversationService conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
    private readonly AutomationContextBuilder contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    private readonly AutomationActionExecutor executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly AutomationOptions options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public async Task<AutomationResult<StoredMacro>> GetAsync(PrincipalRef actor, MacroId id, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.MacroRun, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return await macros.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } stored && Visible(actor, stored.Macro)
            ? new AutomationValue<StoredMacro>(stored)
            : AutomationNotFound.Instance;
    }

    public async Task<AutomationResult<IReadOnlyList<StoredMacro>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.MacroRun, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        UserId? owner = actor.Value is UserPrincipal user ? user.UserId : null;
        var visible = await macros.ListVisibleAsync(scope, owner, cancellationToken).ConfigureAwait(false);
        return new AutomationValue<IReadOnlyList<StoredMacro>>(visible);
    }

    public async Task<AutomationResult<StoredMacro>> CreateAsync(PrincipalRef actor, Macro macro, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(macro);
        if (!await CanManageAsync(actor, macro, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return AutomationGuards.Map(await macros.CreateAsync(scope, macro, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AutomationResult<StoredMacro>> UpdateAsync(PrincipalRef actor, Macro macro, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(macro);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var current = await macros.GetAsync(scope, macro.Id, cancellationToken).ConfigureAwait(false);
        if (current is null || !Visible(actor, current.Macro))
            return AutomationNotFound.Instance;
        if (current.Revision != expectedRevision)
            return new AutomationConflict(current.Revision);
        if (!await CanManageAsync(actor, current.Macro, cancellationToken).ConfigureAwait(false) || !await CanManageAsync(actor, macro, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return AutomationGuards.Map(await macros.UpdateAsync(scope, macro, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AutomationResult<StoredMacro>> DeleteAsync(PrincipalRef actor, MacroId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var current = await macros.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (current is null || !Visible(actor, current.Macro))
            return AutomationNotFound.Instance;
        if (current.Revision != expectedRevision)
            return new AutomationConflict(current.Revision);
        if (!await CanManageAsync(actor, current.Macro, cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        return AutomationGuards.Map(await macros.DeleteAsync(scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<AutomationResult<MacroRunResult>> RunAsync(PrincipalRef actor, MacroId id, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        if (conversation == default)
            throw new ArgumentException("A conversation is required.", nameof(conversation));
        var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return AutomationNotFound.Instance;
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.MacroRun, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
            return AutomationRefused.Forbidden;
        var macro = await macros.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (macro is null || !Visible(actor, macro.Macro))
            return AutomationNotFound.Instance;
        var needs = AutomationContextNeedsHelper.ForActions(macro.Macro.Actions);
        var (context, failure) = await contexts.BuildInternalAsync(actor, conversation, AutomationEvent.ConversationUpdated(ConversationUpdateField.Status), null, needs, cache: null, cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
            return failure == AutomationContextFailure.Forbidden ? AutomationRefused.Forbidden : AutomationNotFound.Instance;
        var agentName = (needs & AutomationContextNeeds.AgentName) != 0 && actor.Value is UserPrincipal user && agents is not null
            ? await agents.GetDisplayNameAsync(scope, user.UserId, cancellationToken).ConfigureAwait(false)
            : null;
        var run = Guid.NewGuid();
        var outcomes = new List<AutomationActionOutcome>(macro.Macro.Actions.Count);
        var completed = true;
        var conversationDirty = false;
        for (var index = 0; index < macro.Macro.Actions.Count; index++)
        {
            var action = macro.Macro.Actions[index];
            if (conversationDirty && AutomationContextNeedsHelper.ActionReadsMutableConversationState(action))
            {
                if (await contexts.RefreshConversationAsync(actor, context, cancellationToken).ConfigureAwait(false) is { } refreshed)
                    context = refreshed;
                conversationDirty = false;
            }
            var outcome = await executor.ExecuteAsync(actor, new AutomationActionRequest(action, context, $"macro-{run:N}-{index}", agentName), cancellationToken)
                .ConfigureAwait(false);
            outcomes.Add(outcome);
            if (outcome.Value is AutomationActionApplied { Sequences.Count: > 0 })
                conversationDirty = true;
            if (outcome.Value is AutomationActionRefused or AutomationActionFailed)
            {
                completed = false;
                break;
            }
        }
        if (options.AppendMacroMarkers)
            _ = await conversationService.AppendExtensionAsync(actor, conversation, AutomationEventCodec.MacroApplied(id, run, outcomes), cancellationToken: cancellationToken).ConfigureAwait(false);
        return new AutomationValue<MacroRunResult>(new MacroRunResult(id, conversation, run, outcomes, completed));
    }

    private static bool Visible(PrincipalRef actor, Macro macro) =>
        macro.Visibility == MacroVisibility.Shared || (actor.Value is UserPrincipal user && macro.Owner == user.UserId);

    private async Task<bool> CanManageAsync(PrincipalRef actor, Macro macro, CancellationToken cancellationToken) => macro.Visibility switch
    {
        MacroVisibility.Shared => await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.MacroManage, cancellationToken).ConfigureAwait(false),
        MacroVisibility.Personal => actor.Value is UserPrincipal user && macro.Owner == user.UserId
            && await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.MacroRun, cancellationToken).ConfigureAwait(false),
        _ => false,
    };

    private static void RequireId(MacroId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A macro identifier is required.", nameof(id));
    }
}
