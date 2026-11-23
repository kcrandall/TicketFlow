using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TicketFlow.Assist;

/// <summary>
/// Telemetry names and middleware for assistant chat clients. Tags carry feature, model, outcome and tool names only — never prompts,
/// outputs, tool arguments or results.
/// </summary>
public static partial class AssistTelemetry
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
        var featureName = request.Feature.ToString();
        return builder.Use(async (messages, options, inner, cancellationToken) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
                var toolCallCount = 0;
                var responseMessages = response.Messages;
                for (var m = 0; m < responseMessages.Count; m++)
                {
                    var contents = responseMessages[m].Contents;
                    for (var c = 0; c < contents.Count; c++)
                        if (contents[c] is FunctionCallContent)
                            toolCallCount++;
                }
                Record(model, featureName, "success", started, response.Usage, toolCallCount, response.FinishReason?.Value);
                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Record(model, featureName, "canceled", started, null, 0, null);
                throw;
            }
            catch
            {
                Record(model, featureName, "failure", started, null, 0, null);
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
                if (activity is { IsAllDataRequested: true })
                {
                    activity.SetTag("ai.tool.name", toolName);
                    activity.SetTag("ai.tool.call_id", context.CallContent.CallId);
                }
                using var scope = logger is not null && (logger.IsEnabled(LogLevel.Information) || logger.IsEnabled(LogLevel.Warning))
                    ? logger.BeginScope(new Dictionary<string, object?> { ["tool_name"] = toolName, ["tool_call_id"] = context.CallContent.CallId })
                    : null;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var result = await next(context, cancellationToken).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var resultType = result?.GetType().Name;
                    if (activity is { IsAllDataRequested: true })
                    {
                        activity.SetTag("ai.tool.success", true);
                        activity.SetTag("ai.tool.duration_ms", elapsed.TotalMilliseconds);
                        activity.SetTag("ai.tool.result_type", resultType);
                    }
                    if (logger is not null)
                        LogToolCompleted(logger, (long)elapsed.TotalMilliseconds, resultType);
                    return result;
                }
                catch (Exception error)
                {
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var exceptionType = error.GetType().Name;
                    if (activity is not null)
                    {
                        activity.SetStatus(ActivityStatusCode.Error, exceptionType);
                        if (activity.IsAllDataRequested)
                        {
                            activity.SetTag("ai.tool.success", false);
                            activity.SetTag("ai.tool.duration_ms", elapsed.TotalMilliseconds);
                            activity.SetTag("ai.tool.exception_type", exceptionType);
                        }
                    }
                    if (logger is not null)
                        LogToolFailed(logger, (long)elapsed.TotalMilliseconds, exceptionType);
                    throw;
                }
            };
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Assistant tool invocation completed. DurationMs={DurationMs} ResultType={ResultType}")]
    private static partial void LogToolCompleted(ILogger logger, long durationMs, string? resultType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Assistant tool invocation failed. DurationMs={DurationMs} ExceptionType={ExceptionType}")]
    private static partial void LogToolFailed(ILogger logger, long durationMs, string exceptionType);

    private static void Record(AssistModelDefinition model, string featureName, string outcome, long started, UsageDetails? usage, int toolCalls, string? finishReason)
    {
        var tags = new TagList
        {
            { "ticketflow.assist.feature", featureName },
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
