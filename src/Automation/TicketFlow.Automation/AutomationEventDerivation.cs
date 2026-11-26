using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;

namespace TicketFlow.Automation;

/// <summary>Derives automation trigger events from timeline events (shared by the outbox handler and replay).</summary>
public static class AutomationEventDerivation
{
    /// <summary>
    /// The automation events one timeline event raises (at most one fires any given trigger, so a rule runs at most once per timeline
    /// event), plus the triggering message for message events. Notes, system events, contact changes and non-SLA extensions raise none.
    /// <c>sla.warning</c>/<c>sla.breached</c> extensions count only when their actor is <paramref name="slaWorker"/> (the SLA tracker's API
    /// client); the same payload appended by anyone else is ignored, so escalations cannot be forged.
    /// </summary>
    public static (IReadOnlyList<AutomationEvent> Events, AutomationMessageFacts? Message) Derive(TimelineEvent timelineEvent, ApiClientId slaWorker)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);
        var sequence = timelineEvent.Sequence;
        return timelineEvent.Event switch
        {
            MessageEvent message => ([message.Direction == MessageDirection.Incoming ? AutomationEvent.MessageReceived(sequence) : AutomationEvent.MessageSent(sequence)],
                new AutomationMessageFacts(message.Direction, message.Body)),
            StatusChanged status => ([AutomationEvent.StatusChanged(status.Current.Kind, sequence)], null),
            AssignmentChanged => ([AutomationEvent.AssignmentChanged(sequence), AutomationEvent.ConversationUpdated(ConversationUpdateField.Assignee, sequence)], null),
            TeamChanged => ([AutomationEvent.AssignmentChanged(sequence), AutomationEvent.ConversationUpdated(ConversationUpdateField.Team, sequence)], null),
            PriorityChanged => ([AutomationEvent.ConversationUpdated(ConversationUpdateField.Priority, sequence)], null),
            LabelsChanged => ([AutomationEvent.ConversationUpdated(ConversationUpdateField.Labels, sequence)], null),
            AttributeChanged => ([AutomationEvent.ConversationUpdated(ConversationUpdateField.Attributes, sequence)], null),
            ParticipantChanged => ([AutomationEvent.ConversationUpdated(ConversationUpdateField.Participants, sequence)], null),
            ExtensionEvent extension => (IsApiClient(timelineEvent.Actor, slaWorker) ? Sla(extension, sequence) : [], null),
            NoteEvent or ContactChanged or SystemEvent => ([], null),
        };
    }

    /// <summary>True when the actor is the automation API client (the event was caused by a rule execution).</summary>
    public static bool IsAutomationActor(AccessActor actor, ApiClientId client) => IsApiClient(actor, client);

    private static bool IsApiClient(AccessActor actor, ApiClientId client) =>
        actor.Kind == AccessActorKind.ApiClient && client != default && string.Equals(actor.Id, client.Value, StringComparison.Ordinal);

    private static IReadOnlyList<AutomationEvent> Sla(ExtensionEvent extension, long sequence)
    {
        if (!SlaEventCodec.TryDecode(extension, out var decoded))
            return [];
        return decoded switch
        {
            SlaWarningEvent warning => [AutomationEvent.SlaEvent(SlaEventKind.Warning, warning.Metric, warning.Actions, sequence)],
            SlaBreachedEvent breached => [AutomationEvent.SlaEvent(SlaEventKind.Breached, breached.Metric, breached.Actions, sequence)],
            SlaAppliedEvent or SlaMetEvent => [],
        };
    }
}
