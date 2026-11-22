using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Surveys;

namespace TicketFlow.Events;

/// <summary>
/// Hydrates <see cref="EventPrivacy.Content"/> for one event as the subscription principal: message bodies through
/// <see cref="IConversationService.ReadTimelineAsync"/> and contact profiles/identities through <see cref="IContactService.GetAsync"/> — so
/// the owning feature applies its Access decision and masking (masked values are removed and listed) — and survey comments from the
/// trusted <see cref="ISurveyStore"/> after the engine decided <c>conversation:read</c> on the response's conversation. Notes are never
/// hydrated. Returns <see langword="null"/> when there is no content (unsupported type, deleted record or refused read).
/// </summary>
public sealed class EventContentHydrator(IConversationService? conversations = null, IContactService? contacts = null, ISurveyStore? surveys = null)
{
    public async Task<EventContent?> HydrateAsync(StoredIntegrationEvent stored, ApiClientPrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(principal);
        switch (stored.Event.Data)
        {
            case ConversationMessageReceivedData received:
                return await MessageAsync(principal, received.Conversation, received.TimelineSequence, cancellationToken).ConfigureAwait(false);
            case ConversationMessageSentData sent:
                return await MessageAsync(principal, sent.Conversation, sent.TimelineSequence, cancellationToken).ConfigureAwait(false);
            case SurveyRespondedData responded:
                if (surveys is null || !Guid.TryParse(responded.Invitation, out var invitation) || invitation == Guid.Empty)
                    return null;
                var response = await surveys.GetResponseAsync(stored.Scope, new SurveyInvitationId(invitation), cancellationToken).ConfigureAwait(false);
                return response is null ? null : new EventContent(new SurveyCommentContent(response.Response.Comment), []);
            case ContactCreatedData created:
                return await ContactAsync(principal, created.Contact, cancellationToken).ConfigureAwait(false);
            case ContactUpdatedData updated:
                return await ContactAsync(principal, updated.Contact, cancellationToken).ConfigureAwait(false);
            case ContactMergedData merged:
                return await ContactAsync(principal, merged.Target, cancellationToken).ConfigureAwait(false);
            case ContactUnmergedData unmerged:
                return await ContactAsync(principal, unmerged.Contact, cancellationToken).ConfigureAwait(false);
            case ContactIdentityLinkedData linked:
                return await ContactAsync(principal, linked.Contact, cancellationToken).ConfigureAwait(false);
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
                return null;
        }
        return null;
    }

    private async Task<EventContent?> MessageAsync(ApiClientPrincipal principal, ConversationId conversation, long sequence, CancellationToken cancellationToken)
    {
        if (conversations is null)
            return null;
        var result = await conversations.ReadTimelineAsync(principal, conversation, sequence - 1, 1, cancellationToken).ConfigureAwait(false);
        if (result.Value is not TimelinePage page || page.Events.FirstOrDefault(view => view.Event.Sequence == sequence) is not { } view
            || view.Event.Event.Value is not MessageEvent message)
            return null;
        return new EventContent(new MessageEventContent(message.Body, EventMapping.ContentType(message.ContentType),
            [.. message.Attachments.Select(attachment => new EventAttachment(attachment.FileName, attachment.MediaType, attachment.SizeBytes))], message.ExternalId),
            [.. view.MaskedFields.Select(field => field.Field.Value)]);
    }

    private async Task<EventContent?> ContactAsync(ApiClientPrincipal principal, ContactId contact, CancellationToken cancellationToken)
    {
        if (contacts is null)
            return null;
        var result = await contacts.GetAsync(principal, contact, cancellationToken).ConfigureAwait(false);
        if (result.Value is not ContactApplied<ContactView> applied)
            return null;
        var view = applied.Value;
        return new EventContent(new ContactEventContent(view.Profile.DisplayName,
            [.. view.Identities.Where(identity => !identity.Masked && identity.Value is not null)
                .Select(identity => new EventIdentity(EventMapping.Identity(identity.Kind), identity.Namespace, identity.Value!, identity.Verification.Value is IdentityVerified))]),
            [.. view.MaskedFields.Select(field => field.Field.Value)]);
    }
}
