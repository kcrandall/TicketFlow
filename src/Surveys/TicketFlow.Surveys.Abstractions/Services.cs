using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>
/// The scheduler's input: a conversation was resolved. TicketFlow's conversation outbox (E5) will produce these; hosts may also
/// construct them. Carries the conversation's inbox, team and assigned agent at resolution time and the contact's preferred
/// locale, if known. This is a trusted, internal input — never build it from end-user requests.
/// </summary>
public sealed record ConversationResolvedNotification
{
    public ConversationResolvedNotification(TicketScope scope, ConversationId conversation, ContactId contact, DateTimeOffset resolvedAt,
        InboxId? inbox = null, TeamId? team = null, UserId? agent = null, SurveyLocale? locale = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default || contact == default || inbox == default(InboxId) || team == default(TeamId) || agent == default(UserId) || locale is { IsInitialized: false })
            throw new ArgumentException("Notification identifiers must be initialized.");
        Scope = scope;
        Conversation = conversation;
        Contact = contact;
        ResolvedAt = SurveyLimits.Truncate(resolvedAt);
        Inbox = inbox;
        Team = team;
        Agent = agent;
        Locale = locale;
    }

    public TicketScope Scope { get; }
    public ConversationId Conversation { get; }
    public ContactId Contact { get; }
    public DateTimeOffset ResolvedAt { get; }
    public InboxId? Inbox { get; }
    public TeamId? Team { get; }
    public UserId? Agent { get; }
    public SurveyLocale? Locale { get; }
}

public enum SurveySkipReason
{
    /// <summary>An invitation for this survey and conversation already exists (duplicate notification).</summary>
    Duplicate = 1,

    /// <summary>The contact was invited to this survey within its cooldown.</summary>
    CoolingDown = 2,

    /// <summary>The store is at capacity.</summary>
    Rejected = 3,
}

public sealed record SurveySkip(SurveyId Survey, SurveySkipReason Reason);

/// <summary>Invitations created for one notification and surveys skipped.</summary>
public sealed record SurveySchedulingResult(IReadOnlyList<StoredInvitation> Created, IReadOnlyList<SurveySkip> Skipped);

