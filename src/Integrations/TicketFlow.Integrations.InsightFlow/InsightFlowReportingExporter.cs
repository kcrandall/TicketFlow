using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Reporting;

namespace TicketFlow.Integrations.InsightFlow;

/// <summary>Result of one InsightFlow export run for a workspace.</summary>
public sealed record InsightFlowExportResult(int Exported, int Batches, bool HasMore, string? FailureCode = null, bool PermanentFailure = false, TimeSpan? RetryAfter = null);

/// <summary>
/// Exports reporting facts from <see cref="IReportingStore.ListFactsAsync"/> to InsightFlow over HTTP in InsightFlow's
/// <c>ClientTrackBatchRequest</c> wire format. Advances the checkpoint only after HTTP 2xx acknowledgement and automatically splits
/// batches that exceed <see cref="InsightFlowExportOptions.MaximumPayloadBytes"/> or receive HTTP 413.
/// </summary>
public sealed class InsightFlowReportingExporter : IDisposable
{
    private readonly IReportingStore reporting;
    private readonly IInsightFlowExportCheckpointStore checkpoints;
    private readonly InsightFlowExportOptions options;
    private readonly TimeProvider time;
    private readonly HttpClient client;

    public InsightFlowReportingExporter(IReportingStore reporting, IInsightFlowExportCheckpointStore checkpoints, IOptions<InsightFlowExportOptions> options,
        TimeProvider time, HttpMessageHandler? handler = null)
    {
        this.reporting = reporting ?? throw new ArgumentNullException(nameof(reporting));
        this.checkpoints = checkpoints ?? throw new ArgumentNullException(nameof(checkpoints));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = this.options.Timeout };
    }

    /// <summary>Reads up to one page of facts for <paramref name="scope"/>, exports them, and advances the checkpoint on success.</summary>
    public async Task<InsightFlowExportResult> ExportOnceAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = time.GetUtcNow().ToUniversalTime();
        var existing = await checkpoints.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        var from = existing?.From ?? now - options.InitialLookback;
        var to = now;
        if (to - from > ReportFactQuery.MaximumRange)
            to = from + ReportFactQuery.MaximumRange;
        if (to <= from)
            return new InsightFlowExportResult(0, 0, false);

        var query = new ReportFactQuery(from, to, ReportFilter.None, options.BatchSize, existing?.Cursor);
        var page = await reporting.ListFactsAsync(scope, query, AllowAllFilter.Instance, cancellationToken).ConfigureAwait(false);
        if (page.Items.Count == 0)
        {
            // Advance the lower window bound to `to` when caught up so ranges stay within ReportFactQuery.MaximumRange.
            var advanced = new InsightFlowExportCheckpoint(to, null, existing?.TotalExported ?? 0, now);
            await checkpoints.SetAsync(scope, advanced, existing, cancellationToken).ConfigureAwait(false);
            return new InsightFlowExportResult(0, 0, to < now);
        }

        var (batches, failureCode, permanent, retryAfter) = await SendWithSplitAsync(scope, page.Items, cancellationToken).ConfigureAwait(false);
        if (failureCode is not null)
            return new InsightFlowExportResult(0, batches, true, failureCode, permanent, retryAfter);

        var last = page.Items[^1];
        var nextCheckpoint = page.Next is not null
            ? new InsightFlowExportCheckpoint(from, page.Next, (existing?.TotalExported ?? 0) + page.Items.Count, now)
            : new InsightFlowExportCheckpoint(last.OccurredAt, new ReportFactCursor(last.OccurredAt, last.SourceId, last.Metric),
                (existing?.TotalExported ?? 0) + page.Items.Count, now);
        await checkpoints.SetAsync(scope, nextCheckpoint, existing, cancellationToken).ConfigureAwait(false);
        return new InsightFlowExportResult(page.Items.Count, batches, page.Next is not null || to < now);
    }

    private async Task<(int Batches, string? FailureCode, bool Permanent, TimeSpan? RetryAfter)> SendWithSplitAsync(TicketScope scope, IReadOnlyList<ReportFact> facts,
        CancellationToken cancellationToken)
    {
        var payload = InsightFlowClientEventEncoder.EncodeBatch(scope, facts, options);
        if (payload.Length > options.MaximumPayloadBytes)
        {
            if (facts.Count == 1)
                return (0, "payload-too-large", true, null);
            return await SendHalvesAsync(scope, facts, cancellationToken).ConfigureAwait(false);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        if (options.ApiKey is not null)
        {
            if (string.Equals(options.AuthorizationHeaderName, "Authorization", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(options.AuthorizationScheme))
                request.Headers.Authorization = new AuthenticationHeaderValue(options.AuthorizationScheme, options.ApiKey);
            else
                request.Headers.TryAddWithoutValidation(options.AuthorizationHeaderName, options.ApiKey);
        }
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300)
                return (1, null, false, null);
            if (status == 413 && facts.Count > 1)
                return await SendHalvesAsync(scope, facts, cancellationToken).ConfigureAwait(false);
            var code = "http-" + status.ToString(CultureInfo.InvariantCulture);
            var permanent = status is not (408 or 429 or >= 500);
            return (0, code, permanent, response.Headers.RetryAfter?.Delta);
        }
        catch (HttpRequestException)
        {
            return (0, "transport-error", false, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (0, "timeout", false, null);
        }
    }

    private async Task<(int Batches, string? FailureCode, bool Permanent, TimeSpan? RetryAfter)> SendHalvesAsync(TicketScope scope, IReadOnlyList<ReportFact> facts,
        CancellationToken cancellationToken)
    {
        var mid = facts.Count / 2;
        var left = await SendWithSplitAsync(scope, [.. facts.Take(mid)], cancellationToken).ConfigureAwait(false);
        if (left.FailureCode is not null)
            return left;
        var right = await SendWithSplitAsync(scope, [.. facts.Skip(mid)], cancellationToken).ConfigureAwait(false);
        return (left.Batches + right.Batches, right.FailureCode, right.Permanent, right.RetryAfter);
    }

    public void Dispose() => client.Dispose();
}
