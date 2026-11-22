using TicketFlow.Core;

namespace TicketFlow.Surveys;

public enum SurveyInvitationState
{
    /// <summary>Waiting for <see cref="SurveyInvitation.SendAfter"/> (first attempt, or a retry after a failed send).</summary>
    Pending = 1,

    /// <summary>The sender accepted the invitation.</summary>
    Sent = 2,

    /// <summary>The single response was recorded.</summary>
    Responded = 3,

    /// <summary>Delivery failed permanently after the maximum number of attempts.</summary>
    Failed = 4,

    /// <summary>The survey was disabled or deleted before delivery.</summary>
    Cancelled = 5,

    /// <summary>
    /// Claimed by one dispatcher until <see cref="SurveyInvitation.LeaseUntil"/>. A dispatcher that stops mid-send leaves the lease to
    /// expire, after which the invitation is due again.
    /// </summary>
    Sending = 6,
}

/// <summary>
/// An invitation for one contact to answer one survey about one conversation. It carries the conversation's inbox, team and agent
/// at resolution time (the response's access and reporting dimensions).
/// <para>
/// <see cref="TokenHash"/>, <see cref="IssuedAt"/> and <see cref="ExpiresAt"/> are set together when the token is first issued and then
/// kept across retries, so a link that already reached the contact stays valid; <see cref="SurveyInvitationState.Sent"/> and
/// <see cref="SurveyInvitationState.Responded"/> always have them. <see cref="LeaseUntil"/> is set exactly while
/// <see cref="SurveyInvitationState.Sending"/>. A response is accepted while a token exists, the state is neither Responded nor
/// Cancelled, and <see cref="ExpiresAt"/> has not passed (<see cref="AcceptsResponses"/>).
/// </para>
/// </summary>
public sealed record SurveyInvitation
{
    public const int MaximumAttempts = 20;

    public SurveyInvitation(
        SurveyInvitationId id,
        SurveyId survey,
        SurveyKind kind,
        ConversationId conversation,
        ContactId contact,
        InboxId? inbox,
        TeamId? team,
        UserId? agent,
        SurveyLocale locale,
        DateTimeOffset createdAt,
        DateTimeOffset sendAfter,
        SurveyInvitationState state = SurveyInvitationState.Pending,
        int attempts = 0,
        SurveyTokenHash? tokenHash = null,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? leaseUntil = null)
    {
        if (!id.IsInitialized || !survey.IsInitialized || conversation == default || contact == default || !locale.IsInitialized
            || inbox == default(InboxId) || team == default(TeamId) || agent == default(UserId) || tokenHash is { IsInitialized: false })
            throw new ArgumentException("Invitation identifiers must be initialized.");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        ArgumentOutOfRangeException.ThrowIfNegative(attempts);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attempts, MaximumAttempts);
        var issued = tokenHash is not null;
        if (issued != (issuedAt is not null) || issued != (expiresAt is not null))
            throw new ArgumentException("Token hash, issue and expiry times are set together.");
        if (!issued && state is SurveyInvitationState.Sent or SurveyInvitationState.Responded)
            throw new ArgumentException("Sent and responded invitations have a token.");
        if ((state == SurveyInvitationState.Sending) != (leaseUntil is not null))
            throw new ArgumentException("A lease is present exactly while the invitation is being sent.");
        if (sendAfter < createdAt || (expiresAt is { } expiry && issuedAt is { } at && expiry <= at))
            throw new ArgumentException("Invitation times are inconsistent.");
        Id = id;
        Survey = survey;
        Kind = kind;
        Conversation = conversation;
        Contact = contact;
        Inbox = inbox;
        Team = team;
        Agent = agent;
        Locale = locale;
        CreatedAt = SurveyLimits.Truncate(createdAt);
        SendAfter = SurveyLimits.Truncate(sendAfter);
        State = state;
        Attempts = attempts;
        TokenHash = tokenHash;
        IssuedAt = issuedAt is { } first ? SurveyLimits.Truncate(first) : null;
        ExpiresAt = expiresAt is { } expires ? SurveyLimits.Truncate(expires) : null;
        LeaseUntil = leaseUntil is { } lease ? SurveyLimits.Truncate(lease) : null;
    }

    public SurveyInvitationId Id { get; }
    public SurveyId Survey { get; }
    public SurveyKind Kind { get; }
    public ConversationId Conversation { get; }
    public ContactId Contact { get; }
    public InboxId? Inbox { get; }
    public TeamId? Team { get; }
    public UserId? Agent { get; }
    public SurveyLocale Locale { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset SendAfter { get; }
    public SurveyInvitationState State { get; }

    /// <summary>Dispatch claims so far (each claim counts, including ones whose dispatcher stopped).</summary>
    public int Attempts { get; }

    public SurveyTokenHash? TokenHash { get; }

    /// <summary>When the token was first issued.</summary>
    public DateTimeOffset? IssuedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public DateTimeOffset? LeaseUntil { get; }

    /// <summary>True when a response presented at <paramref name="now"/> can be recorded.</summary>
    public bool AcceptsResponses(DateTimeOffset now) =>
        TokenHash is not null && State is not (SurveyInvitationState.Responded or SurveyInvitationState.Cancelled) && now < ExpiresAt;

    /// <summary>Claims the invitation for one dispatcher until <paramref name="leaseUntil"/> (one more attempt).</summary>
    public SurveyInvitation Claim(DateTimeOffset leaseUntil) =>
        With(SurveyInvitationState.Sending, Math.Min(Attempts + 1, MaximumAttempts), leaseUntil: leaseUntil);

    /// <summary>Records the token hash and expiry (replacing any previous token).</summary>
    public SurveyInvitation Issue(SurveyTokenHash hash, DateTimeOffset issuedAt, TimeSpan lifetime) =>
        new(Id, Survey, Kind, Conversation, Contact, Inbox, Team, Agent, Locale, CreatedAt, SendAfter, State, Attempts, hash, issuedAt, issuedAt + lifetime, LeaseUntil);

    public SurveyInvitation MarkSent() => With(SurveyInvitationState.Sent, Attempts);

    /// <summary>Back to Pending (keeping the token) until <paramref name="sendAfter"/>.</summary>
    public SurveyInvitation Retry(DateTimeOffset sendAfter) =>
        new(Id, Survey, Kind, Conversation, Contact, Inbox, Team, Agent, Locale, CreatedAt, sendAfter < CreatedAt ? CreatedAt : sendAfter, SurveyInvitationState.Pending, Attempts,
            TokenHash, IssuedAt, ExpiresAt);

    public SurveyInvitation Close(SurveyInvitationState state) =>
        state is SurveyInvitationState.Failed or SurveyInvitationState.Cancelled ? With(state, Attempts) : throw new ArgumentOutOfRangeException(nameof(state));

    private SurveyInvitation With(SurveyInvitationState state, int attempts, DateTimeOffset? leaseUntil = null) =>
        new(Id, Survey, Kind, Conversation, Contact, Inbox, Team, Agent, Locale, CreatedAt, SendAfter, state, attempts, TokenHash, IssuedAt, ExpiresAt, leaseUntil);

    public override string ToString() => "SurveyInvitation { <redacted> }";
}