/// <summary>
/// Turns conversation-resolved notifications into pending invitations: for each kind, the enabled survey bound to the
/// conversation's inbox, else the enabled workspace survey; creation respects delay, idempotency and per-contact cooldown.
/// </summary>
public interface ISurveyScheduler
{
    Task<SurveySchedulingResult> HandleResolvedAsync(ConversationResolvedNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a delivery adapter receives: the invitation, the raw token to embed in the response link and the localized question. The
/// token is secret: never log or persist it.
/// </summary>
public sealed record SurveyInvitationMessage(TicketScope Scope, SurveyInvitation Invitation, SurveyToken Token, SurveyKind Kind, int MinimumScore, int MaximumScore, SurveyQuestion Question)
{
    /// <summary>
    /// Stable idempotency key for the provider (<c>tfs-</c> + invitation GUID): identical on every attempt for this invitation, so a
    /// retry after an ambiguous failure is deduplicated by transports that honour idempotency keys. The token is also identical.
    /// </summary>
    public string DeliveryKey => $"tfs-{Invitation.Id.Value:N}";

    public override string ToString() => "SurveyInvitationMessage { <redacted> }";
}

/// <summary>
/// Delivers survey invitations (for example through EnvelopeFlow templates via the E6 adapter). TicketFlow ships no transport.
/// Implementations resolve the contact's address themselves, must not log the token or address, and throw when delivery fails so
/// the dispatcher can retry. Retries carry the same token and <see cref="SurveyInvitationMessage.DeliveryKey"/>; pass the key to the
/// provider as its idempotency key so an accepted-then-failed attempt is not delivered twice.
/// </summary>
public interface ISurveyInvitationSender
{
    Task SendAsync(SurveyInvitationMessage message, CancellationToken cancellationToken = default);
}

public sealed record SurveyDispatchSummary(int Sent, int Retrying, int Failed, int Cancelled);

/// <summary>Claims due invitations of one scope, issues their deterministic tokens and hands them to <see cref="ISurveyInvitationSender"/>.</summary>
public interface ISurveyDispatcher
{
    Task<SurveyDispatchSummary> DispatchDueAsync(TicketScope scope, int limit = 100, CancellationToken cancellationToken = default);
}

/// <summary>What a respondent sees for a valid, unexpired token.</summary>
public sealed record SurveyInvitationView(SurveyKind Kind, int MinimumScore, int MaximumScore, SurveyLocale Locale, string Question, string? CommentPrompt, DateTimeOffset ExpiresAt, bool Responded);

public sealed record SurveyResponseAccepted(SurveyResponse Response);

/// <summary>The same score and comment were already recorded for this token (idempotent replay).</summary>
public sealed record SurveyResponseDuplicate(SurveyResponse Response);

public enum SurveyResponseRejection
{
    /// <summary>The token is malformed, unknown in the scope, not yet issued, or its invitation was cancelled.</summary>
    InvalidToken = 1,

    Expired = 2,

    /// <summary>The score is outside the survey's scale.</summary>
    InvalidScore = 3,

    /// <summary>The comment is blank, too long or contains control characters.</summary>
    InvalidComment = 4,

    /// <summary>A different response was already recorded; responses are single-use.</summary>
    AlreadyResponded = 5,
}

public sealed record SurveyResponseRejected(SurveyResponseRejection Reason);

/// <summary>Outcome of an anonymous response. Switch over every case without a discard arm.</summary>
public union SurveyResponseResult(SurveyResponseAccepted, SurveyResponseDuplicate, SurveyResponseRejected);

/// <summary>
/// The anonymous respondent path within a host-resolved <see cref="TicketScope"/>. The token is the only credential; unknown,
/// malformed and unsent tokens are indistinguishable.
/// </summary>
public interface ISurveyResponder
{
    Task<SurveyInvitationView?> GetInvitationAsync(TicketScope scope, string token, CancellationToken cancellationToken = default);

    Task<SurveyResponseResult> RespondAsync(TicketScope scope, string token, int score, string? comment = null, CancellationToken cancellationToken = default);
}

/// <summary>A response as an agent sees it; <see cref="Comment"/> is null when masked.</summary>
public sealed record SurveyResponseView(SurveyResponse Response, string? Comment, bool CommentMasked, bool CommentRevealable);

public sealed record SurveyResponseViewPage(IReadOnlyList<SurveyResponseView> Items, SurveyResponseCursor? Next);

/// <summary>
/// Authorized survey administration and reporting in the actor's workspace. Definitions need <c>survey:read</c> /
/// <c>survey:manage</c> on the survey's inbox (or the workspace for workspace surveys); invitations and responses need
/// <c>survey:read</c> on their inbox/team/agent (lists use the access query planner, comments honour the <c>survey.comment</c>
/// field policy); aggregates need <c>report:view</c> and include only responses the actor may view. As on every list path, an actor
/// without any applicable grant gets an empty page or aggregate rather than a refusal.
/// </summary>
public interface ISurveyService
{
    Task<TicketResult<StoredSurvey>> GetSurveyAsync(PrincipalRef actor, SurveyId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<StoredSurvey>>> ListSurveysAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredSurvey>> CreateSurveyAsync(PrincipalRef actor, SurveyDefinition survey, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredSurvey>> UpdateSurveyAsync(PrincipalRef actor, SurveyDefinition survey, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredSurvey>> DeleteSurveyAsync(PrincipalRef actor, SurveyId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredInvitation>> GetInvitationAsync(PrincipalRef actor, SurveyInvitationId id, CancellationToken cancellationToken = default);

    Task<TicketResult<SurveyResponseView>> GetResponseAsync(PrincipalRef actor, SurveyInvitationId invitation, CancellationToken cancellationToken = default);

    Task<TicketResult<SurveyResponseViewPage>> ListResponsesAsync(PrincipalRef actor, SurveyResponseQuery query, CancellationToken cancellationToken = default);

    Task<TicketResult<SurveyAggregateResult>> AggregateAsync(PrincipalRef actor, SurveyAggregateQuery query, CancellationToken cancellationToken = default);
}
