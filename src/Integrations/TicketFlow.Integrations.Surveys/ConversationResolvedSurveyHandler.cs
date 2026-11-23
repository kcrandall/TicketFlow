using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Surveys;

namespace TicketFlow.Integrations.Surveys;

/// <summary>
/// Conversation outbox handler (consumer <see cref="ConsumerName"/>) that schedules surveys when a conversation is resolved. For each
/// appended <c>StatusChanged</c> event whose current status is Resolved it builds a trusted <see cref="ConversationResolvedNotification"/>
/// from store reads — the conversation's inbox, team and assignee at that moment, the contact (following a merge redirect if the
/// conversation was re-pointed later) and the contact's preferred locale when it is a valid survey locale — and calls
/// <see cref="ISurveyScheduler.HandleResolvedAsync"/>. Scheduling is idempotent per (survey, conversation), so redelivered entries are
/// harmless. Conversations deleted since the change are skipped.
/// </summary>
public sealed class ConversationResolvedSurveyHandler(IConversationStore conversations, IContactStore contacts, ISurveyScheduler scheduler) : IConversationOutboxHandler
{
    public const string ConsumerName = "surveys.resolved";

    private readonly IConversationStore conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
    private readonly IContactStore contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
    private readonly ISurveyScheduler scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    public string Consumer => ConsumerName;

    public async Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var change = entry.Change;
        if (change.Kind != ConversationChangeKind.EventsAppended)
            return;
        StoredConversation? stored = null;
        SurveyLocale? locale = null;
        var loaded = false;
        var summaries = change.Events;
        for (var i = 0; i < summaries.Count; i++)
        {
            var summary = summaries[i];
            if (summary.Kind != ConversationEventKind.Status)
                continue;
            var events = await conversations.ReadTimelineAsync(change.Scope, change.Conversation, summary.Sequence - 1, 1, includeNotes: false, cancellationToken).ConfigureAwait(false);
            if (events.Count != 1 || events[0].Sequence != summary.Sequence || events[0].Event.Value is not StatusChanged { Current.Value: ResolvedStatus })
                continue;
            if (!loaded)
            {
                loaded = true;
                stored = await conversations.GetAsync(change.Scope, change.Conversation, cancellationToken).ConfigureAwait(false);
                if (stored is null)
                    return;
                var contact = await contacts.GetAsync(change.Scope, stored.Conversation.Contact, cancellationToken).ConfigureAwait(false);
                locale = contact?.Contact.Profile.Locale is { } value && SurveyLocale.TryParse(value, out var parsed) ? parsed : null;
            }
            var conversation = stored!.Conversation;
            await scheduler.HandleResolvedAsync(new ConversationResolvedNotification(change.Scope, conversation.Id, conversation.Contact, events[0].OccurredAt,
                conversation.Inbox, conversation.Team, conversation.Assignee, locale), cancellationToken).ConfigureAwait(false);
        }
    }
}

public static class ConversationSurveysTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="ConversationResolvedSurveyHandler"/> as an <see cref="IConversationOutboxHandler"/>. Requires conversation and
    /// contact stores and the survey scheduler (<c>AddSurveys()</c>). Register the consumer through
    /// <see cref="ConversationOutboxDispatcher.RegisterAsync"/> before resolutions that should be surveyed.
    /// </summary>
    public static TicketFlowBuilder AddConversationSurveys(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, ConversationResolvedSurveyHandler>());
        return builder;
    }
}
