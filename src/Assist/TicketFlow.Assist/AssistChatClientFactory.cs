using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TicketFlow.Assist;

/// <summary>
/// The default <see cref="IAssistChatClientFactory"/> (a singleton). For each provider the base <see cref="IChatClient"/> is the
/// host's keyed registration (<see cref="AssistChatClients.ServiceKey"/>, owned and disposed by the host) or else one client per
/// provider model created by the <see cref="IAssistChatProvider"/> (owned by this factory). Pipelines are built once per (model,
/// feature, thinking level) — a bounded set — and cached: option mapping, host middleware (<see cref="AssistOptions.Pipeline"/>), usage
/// metrics, tool-invocation telemetry and OpenTelemetry with sensitive data disabled. Disposing the factory (with the container)
/// disposes every cached pipeline and every provider-created base client.
/// </summary>
public sealed class AssistChatClientFactory : IAssistChatClientFactory, IDisposable
{
    private readonly IAssistModelRegistry registry;
    private readonly Dictionary<string, IAssistChatProvider> providers;
    private readonly IServiceProvider? services;
    private readonly IReadOnlyList<Action<ChatClientBuilder, AssistModelDefinition, AssistChatRequest>> middleware;
    private readonly ILoggerFactory? loggerFactory;
    private readonly ConcurrentDictionary<(string Provider, string Model), Lazy<BaseClient>> baseClients = new();
    private readonly ConcurrentDictionary<(string Model, AssistFeature Feature, AssistThinkingLevel? Thinking), Lazy<IChatClient>> pipelines = new();
    private volatile bool disposed;

    public AssistChatClientFactory(IAssistModelRegistry registry, IEnumerable<IAssistChatProvider> providers, IOptions<AssistOptions>? options = null,
        IServiceProvider? services = null, ILoggerFactory? loggerFactory = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(providers);
        var map = new Dictionary<string, IAssistChatProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
            map.TryAdd(provider.Name, provider);
        this.providers = map;
        this.services = services;
        middleware = [.. options?.Value.Pipeline ?? []];
        this.loggerFactory = loggerFactory;
    }

    public IChatClient GetChatClient(AssistChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!registry.TryGet(request.ModelId, out var model))
            throw new ArgumentException($"Model '{request.ModelId}' is not registered.", nameof(request));
        if (!providers.TryGetValue(model.Provider, out var provider))
            throw new InvalidOperationException($"No assistant provider named '{model.Provider}' is registered.");
        var key = (model.Id, request.Feature, request.ThinkingLevel);
        var lazy = pipelines.GetOrAdd(key, _ => new Lazy<IChatClient>(() => Build(provider, model, request with { ModelId = model.Id }), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value;
        }
        catch
        {
            // Never cache a failed build (for example a provider that is not configured yet).
            pipelines.TryRemove(new KeyValuePair<(string, AssistFeature, AssistThinkingLevel?), Lazy<IChatClient>>(key, lazy));
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        foreach (var pipeline in pipelines.Values.Where(lazy => lazy.IsValueCreated))
            pipeline.Value.Dispose();
        pipelines.Clear();
        foreach (var client in baseClients.Values.Where(lazy => lazy.IsValueCreated).Select(lazy => lazy.Value).Where(client => client.Owned))
            client.Client.Dispose();
        baseClients.Clear();
    }

    private IChatClient Build(IAssistChatProvider provider, AssistModelDefinition model, AssistChatRequest request)
    {
        var baseClient = BaseClientFor(provider, model);
        var builder = new NonDisposingChatClient(baseClient).AsBuilder()
            .Use((messages, options, inner, cancellationToken) => inner.GetResponseAsync(messages, Apply(provider, model, request, options), cancellationToken),
                (messages, options, inner, cancellationToken) => inner.GetStreamingResponseAsync(messages, Apply(provider, model, request, options), cancellationToken));
        foreach (var configure in middleware)
            configure(builder, model, request);
        return builder
            .UseAssistUsageMetrics(model, request)
            .UseAssistToolInvocationTelemetry(loggerFactory)
            .UseOpenTelemetry(loggerFactory, AssistTelemetry.ActivitySourceName, telemetry => telemetry.EnableSensitiveData = false)
            .Build(services);
    }

    private IChatClient BaseClientFor(IAssistChatProvider provider, AssistModelDefinition model)
    {
        if (services is IKeyedServiceProvider keyed && keyed.GetKeyedService<IChatClient>(AssistChatClients.ServiceKey(provider.Name)) is { } hosted)
            return hosted;
        var key = (provider.Name, model.ProviderModel);
        var lazy = baseClients.GetOrAdd(key, _ => new Lazy<BaseClient>(() => new BaseClient(provider.CreateClient(model), true), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return lazy.Value.Client;
        }
        catch
        {
            baseClients.TryRemove(new KeyValuePair<(string, string), Lazy<BaseClient>>(key, lazy));
            throw;
        }
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

    private sealed record BaseClient(IChatClient Client, bool Owned);

    /// <summary>Shields a shared base client from the pipeline's dispose chain; the factory (or the host) owns it.</summary>
    private sealed class NonDisposingChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Intentionally shields the shared inner IChatClient from disposal.")]
        protected override void Dispose(bool disposing)
        {
        }
    }
}
