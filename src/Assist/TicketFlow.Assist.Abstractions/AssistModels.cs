using Microsoft.Extensions.AI;

namespace TicketFlow.Assist;

/// <summary>The assistant capabilities. Persisted numerically in usage records; never renumber.</summary>
public enum AssistFeature
{
    ConversationSummary = 1,
    ReplyDraft = 2,
    Triage = 3,
    SentimentAndIntent = 4,
    Translate = 5,
    ToneRewrite = 6,
}

[Flags]
public enum AssistModelCapabilities
{
    None = 0,
    Streaming = 1 << 0,
    Tools = 1 << 1,
    StructuredOutput = 1 << 2,
    Reasoning = 1 << 3,
    PromptCaching = 1 << 4,
}

/// <summary>Provider-neutral reasoning depth. Providers map it to their own controls (for Claude, <c>output_config.effort</c>).</summary>
public enum AssistThinkingLevel
{
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// A registered model: the TicketFlow model identifier (what settings and requests name), the provider that serves it (matching an
/// <see cref="IAssistChatProvider.Name"/>), the provider's own model name, capabilities and a default thinking level.
/// </summary>
public sealed record AssistModelDefinition
{
    public const int MaximumIdLength = 128;

    public AssistModelDefinition(string id, string provider, string providerModel, AssistModelCapabilities capabilities, AssistThinkingLevel? defaultThinkingLevel = null)
    {
        Id = AssistText.RequireToken(id, MaximumIdLength, nameof(id), "Model identifiers");
        Provider = AssistText.RequireToken(provider, MaximumIdLength, nameof(provider), "Provider names");
        ProviderModel = AssistText.RequireToken(providerModel, MaximumIdLength, nameof(providerModel), "Provider model names");
        if (defaultThinkingLevel is { } level && !Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(defaultThinkingLevel));
        Capabilities = capabilities;
        DefaultThinkingLevel = defaultThinkingLevel;
    }

    public string Id { get; }
    public string Provider { get; }
    public string ProviderModel { get; }
    public AssistModelCapabilities Capabilities { get; }
    public AssistThinkingLevel? DefaultThinkingLevel { get; }
}

/// <summary>The registered models and the default model per feature (configured through <c>AssistOptions</c> and provider packages).</summary>
public interface IAssistModelRegistry
{
    IReadOnlyCollection<AssistModelDefinition> All { get; }

    bool TryGet(string id, out AssistModelDefinition definition);

    /// <summary>The default model identifier for <paramref name="feature"/>.</summary>
    string DefaultFor(AssistFeature feature);
}

/// <summary>What a chat client is for: the model, the feature (for telemetry and accounting) and an optional thinking override.</summary>
public sealed record AssistChatRequest(string ModelId, AssistFeature Feature, AssistThinkingLevel? ThinkingLevel = null);

/// <summary>
/// Builds <see cref="IChatClient"/>s for registered models: the provider's base client wrapped in the TicketFlow pipeline (provider
/// option mapping, usage metrics, tool-invocation telemetry and OpenTelemetry with sensitive data disabled). Throws
/// <see cref="ArgumentException"/> for an unknown model and <see cref="InvalidOperationException"/> when no provider serves it.
/// </summary>
public interface IAssistChatClientFactory
{
    IChatClient GetChatClient(AssistChatRequest request);
}

/// <summary>
/// A model provider (for example Claude). Base clients are cached per provider model by the factory. Provider clients must not log
/// prompts, outputs or credentials, and must surface provider failures as <see cref="AssistProviderException"/>.
/// </summary>
public interface IAssistChatProvider
{
    /// <summary>The provider name models reference in <see cref="AssistModelDefinition.Provider"/>.</summary>
    string Name { get; }

    IChatClient CreateClient(AssistModelDefinition model);

    /// <summary>
    /// Maps provider-neutral options onto provider specifics (thinking level, prompt caching of <see cref="ChatOptions.Instructions"/>,
    /// structured output). Returns the options to send; must not mutate <paramref name="options"/>.
    /// </summary>
    ChatOptions ApplyOptions(AssistModelDefinition model, AssistChatRequest request, ChatOptions options);
}

public enum AssistProviderFailure
{
    /// <summary>The provider rejected the request shape (HTTP 400/422).</summary>
    InvalidRequest = 1,

    /// <summary>The credential is missing or invalid (HTTP 401).</summary>
    Authentication = 2,

    /// <summary>The credential may not use the model or feature (HTTP 403).</summary>
    PermissionDenied = 3,

    /// <summary>The model or endpoint does not exist (HTTP 404).</summary>
    NotFound = 4,

    /// <summary>The request exceeds the provider's size limit (HTTP 413).</summary>
    RequestTooLarge = 5,

    /// <summary>Provider rate limit (HTTP 429) after retries.</summary>
    RateLimited = 6,

    /// <summary>Provider overloaded (HTTP 529) after retries.</summary>
    Overloaded = 7,

    /// <summary>Provider server error (HTTP 5xx) after retries.</summary>
    ServerError = 8,

    /// <summary>The request timed out after retries.</summary>
    Timeout = 9,

    /// <summary>Network failure before a response, after retries.</summary>
    Network = 10,

    /// <summary>The output hit the token limit before completing.</summary>
    OutputTruncated = 11,

    /// <summary>The provider response, or the model output, could not be parsed or failed validation.</summary>
    InvalidResponse = 12,

    /// <summary>The provider is not configured (for example no API key).</summary>
    NotConfigured = 13,
}

/// <summary>
/// A provider failure translated to provider-neutral terms. The message is non-sensitive (no prompt, output, credential or provider
/// error body).
/// </summary>
public sealed class AssistProviderException : Exception
{
    public AssistProviderException(AssistProviderFailure failure, bool retryable, int? statusCode = null, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base($"The assistant provider failed: {failure}.", innerException)
    {
        Failure = failure;
        Retryable = retryable;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public AssistProviderFailure Failure { get; }
    public bool Retryable { get; }
    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Token usage of one call. Totals are the sum of every input and output category.</summary>
public sealed record AssistUsage
{
    public AssistUsage(long inputTokens, long outputTokens, long cacheReadInputTokens = 0, long cacheCreationInputTokens = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheReadInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(cacheCreationInputTokens);
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadInputTokens = cacheReadInputTokens;
        CacheCreationInputTokens = cacheCreationInputTokens;
    }

    public static AssistUsage Zero { get; } = new(0, 0);

    /// <summary>Uncached input tokens.</summary>
    public long InputTokens { get; }
    public long OutputTokens { get; }
    public long CacheReadInputTokens { get; }
    public long CacheCreationInputTokens { get; }
    public long TotalTokens => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;

    /// <summary>
    /// Maps Microsoft.Extensions.AI usage: <see cref="UsageDetails.InputTokenCount"/> includes cached reads
    /// (<see cref="UsageDetails.CachedInputTokenCount"/>) and cache writes (the <c>CacheCreationInputTokens</c> additional count).
    /// </summary>
    public static AssistUsage From(UsageDetails? usage)
    {
        if (usage is null)
            return Zero;
        var cached = Math.Max(0, usage.CachedInputTokenCount ?? 0);
        long creation = 0;
        if (usage.AdditionalCounts is { } counts && counts.TryGetValue("CacheCreationInputTokens", out var value))
            creation = Math.Max(0, value);
        var input = Math.Max(0, (usage.InputTokenCount ?? 0) - cached - creation);
        return new(input, Math.Max(0, usage.OutputTokenCount ?? 0), cached, creation);
    }
}
