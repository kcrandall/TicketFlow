using System.Collections.Frozen;
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
    public static ConversationMasking Empty { get; } = new(FrozenDictionary<string, ConversationAttributeDefinition>.Empty, []);

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
        var definitionsTask = definitionStore.ListAsync(scope, cancellationToken);
        var policiesTask = policyStore.ListAsync(scope, cancellationToken);
        await Task.WhenAll(definitionsTask, policiesTask).ConfigureAwait(false);
        var storedDefs = definitionsTask.Result;
        var definitions = new Dictionary<string, ConversationAttributeDefinition>(storedDefs.Count, StringComparer.Ordinal);
        foreach (var stored in storedDefs)
            definitions[stored.Definition.Key] = stored.Definition;
        return new(definitions, BuildPolicyFields(policiesTask.Result, permission));
    }

    public static async Task<ConversationMasking> LoadAsync(TicketScope scope, Permission permission,
        IReadOnlyDictionary<string, ConversationAttributeDefinition> definitions, IAccessFieldPolicyStore policyStore, CancellationToken cancellationToken)
    {
        var storedPolicies = await policyStore.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        return new(definitions, BuildPolicyFields(storedPolicies, permission));
    }

    private static HashSet<FieldPath> BuildPolicyFields(IReadOnlyList<StoredAccessFieldPolicy> storedPolicies, Permission permission)
    {
        var fields = new HashSet<FieldPath>();
        foreach (var stored in storedPolicies)
            if (stored.Policy.AppliesTo.Contains(permission))
                fields.Add(stored.Policy.Field);
        return fields;
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
        IReadOnlyList<ConversationAttributeView> attributes;
        if (conversation.AttributeKeys.Count == 0 || definitions.Count == 0)
            attributes = [];
        else
        {
            var list = new List<ConversationAttributeView>(conversation.AttributeKeys.Count);
            foreach (var key in conversation.AttributeKeys)
            {
                if (!definitions.TryGetValue(key, out var definition))
                    continue;
                var field = definition.Field;
                var masked = masks.IsMasked(field, definition.Sensitive && !policyFields.Contains(field), out var revealable);
                list.Add(new(key, definition.Type, definition.Sensitive, masked ? (ConversationAttributeValue?)null : conversation.Attributes[key], masked, revealable));
            }
            attributes = list;
        }
        return new ConversationView(conversation.Id, stored.Revision, conversation.Inbox, conversation.Contact, conversation.Status, conversation.Priority,
            conversation.Assignee, conversation.Team, conversation.Labels, attributes, conversation.Participants, thread, conversation.CreatedAt,
            conversation.LastActivityAt, conversation.WaitingSince, conversation.FirstReplyAt, conversation.LastIncomingAt, conversation.LastOutgoingAt,
            conversation.LastSequence, masks.Fields);
    }

    public TimelineEventView Project(TimelineEvent timelineEvent, AccessDecision decision) => timelineEvent.Event switch
    {
        MessageEvent message when message.ExternalId is not null => ProjectMessage(timelineEvent, message, decision),
        AttributeChanged attribute => ProjectAttribute(timelineEvent, attribute, decision),
        MessageEvent or NoteEvent or AssignmentChanged or TeamChanged or StatusChanged or PriorityChanged or LabelsChanged
            or ParticipantChanged or ContactChanged or SystemEvent or ExtensionEvent => new(timelineEvent, []),
    };

    private static TimelineEventView ProjectMessage(TimelineEvent timelineEvent, MessageEvent message, AccessDecision decision)
    {
        if (decision is AccessAllowed)
            return new(timelineEvent, []);
        var (masked, revealable) = CheckFieldMask(decision, ConversationFieldPaths.Channel, alwaysMask: false);
        return masked
            ? new(timelineEvent.WithEvent(message.WithDelivery(message.Delivery, null)), [new MaskedField(ConversationFieldPaths.Channel, revealable)])
            : new(timelineEvent, []);
    }

    private TimelineEventView ProjectAttribute(TimelineEvent timelineEvent, AttributeChanged attribute, AccessDecision decision)
    {
        var defined = definitions.TryGetValue(attribute.Key, out var definition);
        var field = defined ? definition!.Field : ConversationFieldPaths.Attribute(attribute.Key);
        var fail = !defined || (definition!.Sensitive && !policyFields.Contains(field));
        var (masked, revealable) = CheckFieldMask(decision, field, fail);
        return masked
            ? new(timelineEvent.WithEvent(new AttributeChanged(attribute.Key, null, null)), [new MaskedField(field, revealable)])
            : new(timelineEvent, []);
    }

    private static (bool Masked, bool Revealable) CheckFieldMask(AccessDecision decision, FieldPath field, bool alwaysMask) => decision switch
    {
        AccessAllowed => (alwaysMask, false),
        AccessMasked masked => FindMaskedField(masked, field, alwaysMask),
        AccessDenied => (true, false),
    };

    private static (bool Masked, bool Revealable) FindMaskedField(AccessMasked masked, FieldPath field, bool alwaysMask)
    {
        for (var i = 0; i < masked.Fields.Count; i++)
            if (masked.Fields[i].Field == field)
                return (true, masked.Fields[i].Revealable);
        return (alwaysMask, false);
    }

    /// <summary>Tracks the masked fields of one projection.</summary>
    private struct Masks
    {
        private readonly AccessMasked? decided;
        private readonly bool maskAll;
        private List<MaskedField>? used;

        public Masks(AccessDecision decision)
        {
            switch (decision)
            {
                case AccessAllowed:
                    decided = null;
                    maskAll = false;
                    break;
                case AccessMasked masked:
                    decided = masked;
                    maskAll = false;
                    break;
                case AccessDenied:
                    decided = null;
                    maskAll = true;
                    break;
            }
            used = null;
        }

        public IReadOnlyList<MaskedField> Fields
        {
            get
            {
                if (used is null || used.Count == 0)
                    return [];
                if (used.Count > 1)
                    used.Sort(static (a, b) => a.Field.CompareTo(b.Field));
                return used;
            }
        }

        public bool IsMasked(FieldPath field, bool alwaysMask, out bool revealable)
        {
            if (decided is not null)
            {
                var fields = decided.Fields;
                for (var i = 0; i < fields.Count; i++)
                {
                    if (fields[i].Field == field)
                    {
                        revealable = fields[i].Revealable;
                        Record(field, revealable);
                        return true;
                    }
                }
            }
            revealable = false;
            if (!maskAll && !alwaysMask)
                return false;
            Record(field, false);
            return true;
        }

        private void Record(FieldPath field, bool revealable)
        {
            used ??= [];
            for (var i = 0; i < used.Count; i++)
            {
                if (used[i].Field == field)
                {
                    used[i] = new MaskedField(field, revealable);
                    return;
                }
            }
            used.Add(new MaskedField(field, revealable));
        }
    }
}
