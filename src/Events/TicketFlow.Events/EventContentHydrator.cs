using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Surveys;

namespace TicketFlow.Events;

/// <summary>
/// Hydrates <see cref="EventPrivacy.Content"/> for a delivery batch as the subscription principal, always through the owning feature
/// services so their Access decisions and masking apply (masked values are removed and listed): message bodies through
/// <see cref="IConversationService.ReadTimelineAsync"/> (one timeline range read per conversation per batch), contact profiles and identities
/// through <see cref="IContactService.GetAsync"/> (once per distinct contact per batch; attached to contact events and, as
/// <see cref="EventContent.Contact"/>, to conversation events of that contact), and survey comments through
/// <see cref="ISurveyService.GetResponseAsync"/> (<c>survey:read</c> and the <c>survey.comment</c> field policy). Notes are never hydrated. A
/// refused or missing read yields no content for that part.
/// </summary>
public sealed class EventContentHydrator(IConversationService? conversations = null, IContactService? contacts = null, ISurveyService? surveys = null)
{
    /// <summary>Content per event sequence (absent when the event has none).</summary>
    public async Task<IReadOnlyDictionary<long, EventContent>> HydrateAsync(IReadOnlyList<StoredIntegrationEvent> events, ApiClientPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(principal);
        var messages = await MessagesAsync(events, principal, cancellationToken).ConfigureAwait(false);
        var contactCache = new Dictionary<ContactId, (ContactEventContent Content, IReadOnlyList<string> Masked)?>();
        async Task<(ContactEventContent Content, IReadOnlyList<string> Masked)?> ContactAsync(ContactId id)
        {
            if (!contactCache.TryGetValue(id, out var value))
                contactCache[id] = value = await LoadContactAsync(principal, id, cancellationToken).ConfigureAwait(false);
            return value;
        }

        var result = new Dictionary<long, EventContent>();
        foreach (var stored in events)
        {
            EventContentValue? value = null;
            var masked = new SortedSet<string>(StringComparer.Ordinal);
            ContactId? own = null;
            switch (stored.Event.Data)
            {
                case ConversationMessageReceivedData received:
                    value = Message(messages, received.Conversation, received.TimelineSequence, masked);
                    break;
                case ConversationMessageSentData sent:
                    value = Message(messages, sent.Conversation, sent.TimelineSequence, masked);
                    break;
                case SurveyRespondedData responded:
                    if (surveys is not null && Guid.TryParse(responded.Invitation, out var invitation) && invitation != Guid.Empty
                        && (await surveys.GetResponseAsync(principal, new SurveyInvitationId(invitation), cancellationToken).ConfigureAwait(false)).Value
                            is SurveySucceeded<SurveyResponseView> response)
                    {
                        value = new SurveyCommentContent(response.Value.Comment);
                        if (response.Value.CommentMasked)
                            masked.Add(SurveyFieldPaths.Comment.Value);
                    }
                    break;
                case ContactCreatedData created:
                    own = created.Contact;
                    break;
                case ContactUpdatedData updated:
                    own = updated.Contact;
                    break;
                case ContactMergedData merged:
                    own = merged.Target;
                    break;
                case ContactUnmergedData unmerged:
                    own = unmerged.Contact;
                    break;
                case ContactIdentityLinkedData linked:
                    own = linked.Contact;
                    break;
                case ConversationCreatedData:
                case ConversationStatusChangedData:
                case ConversationAssignedData:
                case ConversationTeamChangedData:
                case ConversationPriorityChangedData:
                case ConversationLabelsChangedData:
                case ConversationNoteAddedData:
                case ConversationResolvedData:
                case ConversationReopenedData:
                case ConversationDeletedData:
                case ContactDeletedData:
                case SlaAppliedData:
                case SlaWarningData:
                case SlaBreachedData:
                case SlaMetData:
                case RoutingAssignedData:
                case RoutingOverflowedData:
                case RoutingQueueWaitExceededData:
                case ChannelDeliveryStatusData:
                    break;
            }
            ContactEventContent? related = null;
            if (own is { } ownContact)
            {
                if (await ContactAsync(ownContact).ConfigureAwait(false) is { } contact)
                {
                    value = contact.Content;
                    masked.UnionWith(contact.Masked);
                }
            }
            else if (stored.Resource.Conversation is not null && stored.Resource.Contact is { } conversationContact
                && await ContactAsync(conversationContact).ConfigureAwait(false) is { } contact)
            {
                related = contact.Content;
                masked.UnionWith(contact.Masked);
            }
            if (value is not null || related is not null || masked.Count > 0)
                result[stored.Sequence] = new EventContent(value, [.. masked], related);
        }
        return result;
    }

