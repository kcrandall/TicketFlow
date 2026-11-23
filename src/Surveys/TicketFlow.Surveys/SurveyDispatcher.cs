using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>
/// Default <see cref="ISurveyDispatcher"/>.
/// <list type="number">
/// <item>Claims due invitations atomically (<see cref="ISurveyStore.ClaimDueInvitationsAsync"/>): concurrent dispatchers never claim the
/// same invitation, and each claim holds a lease of <see cref="SurveyOptions.SendLease"/>.</item>
/// <item>Derives the invitation's token deterministically from <see cref="SurveyOptions.TokenKey"/> (<see cref="SurveyToken.Derive"/>)
/// and records its hash and expiry on first issue. Retries derive the same token, so a link that already reached the contact (for
/// example when the provider accepted but the sender then failed) keeps working; the database never holds the raw token.</item>
/// <item>Calls <see cref="ISurveyInvitationSender"/> with the token and the stable <see cref="SurveyInvitationMessage.DeliveryKey"/>,
/// then marks the invitation Sent. A throwing sender returns it to Pending with exponential backoff, or Failed after
/// <see cref="SurveyOptions.MaximumSendAttempts"/>; the token stays valid either way.</item>
/// </list>
/// If the process stops after claiming (before or after the sender call), the lease expires and the invitation is due again; the
/// re-send carries the same token and delivery key, so delivery is at-least-once with provider-side deduplication. Invitations of
/// disabled or deleted surveys are cancelled; invitations past their response window are failed.
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
        var claimed = await store.ClaimDueInvitationsAsync(scope, clock.GetUtcNow(), options.SendLease, limit, cancellationToken).ConfigureAwait(false);
        var surveys = new Dictionary<SurveyId, SurveyDefinition?>();
        int sent = 0, retrying = 0, failed = 0, cancelled = 0;
        foreach (var stored in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var invitation = stored.Invitation;
            var revision = stored.Revision;
            if (!surveys.TryGetValue(invitation.Survey, out var survey))
            {
                survey = (await store.GetSurveyAsync(scope, invitation.Survey, cancellationToken).ConfigureAwait(false))?.Survey;
                surveys[invitation.Survey] = survey;
            }
            var now = clock.GetUtcNow();
            if (survey is null || !survey.Enabled || survey.Kind != invitation.Kind)
            {
                if (await WriteAsync(scope, invitation.Close(SurveyInvitationState.Cancelled), revision).ConfigureAwait(false) is not null)
                    cancelled++;
                continue;
            }
            if (invitation.Attempts > options.MaximumSendAttempts || invitation.ExpiresAt <= now)
            {
                if (await WriteAsync(scope, invitation.Close(SurveyInvitationState.Failed), revision).ConfigureAwait(false) is not null)
                    failed++;
                continue;
            }

            var token = SurveyToken.Derive(options.TokenKey, scope, invitation.Id);
            var tokenHash = token.Hash();
            if (invitation.TokenHash != tokenHash)
            {
                // First issue (or a changed TokenKey, which cannot reproduce the earlier link).
                var issued = invitation.Issue(tokenHash, now, survey.Policy.InvitationLifetime);
                if (await WriteAsync(scope, issued, revision).ConfigureAwait(false) is not { } applied)
                    continue;
                (invitation, revision) = (issued, applied.Revision);
            }
            try
            {
                var message = new SurveyInvitationMessage(scope, invitation, token, survey.Kind, SurveyScales.Minimum(survey.Kind), SurveyScales.Maximum(survey.Kind), survey.QuestionFor(invitation.Locale));
                await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var next = invitation.Attempts >= options.MaximumSendAttempts
                    ? invitation.Close(SurveyInvitationState.Failed)
                    : invitation.Retry(clock.GetUtcNow() + Backoff(invitation.Attempts));
                if (await WriteAsync(scope, next, revision).ConfigureAwait(false) is not null)
                {
                    if (next.State == SurveyInvitationState.Failed)
                        failed++;
                    else
                        retrying++;
                }
                continue;
            }
            // A conflict here means a response arrived (or the lease was lost) meanwhile; the invitation was still delivered.
            await WriteAsync(scope, invitation.MarkSent(), revision).ConfigureAwait(false);
            sent++;
        }
        return new SurveyDispatchSummary(sent, retrying, failed, cancelled);
    }

    /// <summary>Revision-fenced write; returns the stored invitation or null when another writer got there first.</summary>
    private async Task<StoredInvitation?> WriteAsync(TicketScope scope, SurveyInvitation invitation, long revision) =>
        await store.UpdateInvitationAsync(scope, invitation, revision, CancellationToken.None).ConfigureAwait(false) is { Value: SurveyWriteApplied<StoredInvitation> applied }
            ? applied.Value
            : null;

    private TimeSpan Backoff(int attempts)
    {
        var delay = options.RetryDelay * Math.Pow(2, Math.Clamp(attempts - 1, 0, 20));
        return delay > options.MaximumRetryDelay ? options.MaximumRetryDelay : delay;
    }
}
