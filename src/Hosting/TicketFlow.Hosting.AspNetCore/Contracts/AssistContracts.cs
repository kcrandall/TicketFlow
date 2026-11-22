using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>Usage of one assistant call (tokens) and the model that answered.</summary>
public sealed record AssistUsageResponse(long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens, string Model, Guid UsageId, string Recording);

/// <summary>An assistant result: exactly one of the feature payloads is populated.</summary>
public sealed record AssistResponse<T>(T Value, AssistUsageResponse Usage);

public sealed record AssistSummaryResponse(string Summary, IReadOnlyList<string> KeyPoints, string? NextStep);

public sealed record AssistGroundingInput(string PortalId, string Locale, string? Query = null);

public sealed record AssistDraftReplyRequest(AssistGroundingInput? Grounding = null, string? Instructions = null);

public sealed record AssistCitationResponse(string ArticleId, string PortalId, string Locale, string Title, string Slug, int RevisionNumber);

public sealed record AssistDraftResponse(string Body, IReadOnlyList<AssistCitationResponse> Citations, bool Grounded);

public sealed record AssistOptionInput(string Id, string? Description = null);

public sealed record AssistTriageSuggestionRequest(IReadOnlyList<AssistOptionInput>? Labels = null, IReadOnlyList<AssistOptionInput>? Teams = null, bool SuggestPriority = true);

public sealed record AssistSuggestionResponse(string Value, double Confidence);

public sealed record AssistTriageResponse(IReadOnlyList<AssistSuggestionResponse> Labels, AssistSuggestionResponse? Team, AssistSuggestionResponse? Priority, string? Rationale);

public sealed record AssistSentimentAnalysisRequest(IReadOnlyList<string>? Intents = null);

public sealed record AssistSentimentResponse(string Sentiment, double SentimentConfidence, string Intent, double IntentConfidence);

/// <summary>Translate either free <see cref="Text"/> or a timeline message (<see cref="MessageId"/>), exactly one.</summary>
public sealed record AssistTranslationRequest(string TargetLocale, string? Text = null, string? MessageId = null);

public sealed record AssistTranslationResponse(string Text, string TargetLocale, string? SourceLocale);

/// <summary><see cref="Tone"/> is <c>friendly</c>, <c>formal</c>, <c>concise</c>, <c>empathetic</c> or <c>apologetic</c>.</summary>
public sealed record AssistRewriteRequest(string Text, string Tone);

public sealed record AssistRewriteResponse(string Text, string Tone);

/// <summary>Assistant settings; <see cref="Settings"/> is the versioned assist settings codec JSON.</summary>
public sealed record AssistSettingsResponse(JsonElement Settings, long Revision);

public sealed record AssistSettingsRequest(JsonElement Settings);

public sealed record AssistUsageTotalResponse(string Feature, string Model, long Requests, long Failed, long InputTokens, long OutputTokens, long CacheReadInputTokens, long CacheCreationInputTokens);

public sealed record AssistUsageSummaryResponse(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<AssistUsageTotalResponse> Totals);
