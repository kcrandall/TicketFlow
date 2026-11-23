using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TicketFlow.Assist.Providers.Claude;

/// <summary>
/// <see cref="IAssistChatProvider"/> for Claude over the official Anthropic .NET SDK and its <see cref="IChatClient"/> adapter. One SDK
/// client (with SDK retries disabled) sits on an <see cref="HttpClient"/> whose <see cref="ClaudeRetryHandler"/> owns retries and
/// attempt timeouts. <see cref="ApplyOptions"/> moves <see cref="ChatOptions.Instructions"/> into a cached system block
/// (<c>cache_control: ephemeral</c>) and maps the thinking level to <c>output_config.effort</c>; JSON-schema response formats become
/// <c>output_config.format</c>. Failures surface as <see cref="AssistProviderException"/>. Nothing is logged.
/// </summary>
public sealed class ClaudeAssistProvider : IAssistChatProvider, IDisposable
{
    private readonly ClaudeAssistOptions options;
    private readonly IConfiguration? configuration;
    private readonly HttpClient http;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private AnthropicClient? client;

    public ClaudeAssistProvider(IOptions<ClaudeAssistOptions> options, TimeProvider? time = null, IConfiguration? configuration = null)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), null, time, configuration)
    {
    }

    /// <summary>Creates the provider over <paramref name="handler"/> (tests pass a fake; production uses a pooled <see cref="SocketsHttpHandler"/>).</summary>
    public ClaudeAssistProvider(ClaudeAssistOptions options, HttpMessageHandler? handler, TimeProvider? time = null, IConfiguration? configuration = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        this.time = time ?? TimeProvider.System;
        this.configuration = configuration;
        var primary = handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        http = new HttpClient(new ClaudeRetryHandler(options, this.time, primary), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Name => ClaudeAssistOptions.ProviderName;

    public IChatClient CreateClient(AssistModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ClaudeChatClient(this, model.ProviderModel);
    }

    public ChatOptions ApplyOptions(AssistModelDefinition model, AssistChatRequest request, ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var effective = options.Clone();
        var instructions = effective.Instructions;
        var effort = effective.Reasoning?.Effort;
        var cache = this.options.CacheSystemPrompt;
        var previous = effective.RawRepresentationFactory;
        effective.Instructions = null;
        effective.RawRepresentationFactory = chatClient =>
        {
            if (previous?.Invoke(chatClient) is { } raw)
                return raw;
            var parameters = new MessageCreateParams
            {
                Model = model.ProviderModel,
                MaxTokens = effective.MaxOutputTokens ?? 4096,
                Messages = [],
            };
            if (instructions is not null)
                parameters = parameters with
                {
                    System = new List<TextBlockParam> { cache ? new() { Text = instructions, CacheControl = new CacheControlEphemeral() } : new() { Text = instructions } },
                };
            if (effort is { } level && model.Capabilities.HasFlag(AssistModelCapabilities.Reasoning))
                parameters = parameters with
                {
                    OutputConfig = new OutputConfig
                    {
                        Effort = level == ReasoningEffort.Low ? Effort.Low : level == ReasoningEffort.Medium ? Effort.Medium : Effort.High,
                    },
                };
            return parameters;
        };
        return effective;
    }

    /// <summary>The default Claude model registrations.</summary>
    public static IReadOnlyList<AssistModelDefinition> DefaultModels { get; } =
    [
        new(ClaudeAssistOptions.HaikuModel, ClaudeAssistOptions.ProviderName, ClaudeAssistOptions.HaikuModel, Capabilities, AssistThinkingLevel.Low),
        new(ClaudeAssistOptions.SonnetModel, ClaudeAssistOptions.ProviderName, ClaudeAssistOptions.SonnetModel, Capabilities, AssistThinkingLevel.Medium),
        new(ClaudeAssistOptions.OpusModel, ClaudeAssistOptions.ProviderName, ClaudeAssistOptions.OpusModel, Capabilities, AssistThinkingLevel.Medium),
    ];

    private const AssistModelCapabilities Capabilities = AssistModelCapabilities.Streaming | AssistModelCapabilities.Tools | AssistModelCapabilities.StructuredOutput
        | AssistModelCapabilities.Reasoning | AssistModelCapabilities.PromptCaching;

    public void Dispose() => http.Dispose();

    internal TimeProvider Time => time;

    internal ClaudeAssistOptions Options => options;

    /// <summary>The SDK client, created at first use; throws <see cref="AssistProviderException"/> (NotConfigured) without an API key.</summary>
    internal AnthropicClient Client()
    {
        lock (gate)
        {
            if (client is not null)
                return client;
            var key = options.ApiKey;
            if (string.IsNullOrWhiteSpace(key))
                key = configuration?["TicketFlow:Assist:Claude:ApiKey"];
            if (string.IsNullOrWhiteSpace(key))
                key = configuration?["ANTHROPIC_API_KEY"];
            if (string.IsNullOrWhiteSpace(key))
                throw new AssistProviderException(AssistProviderFailure.NotConfigured, false, tokensMayHaveBeenConsumed: false);
            client = options.BaseUrl is { } baseUrl
                ? new AnthropicClient { ApiKey = key, HttpClient = http, MaxRetries = 0, Timeout = options.TotalTimeout, BaseUrl = baseUrl.ToString().TrimEnd('/') }
                : new AnthropicClient { ApiKey = key, HttpClient = http, MaxRetries = 0, Timeout = options.TotalTimeout };
            return client;
        }
    }

    /// <summary>Maps SDK and transport failures to provider-neutral failures.</summary>
    internal static AssistProviderException Map(Exception error) => error switch
    {
        AssistProviderException mapped => mapped,
        AnthropicApiException api => Status((int)api.StatusCode, api),
        AnthropicIOException => new(AssistProviderFailure.Network, true),
        TimeoutException => new(AssistProviderFailure.Timeout, true),
        HttpRequestException => new(AssistProviderFailure.Network, true),
        JsonException or AnthropicInvalidDataException or FormatException or InvalidOperationException => new(AssistProviderFailure.InvalidResponse, true),
        _ => new(AssistProviderFailure.InvalidResponse, false),
    };

    /// <summary>An error status means the API rejected the request without generating output, so nothing was consumed.</summary>
    private static AssistProviderException Status(int status, Exception error)
    {
        var (failure, retryable) = status switch
        {
            400 or 422 => (AssistProviderFailure.InvalidRequest, false),
            401 => (AssistProviderFailure.Authentication, false),
            403 => (AssistProviderFailure.PermissionDenied, false),
            404 => (AssistProviderFailure.NotFound, false),
            413 => (AssistProviderFailure.RequestTooLarge, false),
            408 => (AssistProviderFailure.Timeout, true),
            429 => (AssistProviderFailure.RateLimited, true),
            529 or 503 => (AssistProviderFailure.Overloaded, true),
            >= 500 => (AssistProviderFailure.ServerError, true),
            _ => (AssistProviderFailure.InvalidRequest, false),
        };
        return new(failure, retryable, status, innerException: null, tokensMayHaveBeenConsumed: false);
    }
}

/// <summary>
/// The per-model Claude <see cref="IChatClient"/>: resolves the SDK client lazily (so a missing key fails per call, not once forever),
/// enforces the total timeout and translates failures into <see cref="AssistProviderException"/>.
/// </summary>
internal sealed class ClaudeChatClient(ClaudeAssistProvider provider, string model) : IChatClient
{
    private readonly Lock innerGate = new();
    private IChatClient? inner;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var total = new CancellationTokenSource(provider.Options.TotalTimeout, provider.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, total.Token);
        try
        {
            return await Inner().GetResponseAsync(messages, options, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error)
        {
            throw new AssistProviderException(AssistProviderFailure.Timeout, true, innerException: error);
        }
        catch (Exception error) when (error is not AssistProviderException)
        {
            throw ClaudeAssistProvider.Map(error);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> enumerator;
        try
        {
            enumerator = Inner().GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception error) when (error is not (AssistProviderException or OperationCanceledException))
        {
            throw ClaudeAssistProvider.Map(error);
        }
        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        yield break;
                    update = enumerator.Current;
                }
                catch (Exception error) when (error is not (AssistProviderException or OperationCanceledException))
                {
                    throw ClaudeAssistProvider.Map(error);
                }
                yield return update;
            }
        }
    }

    public object? GetService(System.Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private IChatClient Inner()
    {
        if (Volatile.Read(ref inner) is { } existing)
            return existing;
        lock (innerGate)
        {
            if (inner is not null)
                return inner;
            try
            {
                var created = provider.Client().AsIChatClient(model);
                Volatile.Write(ref inner, created);
                return created;
            }
            catch (Exception error) when (error is not AssistProviderException)
            {
                throw ClaudeAssistProvider.Map(error);
            }
        }
    }
}
