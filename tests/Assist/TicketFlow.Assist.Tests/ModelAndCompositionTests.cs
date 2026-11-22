using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Assist.Providers.Claude;
using TicketFlow.Assist.StoreContracts;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Assist.Tests;

[TestClass]
public sealed class ModelAndCompositionTests
{
    [TestMethod]
    public void InputsAreBoundedAndValidated()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AssistSummaryRequest(default));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistDraftRequest(AssistFixture.C1, instructions: new string('x', 2001)));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistDraftRequest(AssistFixture.C1, instructions: "bad \u0001 control"));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistDraftRequest(AssistFixture.C1, instructions: "lone \ud800 surrogate"));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistTranslateRequest(AssistFixture.C1, new HelpCenterLocale("de")));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistTranslateRequest(AssistFixture.C1, new HelpCenterLocale("de"), "x", new MessageId("m")));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistSentimentRequest(AssistFixture.C1, ["Not A Token"]));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistTriageRequest(AssistFixture.C1, [new AssistLabelOption(new LabelId("a")), new AssistLabelOption(new LabelId("a"))]));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistTriageRequest(AssistFixture.C1, suggestPriority: false));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AssistSettings(true, 0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AssistSettings(true, 1, 10_001));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistSettings(true, 1, 1, new Dictionary<AssistFeature, string> { [AssistFeature.Triage] = "Bad Model" }));
        Assert.ThrowsExactly<ArgumentException>(() => new AssistModelDefinition("x", "p", "", AssistModelCapabilities.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AssistUsage(-1, 0));
        Assert.AreEqual("AssistDraft { Citations = 0, Grounded = False }", new AssistDraft("secret body", [], false).ToString());
        Assert.DoesNotContain("secret", new AssistSummary("secret", [], null).ToString());
    }

    [TestMethod]
    public void UsageMapsMicrosoftExtensionsAiCounts()
    {
        Assert.AreEqual(AssistUsage.Zero, AssistUsage.From(null));
        var usage = AssistUsage.From(new UsageDetails { InputTokenCount = 15, OutputTokenCount = 5, CachedInputTokenCount = 3, AdditionalCounts = new() { ["CacheCreationInputTokens"] = 2 } });
        Assert.AreEqual(new AssistUsage(10, 5, 3, 2), usage);
        Assert.AreEqual(20, usage.TotalTokens);
    }

    [TestMethod]
    public void CodecsRoundTripAndRejectUnknownShapes()
    {
        var settings = new AssistSettings(false, 12345, 7, new Dictionary<AssistFeature, string> { [AssistFeature.ReplyDraft] = "m-1", [AssistFeature.Triage] = "m-2" });
        var json = AssistJsonCodec.EncodeSettings(settings);
        Assert.AreEqual("""{"v":1,"enabled":false,"daily":12345,"rpm":7,"models":{"2":"m-1","3":"m-2"}}""", json);
        Assert.AreEqual(settings, AssistJsonCodec.DecodeSettings(json));
        Assert.ThrowsExactly<FormatException>(() => AssistJsonCodec.DecodeSettings("""{"v":2,"enabled":false,"daily":1,"rpm":1,"models":{}}"""));
        Assert.ThrowsExactly<FormatException>(() => AssistJsonCodec.DecodeSettings("""{"v":1,"enabled":false,"daily":1,"rpm":1,"models":{},"x":1}"""));
        Assert.ThrowsExactly<FormatException>(() => AssistJsonCodec.DecodeSettings("""{"v":1,"enabled":false,"daily":1,"rpm":1,"models":{"99":"m"}}"""));

        var payload = new AssistTimelinePayload(AssistFeature.ReplyDraft, "claude-sonnet-5-5", Guid.Parse("01890000-0000-7000-8000-000000000001"), new AssistUsage(1, 2, 3, 4), [new ArticleId("a1")]);
        var encoded = AssistJsonCodec.EncodeTimelinePayload(payload);
        Assert.AreEqual("""{"feature":2,"model":"claude-sonnet-5-5","usage_id":"01890000-0000-7000-8000-000000000001","usage":{"in":1,"out":2,"cache_read":3,"cache_write":4},"citations":["a1"]}""", encoded);
        Assert.AreEqual(payload, AssistJsonCodec.DecodeTimelinePayload(1, encoded));
        Assert.ThrowsExactly<FormatException>(() => AssistJsonCodec.DecodeTimelinePayload(2, encoded));
        Assert.AreEqual(AssistTimelineKinds.Triage, AssistTimelineKinds.For(AssistFeature.Triage));
        Assert.IsNull(AssistTimelineKinds.For(AssistFeature.ToneRewrite));
    }

    [TestMethod]
    public async Task InMemoryStoreSatisfiesTheContract()
    {
        var store = new InMemoryAssistStore();
        await AssistStoreContract.VerifySettingsAsync(store, store);
        await AssistStoreContract.VerifyReservationsAsync(store, store);
        await AssistStoreContract.VerifyConcurrentReservationsAsync(store, store);
        await AssistStoreContract.VerifySummaryAndPurgeAsync(store, store);
        await AssistStoreContract.VerifyEstimatedSettlementAsync(store, store);
        await AssistStoreContract.VerifySweepAsync(store, store);
        await AssistStoreContract.VerifyConcurrentReserveAndSettleAsync(store, store);
    }

    [TestMethod]
    public async Task CompositionRegistersPermissionsModelsAndClaudeDefaults()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddAssist().AddAssist().AddClaudeAssist(options => options.ApiKey = "unused").AddClaudeAssist().AddInMemoryAssistStores();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var catalog = provider.GetRequiredService<IPermissionCatalog>();
        Assert.IsTrue(catalog.Contains(AssistPermissions.Use));
        Assert.IsTrue(catalog.Find(AssistPermissions.Manage)!.IsSensitive);
        var registry = provider.GetRequiredService<IAssistModelRegistry>();
        Assert.AreEqual(ClaudeAssistOptions.HaikuModel, registry.DefaultFor(AssistFeature.Triage));
        Assert.AreEqual(ClaudeAssistOptions.HaikuModel, registry.DefaultFor(AssistFeature.SentimentAndIntent));
        Assert.AreEqual(ClaudeAssistOptions.SonnetModel, registry.DefaultFor(AssistFeature.ReplyDraft));
        Assert.AreEqual(ClaudeAssistOptions.SonnetModel, registry.DefaultFor(AssistFeature.ConversationSummary));
        Assert.HasCount(3, registry.All);
        Assert.HasCount(1, provider.GetServices<IAssistChatProvider>());
        Assert.IsInstanceOfType<InMemoryAssistStore>(provider.GetRequiredService<IAssistStore>());
    }

    [TestMethod]
    public void FactoryWrapsProviderClientsAndRejectsUnknownModels()
    {
        var model = new ScriptedChatClient();
        var scripted = new ScriptedChatProvider(model);
        var options = new AssistOptions();
        options.Models.Add(new AssistModelDefinition("m", ScriptedChatProvider.ProviderName, "m-v2", AssistModelCapabilities.Tools, AssistThinkingLevel.High));
        options.Models.Add(new AssistModelDefinition("orphan", "nobody", "x", AssistModelCapabilities.None));
        options.FeatureModels[AssistFeature.Triage] = "m";
        var registry = new AssistModelRegistry(Options.Create(options));
        using var factory = new AssistChatClientFactory(registry, [scripted]);

        Assert.ThrowsExactly<ArgumentException>(() => factory.GetChatClient(new AssistChatRequest("missing", AssistFeature.Triage)));
        Assert.ThrowsExactly<InvalidOperationException>(() => factory.GetChatClient(new AssistChatRequest("orphan", AssistFeature.Triage)));
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.DefaultFor(AssistFeature.Translate));

        model.Reply("{}");
        var client = factory.GetChatClient(new AssistChatRequest("m", AssistFeature.Triage));
        _ = client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]).GetAwaiter().GetResult();
        var sent = model.Requests.Single().Options!;
        Assert.AreEqual("m-v2", sent.ModelId);
        Assert.AreEqual(ReasoningEffort.High, sent.Reasoning!.Effort, "model default thinking level");

        var badOptions = new AssistOptions();
        badOptions.FeatureModels[AssistFeature.Triage] = "nope";
        Assert.ThrowsExactly<InvalidOperationException>(() => new AssistModelRegistry(Options.Create(badOptions)));
    }

    [TestMethod]
    public void PipelinesAreCachedPerModelFeatureAndThinkingAndDisposedWithTheContainer()
    {
        var model = new CountingChatClient();
        var services = new ServiceCollection();
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddAssist(options =>
        {
            options.Models.Add(new AssistModelDefinition("m", ScriptedChatProvider.ProviderName, "m-v1", AssistModelCapabilities.None));
            options.Models.Add(new AssistModelDefinition("n", ScriptedChatProvider.ProviderName, "n-v1", AssistModelCapabilities.None));
        }).AddInMemoryAssistStores();
        services.AddSingleton<IAssistChatProvider>(new ScriptedChatProvider(model));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var factory = provider.GetRequiredService<IAssistChatClientFactory>();

        var first = factory.GetChatClient(new AssistChatRequest("m", AssistFeature.Triage, AssistThinkingLevel.Low));
        Assert.AreSame(first, factory.GetChatClient(new AssistChatRequest("m", AssistFeature.Triage, AssistThinkingLevel.Low)));
        var other = factory.GetChatClient(new AssistChatRequest("m", AssistFeature.ReplyDraft));
        Assert.AreNotSame(first, other);
        Assert.AreNotSame(first, factory.GetChatClient(new AssistChatRequest("m", AssistFeature.Triage, AssistThinkingLevel.High)));
        _ = factory.GetChatClient(new AssistChatRequest("n", AssistFeature.Triage));
        Assert.AreEqual(2, model.Created, "one provider client per provider model");
        Assert.AreEqual(0, model.Disposed);

        provider.Dispose();
        Assert.AreEqual(2, model.Disposed, "each provider-created client is disposed once, with the container");
        Assert.ThrowsExactly<ObjectDisposedException>(() => factory.GetChatClient(new AssistChatRequest("m", AssistFeature.Triage)));
    }

    [TestMethod]
    public async Task HostsCanSupplyKeyedClientsAndPipelineMiddleware()
    {
        var hosted = new ScriptedChatClient();
        var providerClient = new CountingChatClient();
        var seen = new List<string?>();
        var services = new ServiceCollection();
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores().AddAssist(options =>
        {
            options.Models.Add(new AssistModelDefinition("m", ScriptedChatProvider.ProviderName, "m-v1", AssistModelCapabilities.None));
            options.Pipeline.Add((builder, model, request) => builder.Use((messages, chatOptions, inner, cancellationToken) =>
            {
                seen.Add(chatOptions?.ModelId + "|" + request.Feature);
                return inner.GetResponseAsync(messages, chatOptions, cancellationToken);
            }, null));
        }).AddInMemoryAssistStores();
        services.AddSingleton<IAssistChatProvider>(new ScriptedChatProvider(providerClient));
        services.AddKeyedSingleton<IChatClient>(AssistChatClients.ServiceKey(ScriptedChatProvider.ProviderName), hosted);
        await using (var provider = services.BuildServiceProvider())
        {
            hosted.Reply("{}");
            var client = provider.GetRequiredService<IAssistChatClientFactory>().GetChatClient(new AssistChatRequest("m", AssistFeature.Translate));
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
            Assert.HasCount(1, hosted.Requests);
            Assert.AreEqual("m-v1", hosted.Requests.Single().Options!.ModelId);
            CollectionAssert.AreEqual(new[] { "m-v1|Translate" }, seen);
            Assert.AreEqual(0, providerClient.Created, "the keyed host client replaces the provider client");
        }
        Assert.AreEqual(0, hosted.Disposals, "the host owns its keyed client");
    }

    [TestMethod]
    public async Task ToolInvocationIsTracedWithoutArgumentsOrResults()
    {
        var model = new ScriptedChatClient();
        var options = new AssistOptions();
        options.Models.Add(new AssistModelDefinition("m", ScriptedChatProvider.ProviderName, "m", AssistModelCapabilities.Tools));
        using var factory = new AssistChatClientFactory(new AssistModelRegistry(Options.Create(options)), [new ScriptedChatProvider(model)]);
        var invoked = 0;
        var tool = AIFunctionFactory.Create((string query) => { invoked++; return "result for " + query; }, "lookup");
        model.Enqueue(new ScriptedTurn(Calls: [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["query"] = "secret query" })]));
        model.Reply("done");

        var activities = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == AssistTelemetry.ToolActivitySourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var response = await factory.GetChatClient(new AssistChatRequest("m", AssistFeature.ReplyDraft)).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { Tools = [tool] });

        Assert.AreEqual("done", response.Text);
        Assert.AreEqual(1, invoked);
        var span = activities.Single();
        Assert.AreEqual("lookup", span.GetTagItem("ai.tool.name"));
        Assert.AreEqual(true, span.GetTagItem("ai.tool.success"));
        Assert.IsFalse(span.Tags.Any(tag => tag.Value?.Contains("secret query", StringComparison.Ordinal) == true));
    }
}
