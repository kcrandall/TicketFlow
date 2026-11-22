using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>Usage of one assistant call (tokens) and the model that answered.</summary>
public sealed record AssistUsageDto(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens, string Model, Guid UsageId, string Recording);

/// <summary>An assistant result: exactly one of the feature payloads is populated.</summary>
public sealed record AssistResultDto<T>(T Value, AssistUsageDto Usage);

public sealed record AssistSummaryDto(string Summary, IReadOnlyList<string> KeyPoints, string? NextStep);

public sealed record AssistGroundingDto(string PortalId, string Locale, string? Query = null);

public sealed record AssistDraftRequestDto(AssistGroundingDto? Grounding = null, string? Instructions = null);

public sealed record AssistCitationDto(string ArticleId, string PortalId, string Locale, string Title, string Slug, int RevisionNumber);

public sealed record AssistDraftDto(string Body, IReadOnlyList<AssistCitationDto> Citations, bool Grounded);

public sealed record AssistOptionDto(string Id, string? Description = null);

public sealed record AssistTriageRequestDto(IReadOnlyList<AssistOptionDto>? Labels = null, IReadOnlyList<AssistOptionDto>? Teams = null, bool SuggestPriority = true);

public sealed record AssistSuggestionDto(string Value, double Confidence);

public sealed record AssistTriageDto(IReadOnlyList<AssistSuggestionDto> Labels, AssistSuggestionDto? Team, AssistSuggestionDto? Priority, string? Rationale);

public sealed record AssistSentimentRequestDto(IReadOnlyList<string>? Intents = null);

public sealed record AssistSentimentDto(string Sentiment, double SentimentConfidence, string Intent, double IntentConfidence);

/// <summary>Translate either free <see cref="Text"/> or a timeline message (<see cref="MessageId"/>), exactly one.</summary>
public sealed record AssistTranslateRequestDto(string TargetLocale, string? Text = null, string? MessageId = null);

public sealed record AssistTranslationDto(string Text, string TargetLocale, string? SourceLocale);

/// <summary><see cref="Tone"/> is <c>friendly</c>, <c>formal</c>, <c>concise</c>, <c>empathetic</c> or <c>apologetic</c>.</summary>
public sealed record AssistRewriteRequestDto(string Text, string Tone);

public sealed record AssistRewriteDto(string Text, string Tone);

/// <summary>Assistant settings; <see cref="Settings"/> is the versioned assist settings codec JSON.</summary>
public sealed record AssistSettingsDto(JsonElement Settings, long Revision);

public sealed record AssistSettingsRequest(JsonElement Settings);

public sealed record AssistUsageTotalDto(string Feature, string Model, long Requests, long Failed, long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens);

public sealed record AssistUsageSummaryDto(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<AssistUsageTotalDto> Totals);
