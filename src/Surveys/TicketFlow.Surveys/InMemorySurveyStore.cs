using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>
/// Development-only <see cref="ISurveyStore"/>. State is process-local, bounded, lost on restart and never shared between processes;
/// every operation runs under one lock. Lists and aggregates evaluate the <see cref="AccessFilter"/> with
/// <see cref="AccessFilterEvaluator"/> over <see cref="SurveyResources.Response"/>.
/// </summary>
public sealed class InMemorySurveyStore : ISurveyStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<TicketScope, ScopeState> scopes = [];
    private readonly int maximumRecords;
    private int records;

    /// <param name="maximumRecords">Global cap on surveys, invitations and responses together.</param>
    public InMemorySurveyStore(int maximumRecords = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecords, 1);
        this.maximumRecords = maximumRecords;
    }

    private sealed class ScopeState
    {
        public SortedDictionary<SurveyId, StoredSurvey> Surveys { get; } = [];
        public Dictionary<SurveyInvitationId, StoredInvitation> Invitations { get; } = [];
        public Dictionary<(SurveyId, ConversationId), SurveyInvitationId> ByConversation { get; } = [];
        public Dictionary<(SurveyId, ContactId), DateTimeOffset> LastInvited { get; } = [];
        public Dictionary<SurveyTokenHash, SurveyInvitationId> ByToken { get; } = [];
        public Dictionary<SurveyInvitationId, StoredResponse> Responses { get; } = [];
    }

    public Task<StoredSurvey?> GetSurveyAsync(TicketScope scope, SurveyId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        lock (gate)
            return Task.FromResult(Find(scope)?.Surveys.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<StoredSurvey>> ListSurveysAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredSurvey>>(Find(scope) is { } state ? [.. state.Surveys.Values] : []);
    }

    public Task<StoreWriteResult<StoredSurvey, SurveyWriteRejection>> CreateSurveyAsync(TicketScope scope, SurveyDefinition survey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(survey);
        lock (gate)
        {
            var state = State(scope);
            if (state.Surveys.TryGetValue(survey.Id, out var existing))
                return Write<StoredSurvey>(new StoreConflict(existing.Revision));
            if (records >= maximumRecords || state.Surveys.Count >= SurveyLimits.MaximumSurveysPerWorkspace)
            {
                Prune(scope, state);
                return Write<StoredSurvey>(new StoreRejected<SurveyWriteRejection>(SurveyWriteRejection.CapacityExceeded));
            }
            var stored = new StoredSurvey(scope, survey, 1);
            state.Surveys.Add(survey.Id, stored);
            records++;
            return Write<StoredSurvey>(new StoreApplied<StoredSurvey>(stored));
        }
    }

    public Task<StoreWriteResult<StoredSurvey, SurveyWriteRejection>> UpdateSurveyAsync(TicketScope scope, SurveyDefinition survey, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Surveys.TryGetValue(survey.Id, out var existing))
                return Write<StoredSurvey>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Write<StoredSurvey>(new StoreConflict(existing.Revision));
            var stored = new StoredSurvey(scope, survey, checked(existing.Revision + 1));
            state.Surveys[survey.Id] = stored;
            return Write<StoredSurvey>(new StoreApplied<StoredSurvey>(stored));
        }
    }

    public Task<StoreWriteResult<StoredSurvey, SurveyWriteRejection>> DeleteSurveyAsync(TicketScope scope, SurveyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Surveys.TryGetValue(id, out var existing))
                return Write<StoredSurvey>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Write<StoredSurvey>(new StoreConflict(existing.Revision));
            if (state.Invitations.Values.Any(invitation => invitation.Invitation.Survey == id))
                return Write<StoredSurvey>(new StoreRejected<SurveyWriteRejection>(SurveyWriteRejection.InUse));
            state.Surveys.Remove(id);
            records--;
            Prune(scope, state);
            return Write<StoredSurvey>(new StoreApplied<StoredSurvey>(existing));
        }
    }

    public Task<InvitationCreateResult> CreateInvitationAsync(TicketScope scope, SurveyInvitation invitation, TimeSpan contactCooldown, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentOutOfRangeException.ThrowIfLessThan(contactCooldown, TimeSpan.Zero);
        if (invitation.State != SurveyInvitationState.Pending)
            throw new ArgumentException("New invitations must be pending.", nameof(invitation));
        lock (gate)
        {
            var state = State(scope);
            if (state.ByConversation.TryGetValue((invitation.Survey, invitation.Conversation), out var duplicate))
                return Task.FromResult<InvitationCreateResult>(new InvitationDuplicate(state.Invitations[duplicate]));
            if (state.Invitations.ContainsKey(invitation.Id))
                throw new InvalidOperationException("The invitation identifier is already in use.");
            if (state.LastInvited.TryGetValue((invitation.Survey, invitation.Contact), out var last) && last > invitation.CreatedAt - contactCooldown)
            {
                Prune(scope, state);
                return Task.FromResult<InvitationCreateResult>(new InvitationCoolingDown(last));
            }
            if (records >= maximumRecords)
            {
                Prune(scope, state);
                return Task.FromResult<InvitationCreateResult>(new InvitationRejected(SurveyWriteRejection.CapacityExceeded));
            }
            var stored = new StoredInvitation(scope, invitation, 1);
            state.Invitations.Add(invitation.Id, stored);
            state.ByConversation.Add((invitation.Survey, invitation.Conversation), invitation.Id);
            state.LastInvited[(invitation.Survey, invitation.Contact)] = last > invitation.CreatedAt ? last : invitation.CreatedAt;
            records++;
            return Task.FromResult<InvitationCreateResult>(new InvitationCreated(stored));
        }
    }

    public Task<StoredInvitation?> GetInvitationAsync(TicketScope scope, SurveyInvitationId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        lock (gate)
            return Task.FromResult(Find(scope)?.Invitations.GetValueOrDefault(id));
    }

    public Task<StoredInvitation?> FindInvitationByTokenAsync(TicketScope scope, SurveyTokenHash tokenHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(tokenHash.IsInitialized, nameof(tokenHash));
        lock (gate)
        {
            var state = Find(scope);
            return Task.FromResult(state is not null && state.ByToken.TryGetValue(tokenHash, out var id) ? state.Invitations[id] : null);
        }
    }

    public Task<IReadOnlyList<StoredInvitation>> ListDueInvitationsAsync(TicketScope scope, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, SurveyLimits.MaximumDuePerRead);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredInvitation>>(Due(scope, now, limit));
    }

    public Task<IReadOnlyList<StoredInvitation>> ClaimDueInvitationsAsync(TicketScope scope, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, SurveyLimits.MaximumDuePerRead);
        lock (gate)
        {
            var claimed = new List<StoredInvitation>();
            foreach (var stored in Due(scope, now, limit))
            {
                var next = new StoredInvitation(scope, stored.Invitation.Claim(now + lease), checked(stored.Revision + 1));
                scopes[scope].Invitations[stored.Invitation.Id] = next;
                claimed.Add(next);
            }
            return Task.FromResult<IReadOnlyList<StoredInvitation>>(claimed);
        }
    }

    private List<StoredInvitation> Due(TicketScope scope, DateTimeOffset now, int limit) =>
        [.. (Find(scope)?.Invitations.Values ?? Enumerable.Empty<StoredInvitation>())
            .Where(stored => stored.Invitation.Attempts < SurveyInvitation.MaximumAttempts
                && ((stored.Invitation.State == SurveyInvitationState.Pending && stored.Invitation.SendAfter <= now)
                    || (stored.Invitation.State == SurveyInvitationState.Sending && stored.Invitation.LeaseUntil <= now)))
            .OrderBy(stored => stored.Invitation.SendAfter).ThenBy(stored => stored.Invitation.Id)
            .Take(limit)];

    public Task<StoreWriteResult<StoredInvitation, SurveyWriteRejection>> UpdateInvitationAsync(TicketScope scope, SurveyInvitation invitation, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Invitations.TryGetValue(invitation.Id, out var existing))
                return Write<StoredInvitation>(StoreNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Write<StoredInvitation>(new StoreConflict(existing.Revision));
            var old = existing.Invitation;
            if (old.Survey != invitation.Survey || old.Kind != invitation.Kind || old.Conversation != invitation.Conversation || old.Contact != invitation.Contact
                || (invitation.TokenHash is { } taken && state.ByToken.TryGetValue(taken, out var holder) && holder != invitation.Id))
                return Write<StoredInvitation>(new StoreRejected<SurveyWriteRejection>(SurveyWriteRejection.InUse));
            if (old.TokenHash is { } previous)
                state.ByToken.Remove(previous);
            if (invitation.TokenHash is { } hash)
                state.ByToken[hash] = invitation.Id;
            var stored = new StoredInvitation(scope, invitation, checked(existing.Revision + 1));
            state.Invitations[invitation.Id] = stored;
            return Write<StoredInvitation>(new StoreApplied<StoredInvitation>(stored));
        }
    }

    public Task<ResponseRecordResult> RecordResponseAsync(TicketScope scope, SurveyResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Invitations.TryGetValue(response.Invitation, out var stored))
                return Task.FromResult<ResponseRecordResult>(ResponseInvitationNotFound.Instance);
            if (state.Responses.TryGetValue(response.Invitation, out var existing))
                return Task.FromResult<ResponseRecordResult>(new ResponseAlreadyRecorded(existing));
            var invitation = stored.Invitation;
            if (invitation.TokenHash is null || invitation.State is SurveyInvitationState.Responded or SurveyInvitationState.Cancelled || !Matches(invitation, response))
                return Task.FromResult<ResponseRecordResult>(ResponseInvitationNotFound.Instance);
            if (!invitation.AcceptsResponses(response.RespondedAt))
                return Task.FromResult<ResponseRecordResult>(new ResponseInvitationExpired(invitation.ExpiresAt!.Value));
            var responded = new SurveyInvitation(invitation.Id, invitation.Survey, invitation.Kind, invitation.Conversation, invitation.Contact, invitation.Inbox, invitation.Team,
                invitation.Agent, invitation.Locale, invitation.CreatedAt, invitation.SendAfter, SurveyInvitationState.Responded, invitation.Attempts, invitation.TokenHash,
                invitation.IssuedAt, invitation.ExpiresAt);
            state.Invitations[invitation.Id] = new StoredInvitation(scope, responded, checked(stored.Revision + 1));
            var recorded = new StoredResponse(scope, response);
            state.Responses.Add(invitation.Id, recorded);
            records++;
            return Task.FromResult<ResponseRecordResult>(new ResponseRecorded(recorded));
        }
    }

    public Task<StoredResponse?> GetResponseAsync(TicketScope scope, SurveyInvitationId invitation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(invitation.IsInitialized, nameof(invitation));
        lock (gate)
            return Task.FromResult(Find(scope)?.Responses.GetValueOrDefault(invitation));
    }

    public Task<SurveyResponsePage> ListResponsesAsync(TicketScope scope, SurveyResponseQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            var page = Responses(scope, filter, query.From, query.To, query.Survey)
                .Where(stored => query.After is not { } after || stored.Response.RespondedAt < after.RespondedAt
                    || (stored.Response.RespondedAt == after.RespondedAt && stored.Response.Invitation.CompareTo(after.Invitation) < 0))
                .OrderByDescending(stored => stored.Response.RespondedAt).ThenByDescending(stored => stored.Response.Invitation)
                .Take(query.Limit + 1)
                .ToList();
            if (page.Count <= query.Limit)
                return Task.FromResult(new SurveyResponsePage(page, null));
            var items = page[..query.Limit];
            return Task.FromResult(new SurveyResponsePage(items, new SurveyResponseCursor(items[^1].Response.RespondedAt, items[^1].Response.Invitation)));
        }
    }

    public Task<SurveyAggregateResult> AggregateAsync(TicketScope scope, SurveyAggregateQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        lock (gate)
        {
            var groups = Responses(scope, filter, query.From, query.To, query.Survey)
                .Select(stored => stored.Response)
                .Where(response => response.Kind == query.Kind)
                .GroupBy(response => (Bucket: query.BucketStart(response.RespondedAt), Group: query.GroupKey(response)))
                .OrderBy(group => group.Key.Bucket).ThenBy(group => group.Key.Group, StringComparer.Ordinal)
                .Take(SurveyAggregateQuery.MaximumRows + 1)
                .Select(group => new SurveyAggregateRow(query.Kind, group.Key.Bucket, group.Key.Group, group.LongCount(),
                    group.LongCount(response => SurveyScales.Sentiment(query.Kind, response.Score) > 0),
                    group.LongCount(response => SurveyScales.Sentiment(query.Kind, response.Score) == 0),
                    group.LongCount(response => SurveyScales.Sentiment(query.Kind, response.Score) < 0),
                    group.Sum(response => (long)response.Score)))
                .ToList();
            var truncated = groups.Count > SurveyAggregateQuery.MaximumRows;
            return Task.FromResult(new SurveyAggregateResult(truncated ? groups[..SurveyAggregateQuery.MaximumRows] : groups, truncated));
        }
    }

    private List<StoredResponse> Responses(TicketScope scope, AccessFilter filter, DateTimeOffset from, DateTimeOffset to, SurveyId? survey) =>
        (Find(scope)?.Responses.Values ?? Enumerable.Empty<StoredResponse>())
            .Where(stored => stored.Response.RespondedAt >= from && stored.Response.RespondedAt < to && (survey is null || stored.Response.Survey == survey))
            .Where(stored => AccessFilterEvaluator.Matches(filter, SurveyResources.Response(scope, stored.Response)))
            .ToList();

    private static bool Matches(SurveyInvitation invitation, SurveyResponse response) =>
        invitation.Survey == response.Survey && invitation.Kind == response.Kind && invitation.Conversation == response.Conversation && invitation.Contact == response.Contact
        && invitation.Inbox == response.Inbox && invitation.Team == response.Team && invitation.Agent == response.Agent;

    private static Task<StoreWriteResult<T, SurveyWriteRejection>> Write<T>(StoreWriteResult<T, SurveyWriteRejection> result) => Task.FromResult(result);

    private static void Require(bool initialized, string parameterName)
    {
        if (!initialized)
            throw new ArgumentException("Identifiers must be initialized.", parameterName);
    }

    private ScopeState? Find(TicketScope scope) => scopes.GetValueOrDefault(scope);

    private ScopeState State(TicketScope scope)
    {
        if (!scopes.TryGetValue(scope, out var state))
        {
            state = new ScopeState();
            scopes.Add(scope, state);
        }
        return state;
    }

    private void Prune(TicketScope scope, ScopeState state)
    {
        if (state.Surveys.Count == 0 && state.Invitations.Count == 0)
            scopes.Remove(scope);
    }
}
