using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Builds masked conversation and timeline projections for <c>conversation:read</c> in one scope. Fields listed by an
/// <see cref="AccessMasked"/> decision are removed; a sensitive attribute with no field policy applying to the permission, or an
/// attribute without a definition, is masked and not revealable (fail closed). A denied decision masks every maskable field.
/// </summary>
internal sealed class ConversationMasking
{
    private readonly IReadOnlyDictionary<string, ConversationAttributeDefinition> definitions;
    private readonly HashSet<FieldPath> policyFields;

    private ConversationMasking(IReadOnlyDictionary<string, ConversationAttributeDefinition> definitions, HashSet<FieldPath> policyFields)
    {
        this.definitions = definitions;
        this.policyFields = policyFields;
    }

    public IReadOnlyDictionary<string, ConversationAttributeDefinition> Definitions => definitions;

    public static async Task<ConversationMasking> LoadAsync(TicketScope scope, Permission permission, IConversationAttributeDefinitionStore definitionStore,
        IAccessFieldPolicyStore policyStore, CancellationToken cancellationToken)
    {
        var definitions = (await definitionStore.ListAsync(scope, cancellationToken).ConfigureAwait(false))
            .ToDictionary(stored => stored.Definition.Key, stored => stored.Definition, StringComparer.Ordinal);
        var policies = (await policyStore.ListAsync(scope, cancellationToken).ConfigureAwait(false))
            .Where(stored => stored.Policy.AppliesTo.Contains(permission))
            .Select(stored => stored.Policy.Field)
            .ToHashSet();
        return new(definitions, policies);
    }

    public ConversationView Project(StoredConversation stored, AccessDecision decision)
    {
        var conversation = stored.Conversation;
        var masks = new Masks(decision);
        ChannelThreadView? thread = null;
        if (conversation.ChannelThread is { } reference)
        {
            var masked = masks.IsMasked(ConversationFieldPaths.Channel, alwaysMask: false, out var revealable);
            thread = new ChannelThreadView(reference.Channel, masked ? null : reference.ExternalThreadId, masked, revealable);
        }
        var attributes = new List<ConversationAttributeView>();
        foreach (var key in conversation.AttributeKeys)
        {
            if (!definitions.TryGetValue(key, out var definition))
                continue;
            var masked = masks.IsMasked(definition.Field, definition.Sensitive && !policyFields.Contains(definition.Field), out var revealable);
            attributes.Add(new(key, definition.Type, definition.Sensitive, masked ? (ConversationAttributeValue?)null : conversation.Attributes[key], masked, revealable));
        }
        return new ConversationView(conversation.Id, stored.Revision, conversation.Inbox, conversation.Contact, conversation.Status, conversation.Priority,
            conversation.Assignee, conversation.Team, conversation.Labels, attributes, conversation.Participants, thread, conversation.CreatedAt,
            conversation.LastActivityAt, conversation.WaitingSince, conversation.FirstReplyAt, conversation.LastIncomingAt, conversation.LastOutgoingAt,
            conversation.LastSequence, masks.Fields);
    }

    public TimelineEventView Project(TimelineEvent timelineEvent, AccessDecision decision)
    {
        var masks = new Masks(decision);
        switch (timelineEvent.Event)
        {
            case MessageEvent message when message.ExternalId is not null:
                if (masks.IsMasked(ConversationFieldPaths.Channel, alwaysMask: false, out _))
                    return new(timelineEvent.WithEvent(message.WithDelivery(message.Delivery, null)), masks.Fields);
                break;
            case AttributeChanged attribute:
                var defined = definitions.TryGetValue(attribute.Key, out var definition);
                var fail = !defined || (definition!.Sensitive && !policyFields.Contains(definition.Field));
                if (masks.IsMasked(ConversationFieldPaths.Attribute(attribute.Key), fail, out _))
                    return new(timelineEvent.WithEvent(new AttributeChanged(attribute.Key, null, null)), masks.Fields);
                break;
            case MessageEvent:
            case NoteEvent:
            case AssignmentChanged:
            case TeamChanged:
            case StatusChanged:
            case PriorityChanged:
            case LabelsChanged:
            case ParticipantChanged:
            case ContactChanged:
            case SystemEvent:
            case ExtensionEvent:
                break;
        }
        return new(timelineEvent, []);
    }

    /// <summary>Tracks the masked fields of one projection.</summary>
    private sealed class Masks
    {
        private readonly Dictionary<FieldPath, bool> decided = [];
        private readonly SortedDictionary<FieldPath, bool> used = new();
        private readonly bool maskAll;

        public Masks(AccessDecision decision)
        {
            switch (decision)
            {
                case AccessAllowed:
                    break;
                case AccessMasked masked:
                    foreach (var field in masked.Fields)
                        decided[field.Field] = field.Revealable;
                    break;
                case AccessDenied:
                    maskAll = true;
                    break;
            }
        }

        public IReadOnlyList<MaskedField> Fields => [.. used.Select(pair => new MaskedField(pair.Key, pair.Value))];

        public bool IsMasked(FieldPath field, bool alwaysMask, out bool revealable)
        {
            if (decided.TryGetValue(field, out revealable))
            {
                used[field] = revealable;
                return true;
            }
            revealable = false;
            if (!maskAll && !alwaysMask)
                return false;
            used[field] = false;
            return true;
        }
    }
}
