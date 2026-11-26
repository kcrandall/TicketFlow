namespace TicketFlow.Integrations.InsightFlow;

/// <summary>
/// Configuration for exporting TicketFlow reporting facts to InsightFlow over HTTP. Ingestion scope (<see cref="TenantId"/>,
/// <see cref="ProjectId"/>, <see cref="Environment"/>, <see cref="AppName"/>) comes exclusively from host configuration, never
/// from exported facts.
/// </summary>
public sealed class InsightFlowExportOptions
{
    public const int DefaultBatchSize = 100;
    public const int MaximumBatchSize = 500;
    public const int DefaultMaximumPayloadBytes = 512 * 1024;

    /// <summary>Absolute HTTPS endpoint accepting InsightFlow <c>ClientTrackBatchRequest</c> JSON payloads.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Optional API key or bearer token sent on export requests; never logged or exposed in <see cref="ToString"/>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Header carrying <see cref="ApiKey"/> (default <c>Authorization</c>).</summary>
    public string AuthorizationHeaderName { get; set; } = "Authorization";

    /// <summary>Optional prefix before <see cref="ApiKey"/> when using <c>Authorization</c> (default <c>Bearer</c>; empty/null sends raw value).</summary>
    public string? AuthorizationScheme { get; set; } = "Bearer";

    public string TenantId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;

    public string AppSource { get; set; } = "ticketflow";
    public string? AppVersion { get; set; }

    /// <summary>Prefix prepended to snake_case metric names (default <c>ticketflow.reporting.</c>).</summary>
    public string EventPrefix { get; set; } = "ticketflow.reporting.";

    /// <summary>Facts read and sent per batch (1-500, default 100).</summary>
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>Maximum UTF-8 payload bytes before automatically splitting a batch (1 KiB to 4 MiB, default 512 KiB).</summary>
    public int MaximumPayloadBytes { get; set; } = DefaultMaximumPayloadBytes;

    /// <summary>How far back the first export run looks when no checkpoint exists yet (default 24 hours, max 365 days).</summary>
    public TimeSpan InitialLookback { get; set; } = TimeSpan.FromHours(24);

    /// <summary>HTTP timeout (1 second to 2 minutes, default 15 seconds).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri || !string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || Endpoint.UserInfo.Length != 0 || Endpoint.Query.Length != 0 || Endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("InsightFlow Endpoint must be an absolute https URI without credentials, query or fragment.");
        if (ApiKey is not null && (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length > 512 || ApiKey.Any(c => c is < '!' or > '~')))
            throw new InvalidOperationException("InsightFlow ApiKey must be printable ASCII without whitespace.");
        if (string.IsNullOrWhiteSpace(AuthorizationHeaderName) || AuthorizationHeaderName.Length > 64 || !IsHeaderToken(AuthorizationHeaderName))
            throw new InvalidOperationException("AuthorizationHeaderName must be a valid HTTP header token.");
        if (!string.IsNullOrEmpty(AuthorizationScheme) && (AuthorizationScheme.Length > 64 || !IsHeaderToken(AuthorizationScheme)))
            throw new InvalidOperationException("AuthorizationScheme must be a valid HTTP token.");
        if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(Environment) || string.IsNullOrWhiteSpace(AppName))
            throw new InvalidOperationException("InsightFlow host scope (TenantId, ProjectId, Environment, AppName) is required.");
        if (TenantId.Length > 128 || ProjectId.Length > 128 || Environment.Length > 128 || AppName.Length > 128)
            throw new InvalidOperationException("InsightFlow host scope identifiers must be at most 128 characters.");
        if (string.IsNullOrWhiteSpace(AppSource) || AppSource.Length > 64)
            throw new InvalidOperationException("AppSource is required.");
        if (string.IsNullOrWhiteSpace(EventPrefix) || EventPrefix.Length > 64)
            throw new InvalidOperationException("EventPrefix is required.");
        if (BatchSize is < 1 or > MaximumBatchSize)
            throw new InvalidOperationException($"BatchSize must be between 1 and {MaximumBatchSize}.");
        if (MaximumPayloadBytes is < 1024 or > 4 * 1024 * 1024)
            throw new InvalidOperationException("MaximumPayloadBytes must be between 1 KiB and 4 MiB.");
        if (InitialLookback <= TimeSpan.Zero || InitialLookback > TimeSpan.FromDays(365))
            throw new InvalidOperationException("InitialLookback must be positive and at most 365 days.");
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Timeout must be between 1 second and 2 minutes.");
    }

    public override string ToString() => $"InsightFlowExportOptions {{ Endpoint = {Endpoint}, TenantId = {TenantId}, ProjectId = {ProjectId}, Environment = {Environment}, AppName = {AppName} }}";

    private static bool IsHeaderToken(string value) =>
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
