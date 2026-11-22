namespace TicketFlow.Surveys;

/// <summary>
/// Default <see cref="ISurveyScheduler"/>. Trusted, internal path (no principal): it runs on the conversation outbox, not on user
/// requests. For each survey kind, the enabled survey bound to the conversation's inbox wins over the enabled workspace survey;
/// the store makes creation idempotent per (survey, conversation) and enforces the per-contact cooldown atomically.
/// </summary>
public sealed class SurveyScheduler(ISurveyStore store) : ISurveyScheduler
{
    private readonly ISurveyStore store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<SurveySchedulingResult> HandleResolvedAsync(ConversationResolvedNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var surveys = await store.ListSurveysAsync(notification.Scope, cancellationToken).ConfigureAwait(false);
        var selected = surveys
            .Select(stored => stored.Survey)
            .Where(survey => survey.Enabled && survey.Policy.Trigger == SurveyTrigger.ConversationResolved && (survey.Inbox is null || survey.Inbox == notification.Inbox))
            .GroupBy(survey => survey.Kind)
            .Select(kind => kind.OrderBy(survey => survey.Inbox is null ? 1 : 0).ThenBy(survey => survey.Id).First())
            .OrderBy(survey => survey.Kind);

        var created = new List<StoredInvitation>();
        var skipped = new List<SurveySkip>();
        foreach (var survey in selected)
        {
            var locale = notification.Locale is { } wanted && survey.Questions.Any(question => question.Locale == wanted) ? wanted : survey.DefaultLocale;
            var invitation = new SurveyInvitation(SurveyInvitationId.New(), survey.Id, survey.Kind, notification.Conversation, notification.Contact,
                notification.Inbox, notification.Team, notification.Agent, locale, notification.ResolvedAt, notification.ResolvedAt + survey.Policy.Delay);
            var result = await store.CreateInvitationAsync(notification.Scope, invitation, survey.Policy.ContactCooldown, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case InvitationCreated added:
                    created.Add(added.Invitation);
                    break;
                case InvitationDuplicate:
                    skipped.Add(new SurveySkip(survey.Id, SurveySkipReason.Duplicate));
                    break;
                case InvitationCoolingDown:
                    skipped.Add(new SurveySkip(survey.Id, SurveySkipReason.CoolingDown));
                    break;
                case InvitationRejected:
                    skipped.Add(new SurveySkip(survey.Id, SurveySkipReason.Rejected));
                    break;
            }
        }
        return new SurveySchedulingResult(created, skipped);
    }
}
