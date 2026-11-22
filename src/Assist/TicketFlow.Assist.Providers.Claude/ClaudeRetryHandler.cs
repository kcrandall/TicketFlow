using System.Globalization;
using System.Net;

namespace TicketFlow.Assist.Providers.Claude;

/// <summary>
/// HTTP retry policy for the Claude API: retries 408, 409, 429, 5xx and 529 responses, attempt timeouts and network failures with
/// exponential backoff and jitter, honoring <c>retry-after-ms</c> / <c>retry-after</c> (seconds or HTTP date) and
/// <c>x-should-retry</c>. Delays use the injected <see cref="TimeProvider"/>. Request and response bodies are never logged or inspected
/// beyond buffering the request for replay.
/// </summary>
internal sealed class ClaudeRetryHandler : DelegatingHandler
{
    private readonly ClaudeAssistOptions options;
    private readonly TimeProvider time;
    private readonly Func<double> jitter;

    public ClaudeRetryHandler(ClaudeAssistOptions options, TimeProvider time, HttpMessageHandler inner, Func<double>? jitter = null)
        : base(inner)
    {
        this.options = options;
        this.time = time;
        this.jitter = jitter ?? Random.Shared.NextDouble;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var contentHeaders = request.Content?.Headers.ToList();
        for (var attempt = 0; ; attempt++)
        {
            using var attemptRequest = Clone(request, body, contentHeaders, attempt);
            using var attemptTimeout = new CancellationTokenSource(options.AttemptTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(attemptRequest, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= options.MaxRetries)
                    throw new TimeoutException("The Claude request timed out.");
                await DelayAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException) when (attempt < options.MaxRetries)
            {
                await DelayAsync(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt >= options.MaxRetries || !ShouldRetry(response, out var delay, attempt))
                return response;
            response.Dispose();
            await DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ShouldRetry(HttpResponseMessage response, out TimeSpan delay, int attempt)
    {
        delay = TimeSpan.Zero;
        if (response.Headers.TryGetValues("x-should-retry", out var values))
        {
            var value = values.FirstOrDefault();
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) && !Retryable(response.StatusCode))
                return false;
        }
        else if (!Retryable(response.StatusCode))
            return false;
        if (RetryAfter(response) is { } hinted)
        {
            if (hinted > options.MaximumRetryAfter)
                return false;
            delay = hinted;
            return true;
        }
        delay = Backoff(attempt);
        return true;
    }

    internal static bool Retryable(HttpStatusCode status) => (int)status is 408 or 409 or 429 or >= 500;

    internal TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var milliseconds)
            && double.TryParse(milliseconds.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && double.IsFinite(ms) && ms >= 0)
            return TimeSpan.FromMilliseconds(Math.Min(ms, TimeSpan.FromDays(1).TotalMilliseconds));
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return delta;
        if (retryAfter?.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = options.InitialBackoff.TotalMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(exponential, options.MaximumBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped * (0.5 + 0.5 * jitter()));
    }

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, time, cancellationToken);

    private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body, List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders, int attempt)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version, VersionPolicy = request.VersionPolicy };
        foreach (var header in request.Headers)
            if (!string.Equals(header.Key, "x-stainless-retry-count", StringComparison.OrdinalIgnoreCase))
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        clone.Headers.TryAddWithoutValidation("x-stainless-retry-count", attempt.ToString(CultureInfo.InvariantCulture));
        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in contentHeaders ?? [])
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in request.Options)
            ((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
        return clone;
    }
}
