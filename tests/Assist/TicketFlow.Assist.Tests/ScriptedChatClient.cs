using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace TicketFlow.Assist.Tests;

/// <summary>One scripted model turn: a reply (JSON text, finish reason, usage), optional function calls, or an exception to throw.</summary>
internal sealed record ScriptedTurn(string? Reply = null, ChatFinishReason? Finish = null, UsageDetails? Usage = null, Exception? Error = null,
    IReadOnlyList<FunctionCallContent>? Calls = null);

/// <summary>Replays scripted turns and captures every request (instructions, messages and options) for assertions.</summary>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ScriptedTurn> turns = new();

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Requests { get; } = [];

    /// <summary>Everything sent to the model, concatenated: what a provider would serialize into its request body.</summary>
    public string Transcript => string.Join("\n", Requests.Select(request =>
        (request.Options?.Instructions ?? "") + "\n" + string.Join("\n", request.Messages.Select(message => message.Role + ": " + message.Text))));

    public void Enqueue(ScriptedTurn turn) => turns.Enqueue(turn);

    public void Reply(string json, long input = 100, long output = 20) =>
        Enqueue(new ScriptedTurn(json, ChatFinishReason.Stop, new UsageDetails { InputTokenCount = input, OutputTokenCount = output }));

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(([.. messages], options));
        if (!turns.TryDequeue(out var turn))
            throw new AssertFailedException("No scripted model turn is left.");
        if (turn.Error is not null)
            throw turn.Error;
        var message = new ChatMessage(ChatRole.Assistant, turn.Reply ?? "");
        foreach (var call in turn.Calls ?? [])
            message.Contents.Add(call);
        return Task.FromResult(new ChatResponse(message) { FinishReason = turn.Finish ?? (turn.Calls is { Count: > 0 } ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop), Usage = turn.Usage });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A provider named <c>scripted</c> that hands out the shared <see cref="ScriptedChatClient"/> and records applied options.</summary>
internal sealed class ScriptedChatProvider(ScriptedChatClient client) : IAssistChatProvider
{
    public const string ProviderName = "scripted";

    public List<(AssistModelDefinition Model, AssistChatRequest Request)> Applied { get; } = [];

    public string Name => ProviderName;

    public IChatClient CreateClient(AssistModelDefinition model) => client;

    public ChatOptions ApplyOptions(AssistModelDefinition model, AssistChatRequest request, ChatOptions options)
    {
        Applied.Add((model, request));
        return options.Clone();
    }
}

/// <summary>A fake <see cref="HttpMessageHandler"/> that captures request bodies and replays scripted HTTP responses.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> responses = new();

    public List<string> Bodies { get; } = [];

    public void Enqueue(string json, int status = 200) =>
        responses.Enqueue(() => new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static string Message(string text, string stopReason = "end_turn", string model = "claude-sonnet-5-5") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model,
            content = new object[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 40, output_tokens = 12, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 },
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(CancellationToken.None));
        if (!responses.TryDequeue(out var response))
            throw new AssertFailedException("No scripted HTTP response is left.");
        return response();
    }
}