    private static EventContentValue? Message(IReadOnlyDictionary<(ConversationId, long), TimelineEventView> messages, ConversationId conversation, long sequence,
        SortedSet<string> masked)
    {
        if (!messages.TryGetValue((conversation, sequence), out var view) || view.Event.Event.Value is not MessageEvent message)
            return null;
        masked.UnionWith(view.MaskedFields.Select(field => field.Field.Value));
        return new MessageEventContent(message.Body, EventMapping.ContentType(message.ContentType),
            [.. message.Attachments.Select(attachment => new EventAttachment(attachment.FileName, attachment.MediaType, attachment.SizeBytes))], message.ExternalId);
    }

    /// <summary>One timeline range read (paged by the service maximum) per conversation covering the batch's message sequences.</summary>
    private async Task<IReadOnlyDictionary<(ConversationId, long), TimelineEventView>> MessagesAsync(IReadOnlyList<StoredIntegrationEvent> events, ApiClientPrincipal principal,
        CancellationToken cancellationToken)
    {
        var views = new Dictionary<(ConversationId, long), TimelineEventView>();
        if (conversations is null)
            return views;
        var wanted = events.Select(stored => stored.Event.Data.Value switch
            {
                ConversationMessageReceivedData received => (received.Conversation, received.TimelineSequence),
                ConversationMessageSentData sent => (sent.Conversation, sent.TimelineSequence),
                _ => ((ConversationId, long)?)null,
            }).OfType<(ConversationId Conversation, long Sequence)>().ToList();
        foreach (var group in wanted.GroupBy(item => item.Conversation))
        {
            var sequences = group.Select(item => item.Sequence).ToHashSet();
            var after = sequences.Min() - 1;
            var last = sequences.Max();
            while (after < last)
            {
                var limit = (int)Math.Min(last - after, IConversationStore.MaximumTimelineRead);
                if ((await conversations.ReadTimelineAsync(principal, group.Key, after, limit, cancellationToken).ConfigureAwait(false)).Value is not TimelinePage page)
                    break;
                foreach (var view in page.Events.Where(view => sequences.Contains(view.Event.Sequence)))
                    views[(group.Key, view.Event.Sequence)] = view;
                if (page.NextAfterSequence is not { } next || next <= after)
                    break;
                after = next;
            }
        }
        return views;
    }

    private async Task<(ContactEventContent Content, IReadOnlyList<string> Masked)?> LoadContactAsync(ApiClientPrincipal principal, ContactId contact, CancellationToken cancellationToken)
    {
        if (contacts is null || (await contacts.GetAsync(principal, contact, cancellationToken).ConfigureAwait(false)).Value is not ContactApplied<ContactView> applied)
            return null;
        var view = applied.Value;
        return (new ContactEventContent(view.Profile.DisplayName,
            [.. view.Identities.Where(identity => !identity.Masked && identity.Value is not null)
                .Select(identity => new EventIdentity(EventMapping.Identity(identity.Kind), identity.Namespace, identity.Value!, identity.Verification.Value is IdentityVerified))]),
            [.. view.MaskedFields.Select(field => field.Field.Value)]);
    }
}