/// <summary>An invitation with its scope and revision (first revision is 1).</summary>
public sealed record StoredInvitation(TicketScope Scope, SurveyInvitation Invitation, long Revision);

/// <summary>
/// A recorded answer: the score on the survey's scale and an optional bounded comment (customer content; never logged), with the
/// invitation's conversation, contact, inbox, team and agent copied for reporting and access filtering.
/// </summary>
public sealed record SurveyResponse
{
    public const int MaximumCommentLength = 2000;

    public SurveyResponse(SurveyInvitationId invitation, SurveyId survey, SurveyKind kind, int score, string? comment, DateTimeOffset respondedAt,
        ConversationId conversation, ContactId contact, InboxId? inbox, TeamId? team, UserId? agent)
    {
        if (!invitation.IsInitialized || !survey.IsInitialized || conversation == default || contact == default
            || inbox == default(InboxId) || team == default(TeamId) || agent == default(UserId))
            throw new ArgumentException("Response identifiers must be initialized.");
        if (!SurveyScales.IsValid(kind, score))
            throw new ArgumentOutOfRangeException(nameof(score));
        if (comment is not null && !SurveyText.IsMultiline(comment, MaximumCommentLength))
            throw new ArgumentException($"Comments must be non-blank, at most {MaximumCommentLength} characters, without control characters other than line breaks and tabs.", nameof(comment));
        Invitation = invitation;
        Survey = survey;
        Kind = kind;
        Score = score;
        Comment = comment;
        RespondedAt = SurveyLimits.Truncate(respondedAt);
        Conversation = conversation;
        Contact = contact;
        Inbox = inbox;
        Team = team;
        Agent = agent;
    }

    /// <summary>A response to <paramref name="invitation"/>, copying its dimensions.</summary>
    public static SurveyResponse For(SurveyInvitation invitation, int score, string? comment, DateTimeOffset respondedAt)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new(invitation.Id, invitation.Survey, invitation.Kind, score, comment, respondedAt, invitation.Conversation, invitation.Contact,
            invitation.Inbox, invitation.Team, invitation.Agent);
    }

    public SurveyInvitationId Invitation { get; }
    public SurveyId Survey { get; }
    public SurveyKind Kind { get; }
    public int Score { get; }
    public string? Comment { get; }
    public DateTimeOffset RespondedAt { get; }
    public ConversationId Conversation { get; }
    public ContactId Contact { get; }
    public InboxId? Inbox { get; }
    public TeamId? Team { get; }
    public UserId? Agent { get; }

    public override string ToString() => "SurveyResponse { <redacted> }";
}

/// <summary>A response with its scope.</summary>
public sealed record StoredResponse(TicketScope Scope, SurveyResponse Response);
