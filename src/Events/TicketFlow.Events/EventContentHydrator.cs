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
        if (events.Count == 0)
            return new Dictionary<long, EventContent>(0);

        // Collect distinct contacts and survey invitations up front so messages, contacts and surveys hydrate in parallel.
        HashSet<ContactId>? wantedContacts = null;
        HashSet<Guid>? wantedSurveys = null;
        for (var i = 0; i < events.Count; i++)
        {
            var stored = events[i];
            ContactId? own = null;
            switch (stored.Event.Data)
            {
                case SurveyRespondedData responded:
                    if (surveys is not null && Guid.TryParse(responded.Invitation, out var invitation) && invitation != Guid.Empty)
                        (wantedSurveys ??= []).Add(invitation);
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
                case ConversationMessageReceivedData:
                case ConversationMessageSentData:
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
                case RoutingQueuedData:
                case RoutingOverflowedData:
                case RoutingQueueWaitExceededData:
                case ChannelDeliveryStatusData:
                    break;
            }
            if (own is { } ownContact)
            {
                if (contacts is not null)
                    (wantedContacts ??= []).Add(ownContact);
            }
            else if (contacts is not null && stored.Resource.Conversation is not null && stored.Resource.Contact is { } conversationContact)
                (wantedContacts ??= []).Add(conversationContact);
        }

        var messagesTask = MessagesAsync(events, principal, cancellationToken);
        var contactsTask = wantedContacts is not null ? LoadContactsAsync(principal, wantedContacts, cancellationToken) : Task.FromResult<Dictionary<ContactId, (ContactEventContent Content, IReadOnlyList<string> Masked)?>>(EmptyContacts);
        var surveysTask = wantedSurveys is not null ? LoadSurveysAsync(principal, wantedSurveys, cancellationToken) : Task.FromResult<Dictionary<Guid, SurveyResponseView>>(EmptySurveys);
        await Task.WhenAll(messagesTask, contactsTask, surveysTask).ConfigureAwait(false);
        var messages = messagesTask.Result;
        var contactCache = contactsTask.Result;
        var surveyCache = surveysTask.Result;

        var result = new Dictionary<long, EventContent>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var stored = events[i];
            EventContentValue? value = null;
            SortedSet<string>? masked = null;
            ContactId? own = null;
            switch (stored.Event.Data)
            {
                case ConversationMessageReceivedData received:
                    value = Message(messages, received.Conversation, received.TimelineSequence, ref masked);
                    break;
                case ConversationMessageSentData sent:
                    value = Message(messages, sent.Conversation, sent.TimelineSequence, ref masked);
                    break;
                case SurveyRespondedData responded:
                    if (Guid.TryParse(responded.Invitation, out var invitation) && surveyCache.TryGetValue(invitation, out var response))
                    {
                        value = new SurveyCommentContent(response.Comment);
                        if (response.CommentMasked)
                            (masked ??= new(StringComparer.Ordinal)).Add(SurveyFieldPaths.Comment.Value);
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
                case RoutingQueuedData:
                case RoutingOverflowedData:
                case RoutingQueueWaitExceededData:
                case ChannelDeliveryStatusData:
                    break;
            }
            ContactEventContent? related = null;
            if (own is { } ownContact)
            {
                if (contactCache.TryGetValue(ownContact, out var contact) && contact is { } found)
                {
                    value = found.Content;
                    if (found.Masked.Count > 0)
                        (masked ??= new(StringComparer.Ordinal)).UnionWith(found.Masked);
                }
            }
            else if (stored.Resource.Conversation is not null && stored.Resource.Contact is { } conversationContact
                && contactCache.TryGetValue(conversationContact, out var contact) && contact is { } found)
            {
                related = found.Content;
                if (found.Masked.Count > 0)
                    (masked ??= new(StringComparer.Ordinal)).UnionWith(found.Masked);
            }
            if (value is not null || related is not null || masked is { Count: > 0 })
                result[stored.Sequence] = new EventContent(value, masked is { Count: > 0 } ? [.. masked] : [], related);
        }
        return result;
    }

    private static readonly Dictionary<ContactId, (ContactEventContent Content, IReadOnlyList<string> Masked)?> EmptyContacts = [];
    private static readonly Dictionary<Guid, SurveyResponseView> EmptySurveys = [];

    private async Task<Dictionary<ContactId, (ContactEventContent Content, IReadOnlyList<string> Masked)?>> LoadContactsAsync(ApiClientPrincipal principal,
        HashSet<ContactId> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ContactId, (ContactEventContent Content, IReadOnlyList<string> Masked)?>(ids.Count);
        if (ids.Count == 1)
        {
            foreach (var id in ids)
                result[id] = await LoadContactAsync(principal, id, cancellationToken).ConfigureAwait(false);
            return result;
        }
        using var gate = new SemaphoreSlim(8);
        var tasks = new List<Task<(ContactId Id, (ContactEventContent Content, IReadOnlyList<string> Masked)? Value)>>(ids.Count);
        foreach (var id in ids)
            tasks.Add(LoadOneContactAsync(id, principal, gate, cancellationToken));
        var loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
        for (var i = 0; i < loaded.Length; i++)
            result[loaded[i].Id] = loaded[i].Value;
        return result;
    }

    private async Task<(ContactId Id, (ContactEventContent Content, IReadOnlyList<string> Masked)? Value)> LoadOneContactAsync(ContactId id, ApiClientPrincipal principal,
        SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (id, await LoadContactAsync(principal, id, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Dictionary<Guid, SurveyResponseView>> LoadSurveysAsync(ApiClientPrincipal principal, HashSet<Guid> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, SurveyResponseView>(ids.Count);
        using var gate = new SemaphoreSlim(8);
        var tasks = new List<Task<(Guid Id, SurveyResponseView? View)>>(ids.Count);
        foreach (var id in ids)
            tasks.Add(LoadOneSurveyAsync(id, principal, gate, cancellationToken));
        var loaded = await Task.WhenAll(tasks).ConfigureAwait(false);
        for (var i = 0; i < loaded.Length; i++)
            if (loaded[i].View is { } view)
                result[loaded[i].Id] = view;
        return result;
    }

    private async Task<(Guid Id, SurveyResponseView? View)> LoadOneSurveyAsync(Guid id, ApiClientPrincipal principal, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var res = await surveys!.GetResponseAsync(principal, new SurveyInvitationId(id), cancellationToken).ConfigureAwait(false);
            return (id, res.Value is TicketValue<SurveyResponseView> ok ? ok.Value : null);
        }
        finally
        {
            gate.Release();
        }
    }

    private static EventContentValue? Message(IReadOnlyDictionary<(ConversationId, long), TimelineEventView> messages, ConversationId conversation, long sequence,
        ref SortedSet<string>? masked)
    {
        if (!messages.TryGetValue((conversation, sequence), out var view) || view.Event.Event.Value is not MessageEvent message)
            return null;
        if (view.MaskedFields.Count > 0)
        {
            masked ??= new(StringComparer.Ordinal);
            for (var i = 0; i < view.MaskedFields.Count; i++)
                masked.Add(view.MaskedFields[i].Field.Value);
        }
        var attachments = message.Attachments.Count == 0
            ? []
            : new EventAttachment[message.Attachments.Count];
        for (var i = 0; i < message.Attachments.Count; i++)
        {
            var a = message.Attachments[i];
            attachments[i] = new EventAttachment(a.FileName, a.MediaType, a.SizeBytes);
        }
        return new MessageEventContent(message.Body, EventMapping.ContentType(message.ContentType), attachments, message.ExternalId);
    }

    /// <summary>Timeline range reads per conversation, splitting sparse sequence gaps wider than one page.</summary>
    private async Task<IReadOnlyDictionary<(ConversationId, long), TimelineEventView>> MessagesAsync(IReadOnlyList<StoredIntegrationEvent> events, ApiClientPrincipal principal,
        CancellationToken cancellationToken)
    {
        var views = new Dictionary<(ConversationId, long), TimelineEventView>();
        if (conversations is null)
            return views;
        Dictionary<ConversationId, List<long>>? byConversation = null;
        for (var i = 0; i < events.Count; i++)
        {
            (ConversationId Conv, long Seq)? entry = events[i].Event.Data.Value switch
            {
                ConversationMessageReceivedData received => (received.Conversation, received.TimelineSequence),
                ConversationMessageSentData sent => (sent.Conversation, sent.TimelineSequence),
                _ => null,
            };
            if (entry is { } e)
            {
                byConversation ??= [];
                if (!byConversation.TryGetValue(e.Conv, out var list))
                    byConversation[e.Conv] = list = [];
                list.Add(e.Seq);
            }
        }
        if (byConversation is null)
            return views;
        foreach (var (conversation, seqList) in byConversation)
        {
            seqList.Sort();
            var wanted = new HashSet<long>(seqList);
            // Split into contiguous clusters where consecutive wanted sequences are within one page of each other.
            var startIndex = 0;
            while (startIndex < seqList.Count)
            {
                var clusterStart = seqList[startIndex];
                var clusterEnd = clusterStart;
                var nextIndex = startIndex + 1;
                while (nextIndex < seqList.Count && seqList[nextIndex] - clusterEnd <= IConversationStore.MaximumTimelineRead)
                {
                    clusterEnd = seqList[nextIndex];
                    nextIndex++;
                }
                var after = clusterStart - 1;
                while (after < clusterEnd)
                {
                    var limit = (int)Math.Min(clusterEnd - after, IConversationStore.MaximumTimelineRead);
                    if ((await conversations.ReadTimelineAsync(principal, conversation, after, limit, cancellationToken).ConfigureAwait(false)).Value is not TimelinePage page)
                        break;
                    for (var i = 0; i < page.Events.Count; i++)
                    {
                        var view = page.Events[i];
                        if (wanted.Contains(view.Event.Sequence))
                            views[(conversation, view.Event.Sequence)] = view;
                    }
                    if (page.NextAfterSequence is not { } next || next <= after)
                        break;
                    after = next;
                }
                startIndex = nextIndex;
            }
        }
        return views;
    }

    private async Task<(ContactEventContent Content, IReadOnlyList<string> Masked)?> LoadContactAsync(ApiClientPrincipal principal, ContactId contact, CancellationToken cancellationToken)
    {
        if (contacts is null || (await contacts.GetAsync(principal, contact, cancellationToken).ConfigureAwait(false)).Value is not TicketValue<ContactView> applied)
            return null;
        var view = applied.Value;
        return (new ContactEventContent(view.Profile.DisplayName,
            [.. view.Identities.Where(identity => !identity.Masked && identity.Value is not null)
                .Select(identity => new EventIdentity(EventMapping.Identity(identity.Kind), identity.Namespace, identity.Value!, identity.Verification.Value is IdentityVerified))]),
            [.. view.MaskedFields.Select(field => field.Field.Value)]);
    }
}
