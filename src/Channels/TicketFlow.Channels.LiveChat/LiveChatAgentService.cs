using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Channels.LiveChat;

/// <summary>Default <see cref="ILiveChatAgentService"/>: masks server-side; a denied <c>contact:read</c> masks every personal field.</summary>
public sealed class LiveChatAgentService(IConversationStore conversations, IContactStore contacts, ILiveChatSessionStore sessions, ILiveChatWidgetStore widgets,
    IAccessEvaluator evaluator, IAccessFieldPolicyStore policies) : ILiveChatAgentService
{
    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IContactStore contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
    private readonly ILiveChatSessionStore sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly ILiveChatWidgetStore widgets = widgets ?? throw new ArgumentNullException(nameof(widgets));
    private readonly IAccessEvaluator evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly IAccessFieldPolicyStore policies = policies ?? throw new ArgumentNullException(nameof(policies));

    public async Task<LiveChatResult<LiveChatVisitorView>> GetVisitorAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        if (actor.Value is null)
            throw new ArgumentException("A principal is required.", nameof(actor));
        var scope = actor.Scope;
        var stored = conversation == default ? null : await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return LiveChatRefused.Of(LiveChatRefusal.NotFound);
        if (!(await evaluator.AuthorizeAsync(new AccessRequest(actor, ConversationPermissions.Read, ConversationAccess.Resource(stored)), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return LiveChatRefused.Of(LiveChatRefusal.Forbidden);
        var sessionIds = await sessions.FindSessionsAsync(scope, conversation, 1, cancellationToken).ConfigureAwait(false);
        if (sessionIds.Count == 0 || await sessions.GetAsync(AccessPartition.From(scope), sessionIds[0], cancellationToken).ConfigureAwait(false) is not { } found || found.Scope != scope)
            return LiveChatRefused.Of(LiveChatRefusal.NotFound);
        var session = found.Session;

        var contact = await contacts.GetAsync(scope, stored.Conversation.Contact, cancellationToken).ConfigureAwait(false);
        AccessDecision decision = contact is null
            ? new AccessDenied(ContactPermissions.Read, AccessDenialReason.NoMatchingGrant)
            : await evaluator.AuthorizeAsync(new AccessRequest(actor, ContactPermissions.Read, ContactAccess.Resource(contact)), cancellationToken).ConfigureAwait(false);
        var masked = new SortedDictionary<FieldPath, bool>();
        var decided = new Dictionary<FieldPath, bool>();
        var maskAll = false;
        switch (decision)
        {
            case AccessAllowed:
                break;
            case AccessMasked partial:
                foreach (var field in partial.Fields)
                    decided[field.Field] = field.Revealable;
                break;
            case AccessDenied:
                maskAll = true;
                break;
        }
        var policyFields = (await policies.ListAsync(scope, cancellationToken).ConfigureAwait(false))
            .Where(policy => policy.Policy.AppliesTo.Contains(ContactPermissions.Read)).Select(policy => policy.Policy.Field).ToHashSet();
        bool IsMasked(FieldPath field, bool failClosed)
        {
            if (decided.TryGetValue(field, out var revealable))
            {
                masked[field] = revealable;
                return true;
            }
            if (!maskAll && !failClosed)
                return false;
            masked[field] = false;
            return true;
        }

        var widget = (await widgets.GetAsync(scope, session.ConfigurationId, cancellationToken).ConfigureAwait(false))?.Widget;
        var answers = new List<LiveChatPrechatAnswerView>();
        foreach (var (key, value) in session.Prechat)
        {
            var field = widget?.PrechatFields.FirstOrDefault(item => item.Key == key);
            var hide = field?.Kind switch
            {
                PrechatFieldKind.Email => IsMasked(ContactFieldPaths.Email, failClosed: false),
                PrechatFieldKind.Name => maskAll,
                _ => field is null || IsMasked(ContactFieldPaths.Attribute(key), failClosed: field.Sensitive && !policyFields.Contains(ContactFieldPaths.Attribute(key))),
            };
            answers.Add(new LiveChatPrechatAnswerView(key, field?.Label ?? key, hide ? null : value, hide));
        }
        var identityHidden = session.Identity is not null && IsMasked(ContactFieldPaths.ExternalId, failClosed: false);
        return new LiveChatValue<LiveChatVisitorView>(new LiveChatVisitorView(session.VisitorId, session.Identity is not null, session.Identity?.Namespace,
            identityHidden ? null : session.Identity?.Value, maskAll ? null : session.DisplayName, answers, session.CreatedAt,
            [.. masked.Select(pair => new MaskedField(pair.Key, pair.Value))]));
    }
}
