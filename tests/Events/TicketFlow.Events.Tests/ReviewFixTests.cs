using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Surveys;

namespace TicketFlow.Events.Tests;

/// <summary>Regression tests for the E15 review findings.</summary>
[TestClass]
public sealed class ReviewFixTests
{
    private static IEventSubscriptionService Service(IServiceProvider provider) => provider.GetRequiredService<IEventSubscriptionService>();

    private static JsonElement Data(RenderedEvent rendered) => JsonDocument.Parse(rendered.Json).RootElement.GetProperty("data");

    [TestMethod]
    public async Task ManagersCannotDelegateToAMorePrivilegedPrincipal()
    {
        using var fixture = await EventsFixture.CreateAsync();
        // narrow-manager holds events:manage but not conversation:read/contact:read, which the hooks principal holds.
        var refused = (TicketFailure)(await fixture.SubscribeAsync("s1", principal: "hooks", actor: fixture.NarrowManager)).Value!;
        Assert.AreEqual(TicketFailureKind.Forbidden, refused.Kind);
        StringAssert.Contains(refused.Errors.Single(), "delegate");
        EventsFixture.Value(await fixture.SubscribeAsync("s1", principal: "billing-hooks", actor: fixture.Manager));

        // The same rule applies when an update switches the principal.
        var escalate = new EventSubscription(new EventSubscriptionId("s1"), "s1", new EventSinkRef("in-process", "recorder"), EventSubscriptionFilter.Everything,
            new ApiClientId("content-hooks"));
        Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)(await fixture.InScopeAsync(provider => Service(provider).UpdateAsync(fixture.Manager, escalate, 1))).Value!).Kind);
        Assert.IsInstanceOfType<TicketValue<EventSubscriptionView>>((await fixture.InScopeAsync(provider => Service(provider).UpdateAsync(fixture.Owner, escalate, 1))).Value);
    }

    [TestMethod]
    public async Task AuthorizationPrecedesExistenceAndRevisionOutcomes()
    {
        using var fixture = await EventsFixture.CreateAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("s1"));
        var missing = new EventSubscriptionId("missing");
        var existing = new EventSubscriptionId("s1");
        var subscription = new EventSubscription(existing, "s1", new EventSinkRef("in-process", "recorder"), EventSubscriptionFilter.Everything, new ApiClientId("hooks"));
        foreach (var result in new object?[]
                 {
                     (await fixture.InScopeAsync(provider => Service(provider).GetAsync(fixture.Agent, missing))).Value,
                     (await fixture.InScopeAsync(provider => Service(provider).UpdateAsync(fixture.Agent, subscription, 99))).Value,
                     (await fixture.InScopeAsync(provider => Service(provider).DeleteAsync(fixture.Agent, missing, 1))).Value,
                     (await fixture.InScopeAsync(provider => Service(provider).DeleteAsync(fixture.Agent, existing, 99))).Value,
                     (await fixture.InScopeAsync(provider => Service(provider).ReplayAsync(fixture.Agent, missing, 0))).Value,
                     (await fixture.InScopeAsync(provider => Service(provider).ListDeadLettersAsync(fixture.Agent, missing))).Value,
                 })
            Assert.AreEqual(TicketFailureKind.Forbidden, ((TicketFailure)result!).Kind, "Unauthorized actors learn nothing about existence or revisions.");
    }

    [TestMethod]
    public async Task RevokedPrincipalsAreSuspendedAndResumeAfterAnUpdate()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("revoked", principal: "unknown-client"));
        var store = fixture.Get<IEventSubscriptionStore>();
        var id = new EventSubscriptionId("revoked");
        Assert.AreEqual(1, (await fixture.DeliverAsync()).Retried);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, (await fixture.DeliverAsync()).Retried);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.AreEqual(1, (await fixture.DeliverAsync()).Suspended, "The third configuration failure suspends instead of retrying forever.");
        var state = (await store.GetStateAsync(fixture.Scope, id))!;
        Assert.AreEqual((true, "principal-forbidden", 0L), (state.Suspended, state.LastError, state.Cursor));
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(0, (await fixture.DeliverAsync()).Retried + (await fixture.DeliverAsync()).Suspended, "Suspended subscriptions are not claimed.");

        var fixedPrincipal = new EventSubscription(id, "revoked", new EventSinkRef("in-process", "recorder"), EventSubscriptionFilter.Everything, new ApiClientId("hooks"));
        EventsFixture.Value(await fixture.InScopeAsync(provider => Service(provider).UpdateAsync(fixture.Owner, fixedPrincipal, 1)));
        await fixture.DeliverAllAsync();
        Assert.AreEqual(2, fixture.Subscriber.Events.Count());
        Assert.IsFalse((await store.GetStateAsync(fixture.Scope, id))!.Suspended);
    }

    [TestMethod]
    public async Task PoisonEventsAreDeadLetteredAloneAndLaterEventsFlow()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.AssignAsync("c1", "agent");
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("one", batchSize: 1));
        fixture.Subscriber.Fail = true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await fixture.DeliverAsync();
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        fixture.Subscriber.Fail = false;
        await fixture.DeliverAllAsync();
        var letters = await fixture.Get<IEventSubscriptionStore>().ListDeadLettersAsync(fixture.Scope, new EventSubscriptionId("one"), 0, 10);
        Assert.AreEqual(1L, letters.Single().Sequence);
        CollectionAssert.AreEqual(new long[] { 2, 3 }, fixture.Subscriber.Events.Select(item => item.Event.Sequence).ToArray());
    }

    [TestMethod]
    public async Task OversizedBatchesAreHalvedInsteadOfDeadLettered()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.AssignAsync("c1", "agent");
        await fixture.SetTeamAsync("c1", "billing");
        await fixture.ReplyAsync("c1");
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("split", sink: "splitter"));
        await fixture.DeliverAllAsync();
        var splitter = fixture.Provider.GetServices<IEventSink>().OfType<SplittingSink>().Single();
        Assert.AreEqual(5, splitter.Attempts[0]);
        Assert.AreEqual(2, splitter.Attempts[1], "The oversized batch is halved immediately.");
        Assert.IsTrue(fixture.Subscriber.Deliveries.All(delivery => delivery.Events.Count <= 2));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, fixture.Subscriber.Events.Select(item => item.Event.Sequence).ToArray());
        Assert.AreEqual(0, (await fixture.Get<IEventSubscriptionStore>().ListDeadLettersAsync(fixture.Scope, new EventSubscriptionId("split"), 0, 10)).Count);
    }

    [TestMethod]
    public async Task RetentionGapsAreRecorded()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("behind", start: EventStartAtEarliest.Instance));
        fixture.Clock.Advance(TimeSpan.FromDays(31));
        await fixture.InScopeAsync(provider => provider.GetRequiredService<EventRetentionWorker>().RunOnceAsync());
        await fixture.StartAsync("c2");
        await fixture.DispatchAsync();
        await fixture.DeliverAllAsync();
        var state = (await fixture.Get<IEventSubscriptionStore>().GetStateAsync(fixture.Scope, new EventSubscriptionId("behind")))!;
        Assert.AreEqual("events-pruned-1-2", state.LastError);
        CollectionAssert.AreEqual(new long[] { 3, 4 }, fixture.Subscriber.Events.Select(item => item.Event.Sequence).ToArray());
    }

    [TestMethod]
    public async Task CaughtUpSubscriptionsArePushedOutByThePollInterval()
    {
        using var fixture = await EventsFixture.CreateAsync(options => options.IdlePollInterval = TimeSpan.FromSeconds(2));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        EventsFixture.Value(await fixture.SubscribeAsync("idle"));
        Assert.AreEqual(1, (await fixture.DeliverAsync()).Delivered);
        var state = (await fixture.Get<IEventSubscriptionStore>().GetStateAsync(fixture.Scope, new EventSubscriptionId("idle")))!;
        Assert.AreEqual(fixture.Clock.GetUtcNow().AddSeconds(2), state.AvailableAt);
        await fixture.AssignAsync("c1", "agent");
        await fixture.DispatchAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, (await fixture.DeliverAsync()).Delivered);
    }

    [TestMethod]
    public async Task SurveyCommentsHonorSurveyReadAndCommentMasking()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.InScopeAsync(provider => provider.GetRequiredService<EventSurveyFollower>().CatchUpAsync(fixture.Scope));
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        fixture.Surveys.Add(fixture.Scope, "c1", 2, "The agent was rude", fixture.Clock.GetUtcNow().AddSeconds(-10));
        await fixture.InScopeAsync(provider => provider.GetRequiredService<EventSurveyFollower>().CatchUpAsync(fixture.Scope));
        EventsFixture.Value(await fixture.SubscribeAsync("visible", principal: "content-hooks", privacy: EventPrivacy.Content));
        await fixture.DeliverAllAsync();
        Assert.AreEqual("The agent was rude", Data(fixture.Subscriber.Events.Single()).GetProperty("content").GetProperty("comment").GetString());

        await fixture.MaskAsync(new AccessFieldPolicy(SurveyFieldPaths.Comment, [SurveyPermissions.Read]));
        fixture.Subscriber.Deliveries.Clear();
        EventsFixture.Value(await fixture.SubscribeAsync("masked", principal: "content-hooks", privacy: EventPrivacy.Content));
        await fixture.DeliverAllAsync();
        var content = Data(fixture.Subscriber.Events.Single()).GetProperty("content");
        Assert.IsFalse(content.TryGetProperty("comment", out _), "The masked comment is removed.");
        Assert.AreEqual("survey.comment", content.GetProperty("maskedFields")[0].GetString());

        // A principal without survey:read gets no comment at all.
        await fixture.GrantAsync(new ApiClientId("no-surveys"), await fixture.CustomRoleAsync("no-surveys", EventPermissions.Read, EventPermissions.Content, ConversationPermissions.Read));
        fixture.Subscriber.Deliveries.Clear();
        EventsFixture.Value(await fixture.SubscribeAsync("unreadable", principal: "no-surveys", privacy: EventPrivacy.Content));
        await fixture.DeliverAllAsync();
        Assert.IsFalse(fixture.Subscriber.Events.Single().Json.Contains("rude", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task HydrationReadsEachConversationTimelineOncePerBatchAndAttachesTheContact()
    {
        using var fixture = await EventsFixture.CreateAsync();
        await fixture.StartAsync("c1");
        await fixture.ReplyAsync("c1");
        await fixture.IncomingAsync("c1");
        await fixture.ReplyAsync("c1");
        await fixture.DispatchAsync();
        var events = await fixture.EventsAsync();
        var principal = new ApiClientPrincipal(fixture.Scope, new ApiClientId("content-hooks"));
        var contents = await fixture.InScopeAsync(async provider =>
        {
            var counting = CountingProxy<IConversationService>.Wrap(provider.GetRequiredService<IConversationService>(), out var counter);
            var contactCounting = CountingProxy<IContactService>.Wrap(provider.GetRequiredService<IContactService>(), out var contactCounter);
            var hydrator = new EventContentHydrator(counting, contactCounting, provider.GetRequiredService<ISurveyService>());
            var result = await hydrator.HydrateAsync(events, principal);
            Assert.AreEqual(1, counter.Calls(nameof(IConversationService.ReadTimelineAsync)), "One timeline range read for the conversation.");
            Assert.AreEqual(1, contactCounter.Calls(nameof(IContactService.GetAsync)), "One contact read for the batch.");
            return result;
        });
        var messages = events.Where(item => item.Event.Type is IntegrationEventTypes.ConversationMessageReceived or IntegrationEventTypes.ConversationMessageSent).ToList();
        Assert.AreEqual(4, messages.Count);
        Assert.IsTrue(messages.All(item => contents[item.Sequence].Value?.Value is MessageEventContent && contents[item.Sequence].Contact?.DisplayName == "ada"));
        Assert.AreEqual("Still broken", ((MessageEventContent)contents[messages[2].Sequence].Value!.Value.Value!).Body);
    }

    /// <summary>Counts interface calls by method name while delegating to the real implementation.</summary>
    public class CountingProxy<T> : DispatchProxy where T : class
    {
        private readonly Dictionary<string, int> calls = [];
        private T target = null!;

        public static T Wrap(T target, out CountingProxy<T> counter)
        {
            var proxy = Create<T, CountingProxy<T>>();
            counter = (CountingProxy<T>)(object)proxy;
            counter.target = target;
            return proxy;
        }

        public int Calls(string method) => calls.GetValueOrDefault(method);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            calls[targetMethod!.Name] = calls.GetValueOrDefault(targetMethod.Name) + 1;
            return targetMethod.Invoke(target, args);
        }
    }
}
