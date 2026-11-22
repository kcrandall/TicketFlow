using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Surveys;

namespace TicketFlow.Events.Tests;

/// <summary>Holds recorded responses for the survey follower and hydrator; every other member is unsupported.</summary>
internal sealed class FakeSurveyStore : ISurveyStore
{
    private readonly List<StoredResponse> responses = [];

    public SurveyResponse Add(TicketScope scope, string conversation, int score, string? comment, DateTimeOffset at, string? team = null)
    {
        var response = new SurveyResponse(new SurveyInvitationId(Guid.NewGuid()), new SurveyId("csat"), SurveyKind.Csat, score, comment, at, new ConversationId(conversation),
            new ContactId("ada"), new InboxId("email"), team is null ? null : new TeamId(team), new UserId("agent"));
        lock (responses)
            responses.Add(new StoredResponse(scope, response));
        return response;
    }

    public Task<SurveyResponsePage> ListResponsesAsync(TicketScope scope, SurveyResponseQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        lock (responses)
        {
            var ordered = responses.Where(item => item.Scope == scope && item.Response.RespondedAt >= query.From && item.Response.RespondedAt < query.To)
                .OrderByDescending(item => item.Response.RespondedAt).ThenByDescending(item => item.Response.Invitation.Value).ToList();
            if (query.After is { } after)
                ordered = [.. ordered.SkipWhile(item => item.Response.RespondedAt > after.RespondedAt
                    || item.Response.RespondedAt == after.RespondedAt && item.Response.Invitation.Value.CompareTo(after.Invitation.Value) >= 0)];
            var page = ordered.Take(query.Limit).ToList();
            var next = ordered.Count > query.Limit ? new SurveyResponseCursor(page[^1].Response.RespondedAt, page[^1].Response.Invitation) : null;
            return Task.FromResult(new SurveyResponsePage(page, next));
        }
    }

    public Task<StoredResponse?> GetResponseAsync(TicketScope scope, SurveyInvitationId invitation, CancellationToken cancellationToken = default)
    {
        lock (responses)
            return Task.FromResult(responses.FirstOrDefault(item => item.Scope == scope && item.Response.Invitation == invitation));
    }

    public Task<StoredSurvey?> GetSurveyAsync(TicketScope scope, SurveyId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<StoredSurvey>> ListSurveysAsync(TicketScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SurveyWriteResult<StoredSurvey>> CreateSurveyAsync(TicketScope scope, SurveyDefinition survey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SurveyWriteResult<StoredSurvey>> UpdateSurveyAsync(TicketScope scope, SurveyDefinition survey, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SurveyWriteResult<StoredSurvey>> DeleteSurveyAsync(TicketScope scope, SurveyId id, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<InvitationCreateResult> CreateInvitationAsync(TicketScope scope, SurveyInvitation invitation, TimeSpan contactCooldown, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<StoredInvitation?> GetInvitationAsync(TicketScope scope, SurveyInvitationId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<StoredInvitation?> FindInvitationByTokenAsync(TicketScope scope, SurveyTokenHash tokenHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<StoredInvitation>> ListDueInvitationsAsync(TicketScope scope, DateTimeOffset now, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<StoredInvitation>> ClaimDueInvitationsAsync(TicketScope scope, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SurveyWriteResult<StoredInvitation>> UpdateInvitationAsync(TicketScope scope, SurveyInvitation invitation, long expectedRevision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ResponseRecordResult> RecordResponseAsync(TicketScope scope, SurveyResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SurveyAggregateResult> AggregateAsync(TicketScope scope, SurveyAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
