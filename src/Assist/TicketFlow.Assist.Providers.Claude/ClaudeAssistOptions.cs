namespace TicketFlow.Assist.Providers.Claude;

/// <summary>
/// Claude provider settings. The API key comes from <see cref="ApiKey"/> or, when unset, from configuration
/// (<c>TicketFlow:Assist:Claude:ApiKey</c>, then <c>ANTHROPIC_API_KEY</c>). Never hard-code keys.
/// </summary>
public sealed class ClaudeAssistOptions
{
    /// <summary>Provider name referenced by <see cref="AssistModelDefinition.Provider"/>.</summary>
    public const string ProviderName = "claude";

    public const string HaikuModel = "claude-haiku-5-5";
    public const string SonnetModel = "claude-sonnet-5-5";
    public const string OpusModel = "claude-opus-5-5";

    /// <summary>The API key. Prefer configuration or a secret store; never log it.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional API base URL (for a gateway); defaults to the SDK's.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Default model for classification features (triage, sentiment and intent). Default <c>claude-haiku-5-5</c>.</summary>
    public string ClassificationModel { get; set; } = HaikuModel;

    /// <summary>Default model for generation features (summary, draft, translation, tone). Default <c>claude-sonnet-5-5</c>.</summary>
    public string GenerationModel { get; set; } = SonnetModel;

    /// <summary>Retries after the first attempt for 408/429/5xx/529, timeouts and network failures (0-6, default 3).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>First backoff delay; doubles per retry with jitter (default 1 s).</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound for one backoff delay (default 30 s).</summary>
    public TimeSpan MaximumBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A <c>retry-after</c> longer than this is not waited for; the failure is returned instead (default 60 s).</summary>
    public TimeSpan MaximumRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Timeout of one HTTP attempt (default 90 s).</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Timeout of the whole call including retries (default 4 minutes).</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Mark the system prompt with <c>cache_control: ephemeral</c> (default true).</summary>
    public bool CacheSystemPrompt { get; set; } = true;

    internal void Validate()
    {
        if (MaxRetries is < 0 or > 6)
            throw new InvalidOperationException("MaxRetries must be between 0 and 6.");
        if (InitialBackoff <= TimeSpan.Zero || MaximumBackoff < InitialBackoff || MaximumBackoff > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("Backoff delays must be positive, ordered and at most five minutes.");
        if (MaximumRetryAfter < TimeSpan.Zero || MaximumRetryAfter > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("MaximumRetryAfter must be between zero and ten minutes.");
        if (AttemptTimeout <= TimeSpan.Zero || TotalTimeout < AttemptTimeout || TotalTimeout > TimeSpan.FromMinutes(30))
            throw new InvalidOperationException("Timeouts must be positive, the total at least one attempt and at most 30 minutes.");
        if (BaseUrl is { } baseUrl
            && (!baseUrl.IsAbsoluteUri
                || !string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || baseUrl.UserInfo.Length != 0
                || baseUrl.Query.Length != 0
                || baseUrl.Fragment.Length != 0))
            throw new InvalidOperationException("BaseUrl must be an absolute https URI without credentials, query or fragment.");
    }
}
