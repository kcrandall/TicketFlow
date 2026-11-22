using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>
/// Default <see cref="ISurveyDispatcher"/>. For each due invitation it issues a fresh <see cref="SurveyToken"/>, records only its
/// hash (state Sent, expiry = now + lifetime) with a revision-fenced update, then calls <see cref="ISurveyInvitationSender"/>. When
/// the sender throws, the invitation returns to Pending without a token (exponential backoff) or becomes Failed after
/// <see cref="SurveyOptions.MaximumSendAttempts"/>. Delivery is at-most-once if the process stops between recording the hash and
/// sending. Invitations of disabled or deleted surveys are cancelled. Concurrent dispatchers are safe: the fenced update lets only
/// one of them issue a token.
/// </summary>
public sealed class SurveyDispatcher : ISurveyDispatcher
{
    private readonly ISurveyStore store;
    private readonly ISurveyInvitationSender sender;
    private readonly TimeProvider clock;
    private readonly SurveyOptions options;

    public SurveyDispatcher(ISurveyStore store, ISurveyInvitationSender sender, TimeProvider clock, IOptions<SurveyOptions> options)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.sender = sender ?? throw new ArgumentNullException(nameof(sender));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
    }

    public async Task<SurveyDispatchSummary> DispatchDueAsync(TicketScope scope, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, SurveyLimits.MaximumDuePerRead);
        var due = await store.ListDueInvitationsAsync(scope, clock.GetUtcNow(), limit, cancellationToken).ConfigureAwait(false);
        var surveys = new Dictionary<SurveyId, SurveyDefinition?>();
        int sent = 0, retrying = 0, failed = 0, cancelled = 0;
        foreach (var stored in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invitation = stored.Invitation;
            if (!surveys.TryGetValue(invitation.Survey, out var survey))
            {
                survey = (await store.GetSurveyAsync(scope, invitation.Survey, cancellationToken).ConfigureAwait(false))?.Survey;
                surveys[invitation.Survey] = survey;
            }
            if (survey is null || !survey.Enabled || survey.Kind != invitation.Kind)
            {
                if (await store.UpdateInvitationAsync(scope, invitation.Close(SurveyInvitationState.Cancelled, invitation.Attempts), stored.Revision, cancellationToken).ConfigureAwait(false) is { Value: SurveyWriteApplied<StoredInvitation> })
                    cancelled++;
                continue;
            }

            var token = SurveyToken.Create();
            var now = clock.GetUtcNow();
            var issued = invitation.MarkSent(token.Hash(), now, survey.Policy.InvitationLifetime);
            if (await store.UpdateInvitationAsync(scope, issued, stored.Revision, cancellationToken).ConfigureAwait(false) is not { Value: SurveyWriteApplied<StoredInvitation> applied })
                continue;
            try
            {
                var message = new SurveyInvitationMessage(scope, applied.Value.Invitation, token, survey.Kind, SurveyScales.Minimum(survey.Kind), SurveyScales.Maximum(survey.Kind), survey.QuestionFor(invitation.Locale));
                await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = issued.Attempts;
                var next = attempts >= options.MaximumSendAttempts
                    ? issued.Close(SurveyInvitationState.Failed, attempts)
                    : issued.Retry(clock.GetUtcNow() + Backoff(attempts), attempts);
                if (await store.UpdateInvitationAsync(scope, next, applied.Value.Revision, CancellationToken.None).ConfigureAwait(false) is { Value: SurveyWriteApplied<StoredInvitation> })
                {
                    if (next.State == SurveyInvitationState.Failed)
                        failed++;
                    else
                        retrying++;
                }
            }
        }
        return new SurveyDispatchSummary(sent, retrying, failed, cancelled);
    }

    private TimeSpan Backoff(int attempts)
    {
        var delay = options.RetryDelay * Math.Pow(2, Math.Min(attempts - 1, 20));
        return delay > options.MaximumRetryDelay ? options.MaximumRetryDelay : delay;
    }
}
