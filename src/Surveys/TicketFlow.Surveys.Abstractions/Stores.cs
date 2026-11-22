using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>
/// Durable survey state. Contract:
/// <list type="bullet">
/// <item>Every operation is confined to one <see cref="TicketScope"/>; creates start at revision 1; updates and deletes are
/// compare-and-set on revision. At most <see cref="SurveyLimits.MaximumSurveysPerWorkspace"/> surveys per scope; a survey with
/// invitations cannot be deleted (<see cref="SurveyWriteRejection.InUse"/>).</item>
/// <item><see cref="CreateInvitationAsync"/> is atomic and idempotent: an existing invitation for the same (survey, conversation)
/// is returned as <see cref="InvitationDuplicate"/>; an invitation of the same (survey, contact) created after
/// <c>invitation.CreatedAt - cooldown</c> yields <see cref="InvitationCoolingDown"/>. Concurrent creates for one contact serialize
/// so the cooldown cannot be raced.</item>
/// <item>Invitation updates may not change survey, kind, conversation or contact. Token hashes are unique; lookups by hash are
/// confined to the scope.</item>
/// <item><see cref="ClaimDueInvitationsAsync"/> atomically claims due invitations — Pending with <c>SendAfter ≤ now</c>, or Sending
/// whose lease expired — skipping rows another client is claiming, so concurrent dispatchers never claim the same invitation; claimed
/// invitations become Sending with a lease and one more attempt.</item>
/// <item><see cref="RecordResponseAsync"/> atomically stores the single response and moves the invitation to
/// <see cref="SurveyInvitationState.Responded"/> when <see cref="SurveyInvitation.AcceptsResponses"/> holds at the response time;
/// a second response returns the first.</item>
/// <item>Response lists and aggregates include exactly the responses for which
/// <see cref="AccessFilterEvaluator.Matches"/>(filter, <see cref="SurveyResources.Response"/>) is true, applying the filter in the
/// query; a store that cannot apply a filter node must return no rows.</item>
/// </list>
/// </summary>
public interface ISurveyStore
{
    Task<StoredSurvey?> GetSurveyAsync(TicketScope scope, SurveyId id, CancellationToken cancellationToken = default);

    /// <summary>Every survey in the scope (bounded), ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredSurvey>> ListSurveysAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<SurveyWriteResult<StoredSurvey>> CreateSurveyAsync(TicketScope scope, SurveyDefinition survey, CancellationToken cancellationToken = default);

    Task<SurveyWriteResult<StoredSurvey>> UpdateSurveyAsync(TicketScope scope, SurveyDefinition survey, long expectedRevision, CancellationToken cancellationToken = default);

    Task<SurveyWriteResult<StoredSurvey>> DeleteSurveyAsync(TicketScope scope, SurveyId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<InvitationCreateResult> CreateInvitationAsync(TicketScope scope, SurveyInvitation invitation, TimeSpan contactCooldown, CancellationToken cancellationToken = default);

    Task<StoredInvitation?> GetInvitationAsync(TicketScope scope, SurveyInvitationId id, CancellationToken cancellationToken = default);

    Task<StoredInvitation?> FindInvitationByTokenAsync(TicketScope scope, SurveyTokenHash tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Due invitations (Pending with <c>SendAfter ≤ now</c>, or Sending with <c>LeaseUntil ≤ now</c>), ordered by (send after,
    /// identifier), at most <paramref name="limit"/> (1-500). Read-only; dispatchers use <see cref="ClaimDueInvitationsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<StoredInvitation>> ListDueInvitationsAsync(TicketScope scope, DateTimeOffset now, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims at most <paramref name="limit"/> (1-500) due invitations in (send after, identifier) order: each becomes
    /// <see cref="SurveyInvitationState.Sending"/> with <c>LeaseUntil = now + lease</c>, attempts + 1 and revision + 1. Invitations at
    /// <see cref="SurveyInvitation.MaximumAttempts"/> are not claimed. Rows locked by a concurrent claim are skipped.
    /// </summary>
    Task<IReadOnlyList<StoredInvitation>> ClaimDueInvitationsAsync(TicketScope scope, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken = default);

    Task<SurveyWriteResult<StoredInvitation>> UpdateInvitationAsync(TicketScope scope, SurveyInvitation invitation, long expectedRevision, CancellationToken cancellationToken = default);

    Task<ResponseRecordResult> RecordResponseAsync(TicketScope scope, SurveyResponse response, CancellationToken cancellationToken = default);

    Task<StoredResponse?> GetResponseAsync(TicketScope scope, SurveyInvitationId invitation, CancellationToken cancellationToken = default);

    Task<SurveyResponsePage> ListResponsesAsync(TicketScope scope, SurveyResponseQuery query, AccessFilter filter, CancellationToken cancellationToken = default);

    Task<SurveyAggregateResult> AggregateAsync(TicketScope scope, SurveyAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default);
}
