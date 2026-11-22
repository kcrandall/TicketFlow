using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace TicketFlow.Assist;

/// <summary>
/// The default <see cref="IAssistChatClientFactory"/>. Base clients are created once per (provider, provider model) by the matching
/// <see cref="IAssistChatProvider"/> and wrapped per request in the pipeline: option mapping (model, thinking level, provider
/// specifics), usage metrics, tool-invocation telemetry and OpenTelemetry with sensitive data disabled.
/// </summary>
public sealed class AssistChatClientFactory : IAssistChatClientFactory
{
    private readonly IAssistModelRegistry registry;
    private readonly IReadOnlyDictionary<string, IAssistChatProvider> providers;
    private readonly ILoggerFactory? loggerFactory;
    private readonly ConcurrentDictionary<(string Provider, string Model), Lazy<IChatClient>> clients = new();

    public AssistChatClientFactory(IAssistModelRegistry registry, IEnumerable<IAssistChatProvider> providers, ILoggerFactory? loggerFactory = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(providers);
        var map = new Dictionary<string, IAssistChatProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
            map.TryAdd(provider.Name, provider);
        this.providers = map;
        this.loggerFactory = loggerFactory;
    }

    public IChatClient GetChatClient(AssistChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!registry.TryGet(request.ModelId, out var model))
            throw new ArgumentException($"Model '{request.ModelId}' is not registered.", nameof(request));
        if (!providers.TryGetValue(model.Provider, out var provider))
            throw new InvalidOperationException($"No assistant provider named '{model.Provider}' is registered.");
        var baseClient = clients.GetOrAdd((model.Provider, model.ProviderModel),
            _ => new Lazy<IChatClient>(() => provider.CreateClient(model), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return baseClient.AsBuilder()
            .Use((messages, options, inner, cancellationToken) => inner.GetResponseAsync(messages, Apply(provider, model, request, options), cancellationToken),
                (messages, options, inner, cancellationToken) => inner.GetStreamingResponseAsync(messages, Apply(provider, model, request, options), cancellationToken))
            .UseAssistUsageMetrics(model, request)
            .UseAssistToolInvocationTelemetry(loggerFactory)
            .UseOpenTelemetry(loggerFactory, AssistTelemetry.ActivitySourceName, telemetry => telemetry.EnableSensitiveData = false)
            .Build();
    }

    private static ChatOptions Apply(IAssistChatProvider provider, AssistModelDefinition model, AssistChatRequest request, ChatOptions? options)
    {
        var effective = options?.Clone() ?? new ChatOptions();
        effective.ModelId = model.ProviderModel;
        var thinking = request.ThinkingLevel ?? model.DefaultThinkingLevel;
        if (thinking is { } level)
            effective.Reasoning = new ReasoningOptions
            {
                Effort = level switch
                {
                    AssistThinkingLevel.Low => ReasoningEffort.Low,
                    AssistThinkingLevel.Medium => ReasoningEffort.Medium,
                    AssistThinkingLevel.High => ReasoningEffort.High,
                    _ => throw new ArgumentOutOfRangeException(nameof(request)),
                },
            };
        return provider.ApplyOptions(model, request, effective);
    }
}
