using System.Text.Json;
using Microsoft.Extensions.AI;

namespace TicketFlow.Assist.Providers.Claude.Tests;

[TestClass]
public sealed class ClaudeProviderTests
{
    private static readonly AssistModelDefinition Haiku = ClaudeAssistProvider.DefaultModels.Single(model => model.Id == ClaudeAssistOptions.HaikuModel);
    private const string Schema = """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"],"additionalProperties":false}""";

    private static (ClaudeAssistProvider Provider, FakeAnthropicHandler Handler, InstantTimeProvider Time) Create(Action<ClaudeAssistOptions>? configure = null, TimeSpan? fireThreshold = null)
    {
        var options = new ClaudeAssistOptions { ApiKey = "sk-test-not-real" };
        configure?.Invoke(options);
        var handler = new FakeAnthropicHandler();
        var time = new InstantTimeProvider(fireThreshold ?? TimeSpan.FromSeconds(61));
        return (new ClaudeAssistProvider(options, handler, time), handler, time);
    }

    private static async Task<ChatResponse> SendAsync(ClaudeAssistProvider provider, AssistThinkingLevel? thinking = AssistThinkingLevel.Low, string instructions = "SYSTEM PROMPT")
    {
        var request = new AssistChatRequest(Haiku.Id, AssistFeature.Triage, thinking);
        var options = new ChatOptions
        {
            ModelId = Haiku.ProviderModel,
            Instructions = instructions,
            MaxOutputTokens = 2048,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(Schema).RootElement.Clone(), "triage"),
            Reasoning = thinking is null ? null : new ReasoningOptions { Effort = ReasoningEffort.Low },
        };
        return await provider.CreateClient(Haiku).GetResponseAsync([new ChatMessage(ChatRole.User, "<task>x</task>")], provider.ApplyOptions(Haiku, request, options));
    }

    [TestMethod]
    public async Task RequestJsonShapeUsesCachedSystemPromptStructuredOutputAndEffort()
    {
        var (provider, handler, _) = Create();
        handler.Respond(200, FakeAnthropicHandler.Message("""{"a":"x"}"""));

        var response = await SendAsync(provider);

        var (request, body) = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v1/messages", request.RequestUri!.AbsolutePath);
        Assert.IsTrue(request.Headers.Contains("anthropic-version"));
        Assert.AreEqual("Bearer sk-test-not-real", string.Join(",", request.Headers.GetValues("Authorization")));
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.AreEqual("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.AreEqual(2048, root.GetProperty("max_tokens").GetInt32());
        var system = root.GetProperty("system");
        Assert.AreEqual(1, system.GetArrayLength());
        Assert.AreEqual("SYSTEM PROMPT", system[0].GetProperty("text").GetString());
        Assert.AreEqual("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        var messages = root.GetProperty("messages");
        Assert.AreEqual(1, messages.GetArrayLength());
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("<task>x</task>", messages[0].GetProperty("content")[0].GetProperty("text").GetString());
        var output = root.GetProperty("output_config");
        Assert.AreEqual("low", output.GetProperty("effort").GetString());
        Assert.AreEqual("json_schema", output.GetProperty("format").GetProperty("type").GetString());
        Assert.AreEqual(JsonSerializer.Serialize(JsonDocument.Parse(Schema).RootElement), JsonSerializer.Serialize(output.GetProperty("format").GetProperty("schema")));
        Assert.AreEqual("adaptive", root.GetProperty("thinking").GetProperty("type").GetString(), "the SDK adapter maps reasoning options to adaptive thinking");
        Assert.IsFalse(root.TryGetProperty("temperature", out _));
        Assert.DoesNotContain("sk-test-not-real", body);

        Assert.AreEqual("""{"a":"x"}""", response.Text);
        Assert.AreEqual(ChatFinishReason.Stop, response.FinishReason);
        Assert.AreEqual(new AssistUsage(10, 5, 300, 20), AssistUsage.From(response.Usage));
    }

    [TestMethod]
    public async Task CachingAndEffortCanBeDisabled()
    {
        var (provider, handler, _) = Create(options => options.CacheSystemPrompt = false);
        handler.Respond(200, FakeAnthropicHandler.Message("{}"));
        await SendAsync(provider, thinking: null);
        using var json = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.IsFalse(json.RootElement.GetProperty("system")[0].TryGetProperty("cache_control", out _));
        Assert.IsFalse(json.RootElement.GetProperty("output_config").TryGetProperty("effort", out _));
    }

    [TestMethod]
    public async Task RefusalAndTruncationMapToFinishReasons()
    {
        var (provider, handler, _) = Create();
        handler.Respond(200, FakeAnthropicHandler.Message("", "refusal", ""","stop_details":{"type":"refusal","category":"cyber","explanation":"declined"}"""));
        Assert.AreEqual(ChatFinishReason.ContentFilter, (await SendAsync(provider)).FinishReason);
        handler.Respond(200, FakeAnthropicHandler.Message("{\"a\":", "max_tokens"));
        Assert.AreEqual(ChatFinishReason.Length, (await SendAsync(provider)).FinishReason);
    }

    [TestMethod]
    [DataRow(400, "invalid_request_error", AssistProviderFailure.InvalidRequest, false)]
    [DataRow(401, "authentication_error", AssistProviderFailure.Authentication, false)]
    [DataRow(403, "permission_error", AssistProviderFailure.PermissionDenied, false)]
    [DataRow(404, "not_found_error", AssistProviderFailure.NotFound, false)]
    [DataRow(413, "request_too_large", AssistProviderFailure.RequestTooLarge, false)]
    public async Task ClientErrorsAreFinalAndNotRetried(int status, string type, AssistProviderFailure failure, bool retryable)
    {
        var (provider, handler, _) = Create();
        handler.Respond(status, FakeAnthropicHandler.Error(type, "provider says secret-detail"));
        var error = await Assert.ThrowsExactlyAsync<AssistProviderException>(() => SendAsync(provider));
        Assert.AreEqual(failure, error.Failure);
        Assert.AreEqual(retryable, error.Retryable);
        Assert.AreEqual(status, error.StatusCode);
        Assert.HasCount(1, handler.Requests);
        Assert.DoesNotContain("secret-detail", error.Message);
        Assert.IsFalse(error.TokensMayHaveBeenConsumed, "an error status means nothing was generated");
    }

    [TestMethod]
    [DataRow(429, "rate_limit_error", AssistProviderFailure.RateLimited)]
    [DataRow(529, "overloaded_error", AssistProviderFailure.Overloaded)]
    [DataRow(500, "api_error", AssistProviderFailure.ServerError)]
    public async Task RetryableErrorsAreRetriedWithBackoffThenReported(int status, string type, AssistProviderFailure failure)
    {
        var (provider, handler, time) = Create(options => options.MaxRetries = 2);
        for (var index = 0; index < 3; index++)
            handler.Respond(status, FakeAnthropicHandler.Error(type, "busy"));
        var error = await Assert.ThrowsExactlyAsync<AssistProviderException>(() => SendAsync(provider));
        Assert.AreEqual(failure, error.Failure);
        Assert.IsTrue(error.Retryable);
        Assert.IsFalse(error.TokensMayHaveBeenConsumed);
        Assert.HasCount(3, handler.Requests);
        var delays = time.Fired.ToArray();
        Assert.HasCount(2, delays);
        Assert.IsTrue(delays[0] >= TimeSpan.FromMilliseconds(500) && delays[0] <= TimeSpan.FromSeconds(1), $"first backoff {delays[0]}");
        Assert.IsTrue(delays[1] >= TimeSpan.FromSeconds(1) && delays[1] <= TimeSpan.FromSeconds(2), $"second backoff {delays[1]}");
        CollectionAssert.AreEqual(new[] { "0", "1", "2" }, handler.Requests.Select(request => request.Request.Headers.GetValues("x-stainless-retry-count").Single()).ToArray());
        Assert.IsTrue(handler.Requests.All(request => request.Body == handler.Requests[0].Body), "the same body is replayed");
    }

    [TestMethod]
    public async Task RetryAfterIsHonored()
    {
        var (provider, handler, time) = Create();
        handler.Respond(429, FakeAnthropicHandler.Error("rate_limit_error", "slow"), response => response.Headers.Add("retry-after", "7"));
        handler.Respond(529, FakeAnthropicHandler.Error("overloaded_error", "busy"), response => response.Headers.Add("retry-after-ms", "1500"));
        handler.Respond(200, FakeAnthropicHandler.Message("""{"a":"ok"}"""));

        Assert.AreEqual("""{"a":"ok"}""", (await SendAsync(provider)).Text);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromMilliseconds(1500) }, time.Fired.ToArray());
    }

    [TestMethod]
    public async Task LongRetryAfterAndShouldRetryFalseStopRetrying()
    {
        var (provider, handler, time) = Create(options => options.MaximumRetryAfter = TimeSpan.FromSeconds(30));
        handler.Respond(429, FakeAnthropicHandler.Error("rate_limit_error", "slow"), response => response.Headers.Add("retry-after", "120"));
        Assert.AreEqual(AssistProviderFailure.RateLimited, (await Assert.ThrowsExactlyAsync<AssistProviderException>(() => SendAsync(provider))).Failure);
        handler.Respond(500, FakeAnthropicHandler.Error("api_error", "no"), response => response.Headers.Add("x-should-retry", "false"));
        Assert.AreEqual(AssistProviderFailure.ServerError, (await Assert.ThrowsExactlyAsync<AssistProviderException>(() => SendAsync(provider))).Failure);
        Assert.HasCount(2, handler.Requests);
        Assert.IsEmpty(time.Fired);
    }

    [TestMethod]
    public async Task NetworkFailuresAndAttemptTimeoutsRetryThenMap()
    {
        var (provider, handler, _) = Create(options => options.MaxRetries = 1);
        handler.Throw(new HttpRequestException("connection reset"));
        handler.Respond(200, FakeAnthropicHandler.Message("{}"));
        Assert.AreEqual("{}", (await SendAsync(provider)).Text);

        handler.Throw(new HttpRequestException("connection reset"));
        handler.Throw(new HttpRequestException("connection reset"));
        var network = await Assert.ThrowsExactlyAsync<AssistProviderException>(() => SendAsync(provider));
        Assert.AreEqual(AssistProviderFailure.Network, network.Failure);
        Assert.IsTrue(network.Retryable);
        Assert.IsTrue(network.TokensMayHaveBeenConsumed, "a transport failure may happen after the request was sent");

        var (slow, slowHandler, _) = Create(options =>
        {
            options.MaxRetries = 1;
            options.AttemptTimeout = TimeSpan.FromSeconds(5);
            options.TotalTimeout = TimeSpan.FromMinutes(10);
        });
        slowHandler.Hang();
        slowHandler.Hang();
        var timeout = await Assert.ThrowsExactlyAsync<AssistProviderException>(() => SendAsync(slow));
        Assert.AreEqual(AssistProviderFailure.Timeout, timeout.Failure);
        Assert.IsTrue(timeout.Retryable);
        Assert.IsTrue(timeout.TokensMayHaveBeenConsumed, "a timeout after sending is ambiguous");
        Assert.HasCount(2, slowHandler.Requests, "requests: " + slowHandler.Requests.Count);
    }

    [TestMethod]
    public async Task CallerCancellationIsNotMapped()
    {
        var (provider, handler, _) = Create();
        handler.Hang();
        using var cancellation = new CancellationTokenSource();
        var request = new AssistChatRequest(Haiku.Id, AssistFeature.Triage);
        var call = provider.CreateClient(Haiku).GetResponseAsync([new ChatMessage(ChatRole.User, "x")], provider.ApplyOptions(Haiku, request, new ChatOptions { MaxOutputTokens = 10 }), cancellation.Token);
        while (handler.Requests.Count == 0)
            await Task.Yield();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => call);
    }

    [TestMethod]
    public async Task ApiKeyComesFromOptionsOrConfigurationAndIsRequired()
    {
        var handler = new FakeAnthropicHandler();
        using var missing = new ClaudeAssistProvider(new ClaudeAssistOptions(), handler, new InstantTimeProvider(TimeSpan.Zero), new DictionaryConfiguration([]));
        var error = await Assert.ThrowsExactlyAsync<AssistProviderException>(() => SendAsync(missing));
        Assert.AreEqual(AssistProviderFailure.NotConfigured, error.Failure);
        Assert.IsFalse(error.Retryable);
        Assert.IsFalse(error.TokensMayHaveBeenConsumed, "nothing was sent");
        Assert.IsEmpty(handler.Requests);

        using var configured = new ClaudeAssistProvider(new ClaudeAssistOptions(), handler, new InstantTimeProvider(TimeSpan.Zero),
            new DictionaryConfiguration(new() { ["TicketFlow:Assist:Claude:ApiKey"] = "sk-from-config" }));
        handler.Respond(200, FakeAnthropicHandler.Message("{}"));
        await SendAsync(configured);
        Assert.AreEqual("Bearer sk-from-config", handler.Requests.Single().Request.Headers.GetValues("Authorization").Single());

        using var environmentStyle = new ClaudeAssistProvider(new ClaudeAssistOptions(), handler, new InstantTimeProvider(TimeSpan.Zero),
            new DictionaryConfiguration(new() { ["ANTHROPIC_API_KEY"] = "sk-from-env" }));
        handler.Respond(200, FakeAnthropicHandler.Message("{}"));
        await SendAsync(environmentStyle);
        Assert.AreEqual("Bearer sk-from-env", handler.Requests.Last().Request.Headers.GetValues("Authorization").Single());
    }

    [TestMethod]
    public void OptionsAreValidated()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new ClaudeAssistProvider(new ClaudeAssistOptions { MaxRetries = 7 }, new FakeAnthropicHandler()));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ClaudeAssistProvider(new ClaudeAssistOptions { AttemptTimeout = TimeSpan.FromMinutes(5), TotalTimeout = TimeSpan.FromMinutes(1) }, new FakeAnthropicHandler()));
        Assert.HasCount(3, ClaudeAssistProvider.DefaultModels);
        Assert.IsTrue(ClaudeAssistProvider.DefaultModels.All(model => model.Provider == ClaudeAssistOptions.ProviderName && model.Capabilities.HasFlag(AssistModelCapabilities.PromptCaching)));
    }
}
