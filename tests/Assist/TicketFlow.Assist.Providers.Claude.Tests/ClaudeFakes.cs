using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace TicketFlow.Assist.Providers.Claude.Tests;

/// <summary>Captures requests (headers and bodies) and replays scripted responses or exceptions. Never touches the network.</summary>
internal sealed class FakeAnthropicHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<CancellationToken, Task<HttpResponseMessage>>> script = new();

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public void Respond(int status, string json, Action<HttpResponseMessage>? configure = null) => script.Enqueue(_ =>
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        configure?.Invoke(response);
        return Task.FromResult(response);
    });

    public void Throw(Exception error) => script.Enqueue(_ => Task.FromException<HttpResponseMessage>(error));

    public void Hang() => script.Enqueue(async cancellationToken =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("unreachable");
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(CancellationToken.None)));
        if (!script.TryDequeue(out var next))
            throw new AssertFailedException("No scripted HTTP response is left.");
        return await next(cancellationToken);
    }

    public static string Message(string text, string stopReason = "end_turn", string extra = "") => $$$"""
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5",
         "content":[{"type":"thinking","thinking":"","signature":"sig"},{"type":"text","text":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}],
         "stop_reason":"{{{stopReason}}}","stop_sequence":null{{{extra}}},
         "usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":300,"cache_creation_input_tokens":20}}
        """;

    public static string Error(string type, string message) => $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"{{{message}}}"}}""";
}

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire immediately when due within <see cref="FireThreshold"/> (recording the delay) and
/// never otherwise, so backoff and timeouts are deterministic without waiting.
/// </summary>
internal sealed class InstantTimeProvider(TimeSpan fireThreshold) : TimeProvider
{
    public TimeSpan FireThreshold { get; } = fireThreshold;
    public ConcurrentQueue<TimeSpan> Fired { get; } = new();

    public override DateTimeOffset GetUtcNow() => new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan && dueTime <= FireThreshold)
        {
            Fired.Enqueue(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Minimal in-memory <see cref="IConfiguration"/>.</summary>
internal sealed class DictionaryConfiguration(Dictionary<string, string?> values) : IConfiguration
{
    public string? this[string key]
    {
        get => values.GetValueOrDefault(key);
        set => values[key] = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => throw new NotSupportedException();

    public IConfigurationSection GetSection(string key) => throw new NotSupportedException();
}
