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

    public async Task<TicketResult<LiveChatVisitorView, LiveChatRefused>> GetVisitorAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        if (actor is null)
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

        var hasPrechat = session.Prechat.Count > 0;
        var contactTask = contacts.GetAsync(scope, stored.Conversation.Contact, cancellationToken);
        Task<IReadOnlyList<StoredAccessFieldPolicy>>? policiesTask = hasPrechat ? policies.ListAsync(scope, cancellationToken) : null;
        Task<StoredLiveChatWidget?>? widgetTask = hasPrechat ? widgets.GetAsync(scope, session.ConfigurationId, cancellationToken) : null;
        if (hasPrechat)
            await Task.WhenAll(contactTask, policiesTask!, widgetTask!).ConfigureAwait(false);
        var contact = await contactTask.ConfigureAwait(false);
        AccessDecision decision = contact is null
            ? new AccessDenied(ContactPermissions.Read, AccessDenialReason.NoMatchingGrant)
            : await evaluator.AuthorizeAsync(new AccessRequest(actor, ContactPermissions.Read, ContactAccess.Resource(contact)), cancellationToken).ConfigureAwait(false);
        Dictionary<FieldPath, bool>? masked = null;
        Dictionary<FieldPath, bool>? decided = null;
        var maskAll = false;
        switch (decision)
        {
            case AccessAllowed:
                break;
            case AccessMasked partial:
                if (partial.Fields.Count > 0)
                {
                    decided = new Dictionary<FieldPath, bool>(partial.Fields.Count);
                    for (var i = 0; i < partial.Fields.Count; i++)
                        decided[partial.Fields[i].Field] = partial.Fields[i].Revealable;
                }
                break;
            case AccessDenied:
                maskAll = true;
                break;
        }
        bool IsMasked(FieldPath field, bool failClosed)
        {
            if (decided is not null && decided.TryGetValue(field, out var revealable))
            {
                masked ??= [];
                masked[field] = revealable;
                return true;
            }
            if (!maskAll && !failClosed)
                return false;
            masked ??= [];
            masked[field] = false;
            return true;
        }

        IReadOnlyList<LiveChatPrechatAnswerView> answers;
        if (!hasPrechat)
            answers = [];
        else
        {
            var policyList = await policiesTask!.ConfigureAwait(false);
            HashSet<FieldPath>? policyFields = null;
            for (var i = 0; i < policyList.Count; i++)
            {
                if (policyList[i].Policy.AppliesTo.Contains(ContactPermissions.Read))
                {
                    policyFields ??= [];
                    policyFields.Add(policyList[i].Policy.Field);
                }
            }
            var widget = (await widgetTask!.ConfigureAwait(false))?.Widget;
            Dictionary<string, LiveChatPrechatField>? fieldByKey = null;
            if (widget is not null && widget.PrechatFields.Count > 0)
            {
                fieldByKey = new Dictionary<string, LiveChatPrechatField>(widget.PrechatFields.Count, StringComparer.Ordinal);
                for (var i = 0; i < widget.PrechatFields.Count; i++)
                    fieldByKey[widget.PrechatFields[i].Key] = widget.PrechatFields[i];
            }
            var answerList = new List<LiveChatPrechatAnswerView>(session.Prechat.Count);
            foreach (var (key, value) in session.Prechat)
            {
                LiveChatPrechatField? field = null;
                fieldByKey?.TryGetValue(key, out field);
                bool hide;
                if (field?.Kind == PrechatFieldKind.Email)
                    hide = IsMasked(ContactFieldPaths.Email, failClosed: false);
                else if (field?.Kind == PrechatFieldKind.Name)
                    hide = maskAll;
                else if (field is null)
                    hide = true;
                else
                {
                    var attrField = ContactFieldPaths.Attribute(key);
                    hide = IsMasked(attrField, failClosed: field.Sensitive && (policyFields is null || !policyFields.Contains(attrField)));
                }
                answerList.Add(new LiveChatPrechatAnswerView(key, field?.Label ?? key, hide ? null : value, hide));
            }
            answers = answerList;
        }
        var identityHidden = session.Identity is not null && IsMasked(ContactFieldPaths.ExternalId, failClosed: false);
        IReadOnlyList<MaskedField> maskedFields;
        if (masked is null || masked.Count == 0)
            maskedFields = [];
        else
        {
            var arr = new MaskedField[masked.Count];
            var idx = 0;
            foreach (var pair in masked)
                arr[idx++] = new MaskedField(pair.Key, pair.Value);
            if (arr.Length > 1)
                Array.Sort(arr, static (a, b) => a.Field.CompareTo(b.Field));
            maskedFields = arr;
        }
        return new TicketValue<LiveChatVisitorView>(new LiveChatVisitorView(session.VisitorId, session.Identity is not null, session.Identity?.Namespace,
            identityHidden ? null : session.Identity?.Value, maskAll ? null : session.DisplayName, answers, session.CreatedAt, maskedFields));
    }
}
