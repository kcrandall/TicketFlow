using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Channels.Api;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation.Tests;

[TestClass]
public sealed class MacroAndCannedResponseTests
{
    private static readonly ConversationId C1 = new("c1");

    private static CannedResponse Thanks() => new(new CannedResponseId("thanks"), "thanks", "Thanks", "en",
    [
        new CannedResponseVariant("en", new AutomationTemplate("Thanks {{contact.name|there}} ({{conversation.attributes.plan|no plan}}, {{conversation.attributes.ssn|hidden}}) — {{agent.name|Support}}")),
        new CannedResponseVariant("de", new AutomationTemplate("Danke {{contact.name}}, Plan {{conversation.attributes.plan|-}}, {{conversation.attributes.ssn|verborgen}}")),
    ]);

    private static async Task SeedAsync(AutomationFixture fixture)
    {
        await fixture.InScopeAsync(services => services.GetRequiredService<ICannedResponseService>().CreateAsync(fixture.Owner, Thanks()));
        await fixture.StartAsync("c1", attributes: new Dictionary<string, ConversationAttributeValue>
        {
            ["plan"] = new ConversationTextValue("gold"),
            ["ssn"] = new ConversationTextValue("123-45-6789"),
        });
        await fixture.DispatchAsync();
    }

    private static Task<T> MacrosAsync<T>(AutomationFixture fixture, Func<IMacroService, Task<T>> action) =>
        fixture.InScopeAsync(services => action(services.GetRequiredService<IMacroService>()));

    private static Task<T> CannedAsync<T>(AutomationFixture fixture, Func<ICannedResponseService, Task<T>> action) =>
        fixture.InScopeAsync(services => action(services.GetRequiredService<ICannedResponseService>()));

    [TestMethod]
    public async Task MacrosRunWithTheAgentsOwnPermissions()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await SeedAsync(fixture);
        var macro = new Macro(new MacroId("close"), "Close politely", MacroVisibility.Shared, null,
            [new SendReplyAction(new CannedResponseId("thanks")), new AddLabelsAction(new LabelId("done")), new SetStatusAction(ConversationStatusKind.Resolved)]);
        Assert.IsInstanceOfType<AutomationRefused>((await MacrosAsync(fixture, service => service.CreateAsync(fixture.Agent, macro))).Value, "Shared macros need macro:manage.");
        Assert.IsInstanceOfType<AutomationValue<StoredMacro>>((await MacrosAsync(fixture, service => service.CreateAsync(fixture.Owner, macro))).Value);

        // A light agent may run macros but cannot reply: the run stops at the first refused action and changes nothing.
        var light = Assert.IsInstanceOfType<AutomationValue<MacroRunResult>>((await MacrosAsync(fixture, service => service.RunAsync(fixture.Light, macro.Id, C1))).Value).Value;
        Assert.IsFalse(light.Completed);
        Assert.AreEqual(AutomationActionRefusal.Forbidden, ((AutomationActionRefused)light.Outcomes.Single().Value!).Reason);
        Assert.AreEqual(ConversationStatusKind.Open, (await fixture.ConversationAsync("c1")).Status.Kind);

        var run = Assert.IsInstanceOfType<AutomationValue<MacroRunResult>>((await MacrosAsync(fixture, service => service.RunAsync(fixture.Agent, macro.Id, C1))).Value).Value;
        Assert.IsTrue(run.Completed);
        Assert.AreEqual(3, run.Outcomes.Count);
        var conversation = await fixture.ConversationAsync("c1");
        Assert.AreEqual(ConversationStatusKind.Resolved, conversation.Status.Kind);
        var timeline = await fixture.TimelineAsync("c1");
        var reply = timeline.Select(item => item.Event.Value).OfType<MessageEvent>().Single(message => message.Direction == MessageDirection.Outgoing);
        Assert.AreEqual("Danke Ada, Plan gold, verborgen", reply.Body, "Contact locale, masked SSN never substituted.");
        Assert.IsTrue(timeline.Where(item => item.Event.Value is MessageEvent { Direction: MessageDirection.Outgoing } or StatusChanged).All(item => item.Actor.Id == "agent"));
        Assert.IsTrue(timeline.Any(item => item.Event.Value is ExtensionEvent { Kind: AutomationEventCodec.MacroAppliedKind }));

