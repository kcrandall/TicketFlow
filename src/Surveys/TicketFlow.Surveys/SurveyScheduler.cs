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
        var surveys = await store.ListEnabledSurveysAsync(notification.Scope, SurveyTrigger.ConversationResolved, notification.Inbox, cancellationToken).ConfigureAwait(false);
        SurveyDefinition? csat = null;
        SurveyDefinition? nps = null;
        for (var i = 0; i < surveys.Count; i++)
        {
            var s = surveys[i].Survey;
            if (!s.Enabled || s.Policy.Trigger != SurveyTrigger.ConversationResolved || (s.Inbox is not null && s.Inbox != notification.Inbox))
                continue;
            if (s.Kind == SurveyKind.Csat)
            {
                if (csat is null || IsBetter(s, csat))
                    csat = s;
            }
            else if (s.Kind == SurveyKind.Nps)
            {
                if (nps is null || IsBetter(s, nps))
                    nps = s;
            }
        }
        var selectedCount = (csat is not null ? 1 : 0) + (nps is not null ? 1 : 0);
        if (selectedCount == 0)
            return new SurveySchedulingResult([], []);
        var selected = selectedCount == 2 ? new[] { csat!, nps! } : [csat ?? nps!];

        var created = new List<StoredInvitation>(selected.Length);
        var skipped = new List<SurveySkip>();
        for (var i = 0; i < selected.Length; i++)
        {
            var survey = selected[i];
            var locale = survey.DefaultLocale;
            if (notification.Locale is { } wanted)
                for (var q = 0; q < survey.Questions.Count; q++)
                    if (survey.Questions[q].Locale == wanted)
                    {
                        locale = wanted;
                        break;
                    }
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

    private static bool IsBetter(SurveyDefinition candidate, SurveyDefinition current)
    {
        var candRank = candidate.Inbox is null ? 1 : 0;
        var currRank = current.Inbox is null ? 1 : 0;
        return candRank != currRank ? candRank < currRank : candidate.Id.CompareTo(current.Id) < 0;
    }
}
