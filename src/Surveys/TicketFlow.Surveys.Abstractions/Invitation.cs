using TicketFlow.Core;

namespace TicketFlow.Surveys;

public enum SurveyInvitationState
{
    /// <summary>Created and waiting for <see cref="SurveyInvitation.SendAfter"/>; no token exists yet.</summary>
    Pending = 1,

    /// <summary>A token was issued and handed to the sender; responses are accepted until <see cref="SurveyInvitation.ExpiresAt"/>.</summary>
    Sent = 2,

    /// <summary>The single response was recorded.</summary>
    Responded = 3,

    /// <summary>Delivery failed permanently after the maximum number of attempts.</summary>
    Failed = 4,

    /// <summary>The survey was disabled before delivery.</summary>
    Cancelled = 5,
}

/// <summary>
/// An invitation for one contact to answer one survey about one conversation. It carries the conversation's inbox, team and agent
/// at resolution time (the response's access and reporting dimensions). <see cref="TokenHash"/> is set only while
/// <see cref="State"/> is <see cref="SurveyInvitationState.Sent"/> or <see cref="SurveyInvitationState.Responded"/>.
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
        DateTimeOffset? sentAt = null,
        DateTimeOffset? expiresAt = null)
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
        var issued = state is SurveyInvitationState.Sent or SurveyInvitationState.Responded;
        if (issued != (tokenHash is not null) || issued != (sentAt is not null) || issued != (expiresAt is not null))
            throw new ArgumentException("Token hash, sent and expiry times are present exactly when the invitation was sent.");
        if (sendAfter < createdAt || (expiresAt is { } expiry && sentAt is { } sent && expiry <= sent))
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
        SentAt = sentAt is { } delivered ? SurveyLimits.Truncate(delivered) : null;
        ExpiresAt = expiresAt is { } expires ? SurveyLimits.Truncate(expires) : null;
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
    public int Attempts { get; }
    public SurveyTokenHash? TokenHash { get; }
    public DateTimeOffset? SentAt { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public SurveyInvitation MarkSent(SurveyTokenHash hash, DateTimeOffset sentAt, TimeSpan lifetime) =>
        new(Id, Survey, Kind, Conversation, Contact, Inbox, Team, Agent, Locale, CreatedAt, SendAfter, SurveyInvitationState.Sent, Attempts + 1, hash, sentAt, sentAt + lifetime);

    public SurveyInvitation Retry(DateTimeOffset sendAfter, int attempts) =>
        new(Id, Survey, Kind, Conversation, Contact, Inbox, Team, Agent, Locale, CreatedAt, sendAfter < CreatedAt ? CreatedAt : sendAfter, SurveyInvitationState.Pending, attempts);

    public SurveyInvitation Close(SurveyInvitationState state, int attempts) =>
        state is SurveyInvitationState.Failed or SurveyInvitationState.Cancelled
            ? new(Id, Survey, Kind, Conversation, Contact, Inbox, Team, Agent, Locale, CreatedAt, SendAfter, state, attempts)
            : throw new ArgumentOutOfRangeException(nameof(state));

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
