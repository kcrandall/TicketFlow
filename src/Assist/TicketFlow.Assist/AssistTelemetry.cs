using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TicketFlow.Assist;

/// <summary>
/// Telemetry names and middleware for assistant chat clients. Tags carry feature, model, outcome and tool names only — never prompts,
/// outputs, tool arguments or results.
/// </summary>
public static class AssistTelemetry
{
    public const string ActivitySourceName = "TicketFlow.Assist";
    public const string ToolActivitySourceName = "TicketFlow.Assist.Tools";
    public const string MeterName = "TicketFlow.Assist";

    private static readonly ActivitySource ToolSource = new(ToolActivitySourceName);
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("ticketflow.assist.requests", "requests", "Assistant chat requests.");
    private static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("ticketflow.assist.input_tokens", "tokens", "Assistant input tokens (including cache reads and writes).");
    private static readonly Counter<long> OutputTokens = Meter.CreateCounter<long>("ticketflow.assist.output_tokens", "tokens", "Assistant output tokens.");
    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("ticketflow.assist.tool_calls", "calls", "Tool calls requested by assistant models.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("ticketflow.assist.duration", "ms", "Assistant chat request duration.");

    /// <summary>Records request counts, token usage, tool calls and duration per feature and model.</summary>
    public static ChatClientBuilder UseAssistUsageMetrics(this ChatClientBuilder builder, AssistModelDefinition model, AssistChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Use(async (messages, options, inner, cancellationToken) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                Record(model, request, "success", started, response.Usage,
                    response.Messages.SelectMany(message => message.Contents).Count(content => content is FunctionCallContent), response.FinishReason?.Value);
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Record(model, request, "canceled", started, null, 0, null);
                throw;
            }
            catch
            {
                Record(model, request, "failure", started, null, 0, null);
                throw;
            }
        }, null);
    }

    /// <summary>
    /// Adds function invocation (for callers that pass <see cref="AIFunction"/> tools) with one span and structured log scope per tool
    /// call: tool name, call identifier, success, duration and result/exception type. Arguments and results are never recorded.
    /// </summary>
    public static ChatClientBuilder UseAssistToolInvocationTelemetry(this ChatClientBuilder builder, ILoggerFactory? loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var logger = loggerFactory?.CreateLogger("TicketFlow.Assist.ToolInvoker");
        return builder.UseFunctionInvocation(loggerFactory, invocation =>
        {
            var next = invocation.FunctionInvoker ?? ((context, cancellationToken) => context.Function.InvokeAsync(context.Arguments, cancellationToken));
            invocation.FunctionInvoker = async (context, cancellationToken) =>
            {
                using var activity = ToolSource.StartActivity("ticketflow.assist.tool.invoke");
                var toolName = context.CallContent.Name;
                activity?.SetTag("ai.tool.name", toolName);
                activity?.SetTag("ai.tool.call_id", context.CallContent.CallId);
                using var scope = logger?.BeginScope(new Dictionary<string, object?> { ["tool_name"] = toolName, ["tool_call_id"] = context.CallContent.CallId });
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var result = await next(context, cancellationToken).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    activity?.SetTag("ai.tool.success", true);
                    activity?.SetTag("ai.tool.duration_ms", elapsed.TotalMilliseconds);
                    activity?.SetTag("ai.tool.result_type", result?.GetType().Name);
                    logger?.LogInformation("Assistant tool invocation completed. DurationMs={DurationMs} ResultType={ResultType}", (long)elapsed.TotalMilliseconds, result?.GetType().Name);
                    return result;
                }
                catch (Exception error)
                {
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
                    activity?.SetTag("ai.tool.success", false);
                    activity?.SetTag("ai.tool.duration_ms", elapsed.TotalMilliseconds);
                    activity?.SetTag("ai.tool.exception_type", error.GetType().Name);
                    logger?.LogWarning("Assistant tool invocation failed. DurationMs={DurationMs} ExceptionType={ExceptionType}", (long)elapsed.TotalMilliseconds, error.GetType().Name);
                    throw;
                }
            };
        });
    }

    private static void Record(AssistModelDefinition model, AssistChatRequest request, string outcome, long started, UsageDetails? usage, int toolCalls, string? finishReason)
    {
        var tags = new TagList
        {
            { "ticketflow.assist.feature", request.Feature.ToString() },
            { "ticketflow.assist.model", model.Id },
            { "ticketflow.assist.provider", model.Provider },
            { "ticketflow.assist.outcome", outcome },
        };
        if (finishReason is not null)
            tags.Add("ticketflow.assist.finish_reason", finishReason);
        Requests.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        if (usage?.InputTokenCount is { } input)
            InputTokens.Add(input, tags);
        if (usage?.OutputTokenCount is { } output)
            OutputTokens.Add(output, tags);
        if (toolCalls > 0)
            ToolCalls.Add(toolCalls, tags);
    }
}