        Assert.IsInstanceOfType<AutomationRefused>((await MacrosAsync(fixture, service => service.RunAsync(fixture.Viewer, macro.Id, C1))).Value, "Viewers lack macro:run.");
        Assert.IsInstanceOfType<AutomationNotFound>((await MacrosAsync(fixture, service => service.RunAsync(fixture.Agent, macro.Id, new ConversationId("missing")))).Value);
    }

    [TestMethod]
    public async Task PersonalMacrosAreVisibleOnlyToTheirOwner()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await SeedAsync(fixture);
        var mine = new Macro(new MacroId("mine"), "Mine", MacroVisibility.Personal, new UserId("agent"), [new AddLabelsAction(new LabelId("mine"))]);
        Assert.IsInstanceOfType<AutomationRefused>((await MacrosAsync(fixture, service => service.CreateAsync(fixture.Light,
            new Macro(new MacroId("theirs"), "Theirs", MacroVisibility.Personal, new UserId("agent"), [new AddLabelsAction(new LabelId("x"))])))).Value, "Only the owner creates a personal macro.");
        var created = Assert.IsInstanceOfType<AutomationValue<StoredMacro>>((await MacrosAsync(fixture, service => service.CreateAsync(fixture.Agent, mine))).Value).Value;
        await MacrosAsync(fixture, service => service.CreateAsync(fixture.Owner, new Macro(new MacroId("shared"), "Shared", MacroVisibility.Shared, null, [new AddLabelsAction(new LabelId("s"))])));

        var agentList = Assert.IsInstanceOfType<AutomationValue<IReadOnlyList<StoredMacro>>>((await MacrosAsync(fixture, service => service.ListAsync(fixture.Agent))).Value).Value;
        CollectionAssert.AreEqual(new[] { "mine", "shared" }, agentList.Select(item => item.Id.Value).ToArray());
        var lightList = Assert.IsInstanceOfType<AutomationValue<IReadOnlyList<StoredMacro>>>((await MacrosAsync(fixture, service => service.ListAsync(fixture.Light))).Value).Value;
        CollectionAssert.AreEqual(new[] { "shared" }, lightList.Select(item => item.Id.Value).ToArray());
        Assert.IsInstanceOfType<AutomationNotFound>((await MacrosAsync(fixture, service => service.GetAsync(fixture.Light, mine.Id))).Value);
        Assert.IsInstanceOfType<AutomationNotFound>((await MacrosAsync(fixture, service => service.RunAsync(fixture.Light, mine.Id, C1))).Value);
        Assert.IsInstanceOfType<AutomationNotFound>((await MacrosAsync(fixture, service => service.DeleteAsync(fixture.Light, mine.Id, 1))).Value);
        Assert.IsInstanceOfType<AutomationConflict>((await MacrosAsync(fixture, service => service.DeleteAsync(fixture.Agent, mine.Id, 5))).Value);
        Assert.IsInstanceOfType<AutomationValue<StoredMacro>>((await MacrosAsync(fixture, service => service.DeleteAsync(fixture.Agent, mine.Id, created.Revision))).Value);
    }

    [TestMethod]
    public async Task CannedResponsesRenderWithoutMaskedValues()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await SeedAsync(fixture);
        var rendered = Assert.IsInstanceOfType<AutomationValue<RenderedCannedResponse>>((await CannedAsync(fixture, service =>
            service.RenderAsync(fixture.Agent, new CannedResponseId("thanks"), C1, "en-US"))).Value).Value;
        Assert.AreEqual("en", rendered.Locale);
        Assert.AreEqual("Thanks Ada (gold, hidden) — Support", rendered.Body);
        Assert.IsFalse(rendered.Body.Contains("123-45", StringComparison.Ordinal));
        var defaultLocale = Assert.IsInstanceOfType<AutomationValue<RenderedCannedResponse>>((await CannedAsync(fixture, service =>
            service.RenderAsync(fixture.Agent, new CannedResponseId("thanks"), C1))).Value).Value;
        Assert.AreEqual("de", defaultLocale.Locale, "The contact's locale is used when none is requested.");

        Assert.IsInstanceOfType<AutomationRefused>((await CannedAsync(fixture, service => service.RenderAsync(fixture.Light, new CannedResponseId("thanks"), C1))).Value,
            "Rendering needs conversation:reply.");
        Assert.IsInstanceOfType<AutomationValue<CannedResponsePage>>((await CannedAsync(fixture, service => service.ListAsync(fixture.Agent, new CannedResponseQuery()))).Value);
        Assert.IsInstanceOfType<AutomationValue<StoredCannedResponse>>((await CannedAsync(fixture, service => service.FindByShortcutAsync(fixture.Agent, "thanks"))).Value);
        Assert.IsInstanceOfType<AutomationRefused>((await CannedAsync(fixture, service => service.ListAsync(fixture.Viewer, new CannedResponseQuery()))).Value);
        Assert.IsInstanceOfType<AutomationRefused>((await CannedAsync(fixture, service => service.CreateAsync(fixture.Agent, AutomationStoreContractsShim.Response("x", "x")))).Value);
        var duplicate = await CannedAsync(fixture, service => service.CreateAsync(fixture.Owner, AutomationStoreContractsShim.Response("other", "thanks")));
        Assert.AreEqual(AutomationRefusal.Rejected, ((AutomationRefused)duplicate.Value!).Reason);
    }

    [TestMethod]
    public async Task WebhookActionsAreSignedAndCarryIdentifiersOnly()
    {
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var capture = new CapturingHandler();
        using var fixture = await AutomationFixture.CreateAsync(configure: services => services.AddSingleton<IAutomationWebhookSender>(provider =>
            new SignedAutomationWebhookSender(provider.GetRequiredService<IOptions<AutomationOptions>>(), provider.GetRequiredService<TimeProvider>(),
                new StaticAutomationWebhookDestinationResolver(new Dictionary<WebhookDestinationId, (Uri, byte[], string)>
                {
                    [new WebhookDestinationId("crm")] = (new Uri("https://hooks.example.com/tf"), key, "k1"),
                    [new WebhookDestinationId("internal")] = (new Uri("https://127.0.0.1/hook"), key, "k1"),
                }), capture)));
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("notify"), "Notify", MessageReceivedTrigger.Instance, null,
            [new SendWebhookAction(new WebhookDestinationId("crm")), new SendWebhookAction(new WebhookDestinationId("internal")), new SendWebhookAction(new WebhookDestinationId("unknown"))]));
        await fixture.StartAsync("c1", "my card number is 4111 1111 1111 1111");
        await fixture.DispatchAsync();

        var request = capture.Requests.Single();
        var body = request.Body;
        var text = Encoding.UTF8.GetString(body);
        StringAssert.Contains(text, "\"conversation\":\"c1\"");
        Assert.IsFalse(text.Contains("4111", StringComparison.Ordinal), "Webhook payloads never carry message bodies.");
        Assert.IsFalse(text.Contains("Ada", StringComparison.Ordinal));
        var verification = ApiWebhookSignature.Verify(key, request.Headers[ApiWebhookHeaders.Id], request.Headers[ApiWebhookHeaders.Timestamp],
            request.Headers[ApiWebhookHeaders.Signature], body, fixture.Clock.GetUtcNow());
        Assert.IsInstanceOfType<ApiWebhookVerified>(verification.Value);
        var outcomes = (await fixture.ExecutionsAsync("c1")).Single().Execution.Outcomes;
        Assert.IsInstanceOfType<AutomationActionApplied>(outcomes[0].Value);
        Assert.AreEqual("endpoint-blocked", ((AutomationActionFailed)outcomes[1].Value!).Code, "SSRF guard blocks private addresses.");
        Assert.AreEqual("destination-missing", ((AutomationActionFailed)outcomes[2].Value!).Code);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(byte[] Body, Dictionary<string, string> Headers)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(header => header.Key, header => header.Value.Single());
            Requests.Add((await request.Content!.ReadAsByteArrayAsync(cancellationToken), headers));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}

internal static class AutomationStoreContractsShim
{
    public static CannedResponse Response(string id, string shortcut) => StoreContracts.AutomationStoreContract.Response(id, shortcut);
}
