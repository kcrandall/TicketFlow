namespace TicketFlow.Hosting.AspNetCore.Contracts;

public sealed record SurveyQuestionDto(string Locale, string Text, string? CommentPrompt = null);

/// <summary>When invitations are scheduled: durations in whole seconds; <see cref="Trigger"/> is <c>conversationResolved</c>.</summary>
public sealed record SurveyPolicyDto(long DelaySeconds, long ContactCooldownSeconds, long InvitationLifetimeSeconds, string Trigger = "conversationResolved");

/// <summary>A survey definition; <see cref="Kind"/> is <c>csat</c> (1-5) or <c>nps</c> (0-10).</summary>
public sealed record SurveyDto(string Id, string Name, string Kind, string DefaultLocale, IReadOnlyList<SurveyQuestionDto> Questions, SurveyPolicyDto Policy, string? InboxId, bool Enabled,
    long Revision);

public sealed record SurveyRequest(string Name, string Kind, string DefaultLocale, IReadOnlyList<SurveyQuestionDto> Questions, SurveyPolicyDto Policy, string? InboxId = null, bool Enabled = true,
    string? Id = null);

/// <summary>A survey response; masked comments are removed (<see cref="CommentMasked"/>).</summary>
public sealed record SurveyResponseDto(
    Guid InvitationId,
    string SurveyId,
    string Kind,
    int Score,
    string? Comment,
    bool CommentMasked,
    bool CommentRevealable,
    DateTimeOffset RespondedAt,
    string ConversationId,
    string ContactId,
    string? InboxId,
    string? TeamId,
    string? AgentId);

public sealed record SurveyAggregateRowDto(string Kind, DateTimeOffset BucketStart, string? Group, long Responses, long Positive, long Neutral, long Negative, long ScoreSum);

public sealed record SurveyAggregateDto(IReadOnlyList<SurveyAggregateRowDto> Rows, bool Truncated);

/// <summary>An anonymous invitation lookup; the token comes from the invitation link (keep it in the URL fragment, never the path or query).</summary>
public sealed record SurveyTokenRequest(string Token);

public sealed record SurveyInvitationDto(string Kind, int MinimumScore, int MaximumScore, string Locale, string Question, string? CommentPrompt, DateTimeOffset ExpiresAt, bool Responded);

public sealed record SurveyRespondRequest(string Token, int Score, string? Comment = null);

/// <summary><see cref="Outcome"/> is <c>accepted</c> or <c>duplicate</c> (an earlier identical submission).</summary>
public sealed record SurveyRespondResultDto(string Outcome, int Score, DateTimeOffset RespondedAt);
