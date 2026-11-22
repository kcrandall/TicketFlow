using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>
/// Default <see cref="ISurveyResponder"/>. Tokens are parsed strictly, hashed and looked up within the host-resolved scope; unknown,
/// malformed, unissued and cancelled tokens are indistinguishable. Tokens stay valid across delivery retries. Responses are single-use and idempotent: replaying the same score and comment
/// returns <see cref="SurveyResponseDuplicate"/>, anything else after the first response is refused.
/// </summary>
public sealed class SurveyResponder(ISurveyStore store, TimeProvider clock) : ISurveyResponder
{
    private readonly ISurveyStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<SurveyInvitationView?> GetInvitationAsync(TicketScope scope, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var stored = await FindAsync(scope, token, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return null;
        var invitation = stored.Invitation;
        var responded = invitation.State == SurveyInvitationState.Responded;
        if (!responded && !invitation.AcceptsResponses(clock.GetUtcNow()))
            return null;
        var survey = (await store.GetSurveyAsync(scope, invitation.Survey, cancellationToken).ConfigureAwait(false))?.Survey;
        if (survey is null)
            return null;
        var question = survey.QuestionFor(invitation.Locale);
        return new SurveyInvitationView(invitation.Kind, SurveyScales.Minimum(invitation.Kind), SurveyScales.Maximum(invitation.Kind), question.Locale, question.Text,
            question.CommentPrompt, invitation.ExpiresAt!.Value, responded);
    }

    public async Task<SurveyResponseResult> RespondAsync(TicketScope scope, string token, int score, string? comment = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var stored = await FindAsync(scope, token, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return new SurveyResponseRejected(SurveyResponseRejection.InvalidToken);
        var invitation = stored.Invitation;
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment;
        if (invitation.State == SurveyInvitationState.Responded)
            return await ReplayAsync(scope, invitation.Id, score, comment, cancellationToken).ConfigureAwait(false);
        if (!SurveyScales.IsValid(invitation.Kind, score))
            return new SurveyResponseRejected(SurveyResponseRejection.InvalidScore);
        if (comment is not null && !SurveyText.IsMultiline(comment, SurveyResponse.MaximumCommentLength))
            return new SurveyResponseRejected(SurveyResponseRejection.InvalidComment);
        var now = clock.GetUtcNow();
        if (!invitation.AcceptsResponses(now))
            return new SurveyResponseRejected(SurveyResponseRejection.Expired);
        var result = await store.RecordResponseAsync(scope, SurveyResponse.For(invitation, score, comment, now), cancellationToken).ConfigureAwait(false);
        return result switch
        {
            ResponseRecorded recorded => new SurveyResponseAccepted(recorded.Response.Response),
            ResponseAlreadyRecorded existing => Replay(existing.Existing.Response, score, comment),
            ResponseInvitationNotFound => new SurveyResponseRejected(SurveyResponseRejection.InvalidToken),
            ResponseInvitationExpired => new SurveyResponseRejected(SurveyResponseRejection.Expired),
        };
    }

    private async Task<SurveyResponseResult> ReplayAsync(TicketScope scope, SurveyInvitationId invitation, int score, string? comment, CancellationToken cancellationToken) =>
        await store.GetResponseAsync(scope, invitation, cancellationToken).ConfigureAwait(false) is { } existing
            ? Replay(existing.Response, score, comment)
            : new SurveyResponseRejected(SurveyResponseRejection.AlreadyResponded);

    private static SurveyResponseResult Replay(SurveyResponse existing, int score, string? comment) =>
        existing.Score == score && existing.Comment == comment
            ? new SurveyResponseDuplicate(existing)
            : new SurveyResponseRejected(SurveyResponseRejection.AlreadyResponded);

    private async Task<StoredInvitation?> FindAsync(TicketScope scope, string token, CancellationToken cancellationToken)
    {
        if (!SurveyToken.TryParse(token, out var parsed))
            return null;
        var stored = await store.FindInvitationByTokenAsync(scope, parsed.Hash(), cancellationToken).ConfigureAwait(false);
        // A token is valid from first issue (even while a retry is pending) until the invitation is cancelled.
        return stored is { Invitation.TokenHash: not null } && stored.Invitation.State != SurveyInvitationState.Cancelled ? stored : null;
    }
}
