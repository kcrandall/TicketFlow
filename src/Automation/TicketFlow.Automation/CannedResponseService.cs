using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>
/// Access-checked canned responses. Reading and listing require <c>canned-response:manage</c> at workspace level or
/// <c>conversation:reply</c> somewhere in the workspace (the planned reply filter is not deny-all, so team- and inbox-scoped agents
/// qualify); writes require <c>canned-response:manage</c>. <see cref="RenderAsync"/> requires <c>conversation:reply</c> on the
/// conversation and renders from the caller's masked conversation and contact views, so masked values are never substituted.
/// </summary>
public sealed class CannedResponseService(ICannedResponseStore responses, IAccessEvaluator evaluator, IAccessQueryPlanner planner, IConversationStore conversations,
    AutomationContextBuilder contexts, IAutomationAgentDirectory? agents = null) : ICannedResponseService
{
    private readonly ICannedResponseStore responses = responses ?? throw new ArgumentNullException(nameof(responses));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly IAccessQueryPlanner planner = planner ?? throw new ArgumentNullException(nameof(planner));
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly AutomationContextBuilder contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));

    public async Task<TicketResult<StoredCannedResponse>> GetAsync(PrincipalRef actor, CannedResponseId id, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        if (!await CanReadAsync(actor, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return await responses.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is { } stored
            ? new TicketValue<StoredCannedResponse>(stored)
            : TicketFailure.NotFound;
    }

    public async Task<TicketResult<StoredCannedResponse>> FindByShortcutAsync(PrincipalRef actor, string shortcut, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(shortcut);
        if (!await CanReadAsync(actor, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        if (shortcut.Length is 0 or > CannedResponse.MaximumShortcutLength)
            return TicketFailure.NotFound;
        return await responses.FindByShortcutAsync(scope, shortcut, cancellationToken).ConfigureAwait(false) is { } stored
            ? new TicketValue<StoredCannedResponse>(stored)
            : TicketFailure.NotFound;
    }

    public async Task<TicketResult<CannedResponsePage>> ListAsync(PrincipalRef actor, CannedResponseQuery query, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        if (!await CanReadAsync(actor, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return new TicketValue<CannedResponsePage>(await responses.ListAsync(scope, query, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredCannedResponse>> CreateAsync(PrincipalRef actor, CannedResponse response, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(response);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.CannedResponseManage, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return AutomationGuards.Map(await responses.CreateAsync(scope, response, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredCannedResponse>> UpdateAsync(PrincipalRef actor, CannedResponse response, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.CannedResponseManage, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return AutomationGuards.Map(await responses.UpdateAsync(scope, response, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredCannedResponse>> DeleteAsync(PrincipalRef actor, CannedResponseId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.CannedResponseManage, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return AutomationGuards.Map(await responses.DeleteAsync(scope, id, expectedRevision, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<RenderedCannedResponse>> RenderAsync(PrincipalRef actor, CannedResponseId id, ConversationId conversation, string? locale = null,
        CancellationToken cancellationToken = default)
    {
        var scope = AutomationGuards.RequireActor(actor);
        RequireId(id);
        if (conversation == default)
            throw new ArgumentException("A conversation is required.", nameof(conversation));
        if (locale is not null)
        {
            try
            {
                CannedResponseVariant.RequireLocale(locale, nameof(locale));
            }
            catch (ArgumentException)
            {
                return TicketFailure.Invalid("The locale is invalid.");
            }
        }
        var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return TicketFailure.NotFound;
        if (!await AutomationGuards.PermittedAsync(evaluator, actor, AccessPermissions.ConversationReply, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        if (await responses.GetAsync(scope, id, cancellationToken).ConfigureAwait(false) is not { } response)
            return TicketFailure.NotFound;
        var needs = AutomationContextNeedsHelper.ForCannedResponse(response.Response, needsContactLocale: locale is null);
        var (context, failure) = await contexts.BuildInternalAsync(actor, conversation, AutomationEvent.MessageSent(), null, needs, cache: null, cancellationToken)
            .ConfigureAwait(false);
        if (context is null)
            return failure == AutomationContextFailure.Forbidden ? TicketFailure.Forbidden : TicketFailure.NotFound;
        var agentName = (needs & AutomationContextNeeds.AgentName) != TicketFlow.Automation.AutomationContextNeeds.None && actor is UserPrincipal user && agents is not null
            ? await agents.GetDisplayNameAsync(scope, user.UserId, cancellationToken).ConfigureAwait(false)
            : null;
        var variant = response.Response.Select(locale ?? context.Contact?.Locale);
        var body = variant.Template.Render(AutomationTemplateValues.Resolver(context, agentName), response.Response.Escaping);
        if (body is null)
            return TicketFailure.Invalid("The rendered response exceeds the output limit.");
        return new TicketValue<RenderedCannedResponse>(new RenderedCannedResponse(id, variant.Locale, response.Response.ContentType, body));
    }

    private async Task<bool> CanReadAsync(PrincipalRef actor, CancellationToken cancellationToken)
    {
        if (await AutomationGuards.PermittedAsync(evaluator, actor, AutomationPermissions.CannedResponseManage, cancellationToken).ConfigureAwait(false))
            return true;
        var plan = await planner.PlanAsync(new AccessQuery(actor, AccessPermissions.ConversationReply, actor.Scope), cancellationToken).ConfigureAwait(false);
        return plan is not DenyAllFilter;
    }

    private static void RequireId(CannedResponseId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A canned response identifier is required.", nameof(id));
    }
}
