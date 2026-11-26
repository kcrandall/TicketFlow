using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;
using static TicketFlow.Channels.Tests.ChannelTestSupport;

namespace TicketFlow.Channels.Tests;

[TestClass]
public sealed class ReviewRegressionTests
{
    [TestMethod]
    public async Task AddressesNestedUnderOrEnclosingAnotherScopesAccountAreRefused()
    {
        await using var host = await HostAsync(EmailChannel());
        var kind = new ChannelKind("email");
        var store = host.Services.GetRequiredService<IChannelEndpointStore>();
        var victim = TestScopes.Create(workspaceId: "victim");
        Assert.IsInstanceOfType<StoreApplied<StoredChannelEndpoint>>((await store.CreateAsync(victim,
            new ChannelEndpoint(kind, "cfg-victim", new InboxId("victim-inbox"), "mailgun:acct"))).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredChannelEndpoint>>((await store.CreateAsync(victim,
            new ChannelEndpoint(kind, "cfg-victim-2", new InboxId("victim-inbox"), "twilio:AC9:+1555"))).Value);
        var inboxes = host.Services.GetRequiredService<IInboxStore>();
        await inboxes.CreateAsync(host.Scope, new Inbox(new InboxId("email-2"), "Email 2", new ChannelBinding(kind, "cfg-email-2")));

        // Shadowing: a narrow address under another scope's account.
        Assert.AreEqual("address-not-owned", ChannelHost.Is<TicketFailure>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email-2", new InboxId("email-2"), "mailgun:acct:support@acme.example"))).Value).Code);
        // Enclosing: an account-level address over another scope's narrow address.
        Assert.AreEqual("address-not-owned", ChannelHost.Is<TicketFailure>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email-2", new InboxId("email-2"), "twilio:AC9"))).Value).Code);
        // Moving an existing endpoint onto a shadowing address is refused too.
        var existing = ChannelHost.Is<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.GetAsync(host.Owner, kind, "cfg-email")).Value).Value;
        Assert.AreEqual("address-not-owned", ChannelHost.Is<TicketFailure>((await host.Endpoints.UpdateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email", existing.Endpoint.Inbox, "mailgun:acct:x"), existing.Revision)).Value).Code);

        // Own nesting and unrelated addresses are fine.
        Assert.IsInstanceOfType<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email-2", new InboxId("email-2"), "email:cfg-email:narrow"))).Value);
        Assert.IsInstanceOfType<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.UpdateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email", existing.Endpoint.Inbox, "mailgun:acctx"), existing.Revision)).Value);
    }

    [TestMethod]
    public async Task SharedThreadsKeepOneConversationPerSpeaker()
    {
        var slack = new FakeChannel(new ChannelKind("slack"), new ChannelCapabilities(ChannelFeatures.Threading), ContactIdentityKind.ExternalUser);
        await using var host = await HostAsync(slack);
        var kind = new ChannelKind("slack");
        await host.Services.GetRequiredService<IInboxStore>().CreateAsync(host.Scope, new Inbox(new InboxId("slack-inbox"), "Slack", new ChannelBinding(kind, "cfg-slack")));
        Assert.IsInstanceOfType<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.CreateAsync(host.Owner, new ChannelEndpoint(kind, "cfg-slack", new InboxId("slack-inbox")))).Value);
        InboundChannelMessage Message(string id, string user) => new(host.Scope, kind, "cfg-slack", id,
            new ChannelSenderEvidence(ContactIdentityKind.ExternalUser, user, verified: true, "slack"), host.Clock.GetUtcNow(), $"from {user}", externalThreadId: "C1:100.000100");

        var conversations = new List<InboundRecorded>();
        foreach (var (id, user) in new[] { ("m1", "U-A"), ("m2", "U-B"), ("m3", "U-A"), ("m4", "U-B"), ("m5", "U-A") })
            conversations.Add(Recorded(await host.Pipeline.ReceiveAsync(host.Worker, Message(id, user))));
        Assert.AreEqual(2, conversations.Select(item => item.Conversation).Distinct().Count());
        Assert.AreEqual(conversations[0].Conversation, conversations[2].Conversation);
        Assert.AreEqual(conversations[0].Conversation, conversations[4].Conversation);
        Assert.AreEqual(conversations[1].Conversation, conversations[3].Conversation);
        Assert.AreEqual(3, (await MessagesAsync(host, conversations[0].Conversation)).Count);
        Assert.AreEqual(2, (await MessagesAsync(host, conversations[1].Conversation)).Count);
    }

    [TestMethod]
    public async Task ProviderRetryAfterDelaysTheNextAttempt()
    {
        var email = EmailChannel().Then(new ChannelSendRejected("rate-limited", retryable: true, TimeSpan.FromMinutes(2)));
        await using var host = await HostAsync(email);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation)));
        Assert.AreEqual(1, (await host.DispatchAsync()).Retried);
        var scheduled = (await host.Dispatches.GetAsync(host.Scope, inbound.Conversation, messageId))!.Dispatch;
        Assert.AreEqual(host.Clock.GetUtcNow().AddMinutes(2), scheduled.NextAttemptAt);

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await host.DispatchAsync();
        host.Clock.Advance(TimeSpan.FromSeconds(60));
        await host.DispatchAsync();
        Assert.AreEqual(1, email.Requests.Count, "The outbox backoff alone (seconds) must not override the provider's 2-minute Retry-After.");
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        await host.DispatchAsync();
        Assert.AreEqual(2, email.Requests.Count);
        Assert.AreEqual(MessageDeliveryState.Sent, (await MessageAsync(host, inbound.Conversation, messageId)).Delivery.State);
    }

    [TestMethod]
    public async Task RedeliveredEntriesDoNotSendBeforeTheScheduledAttempt()
    {
        var email = EmailChannel();
        await using var host = await HostAsync(email);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        await host.DispatchAsync();
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation)));
        Assert.IsInstanceOfType<StoreApplied<StoredChannelDispatch>>((await host.Dispatches.WriteAsync(host.Scope, new ChannelDispatch(inbound.Conversation, messageId,
            new InboxId("email-inbox"), new ChannelKind("email"), ChannelDispatchState.RetryScheduled, 1, host.Clock.GetUtcNow(), host.Clock.GetUtcNow().AddMinutes(2), code: "rate-limited"), null)).Value);
        Assert.AreEqual(1, (await host.Outbox.RunOnceAsync()).Retried);
        Assert.AreEqual(0, email.Requests.Count);
        host.Clock.Advance(TimeSpan.FromSeconds(10));
        await host.DispatchAsync();
        Assert.AreEqual(0, email.Requests.Count, "The release honoured the remaining two minutes.");
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        await host.DispatchAsync();
        Assert.AreEqual(1, email.Requests.Count);
    }

    [TestMethod]
    public async Task FutureReceiveTimesAreClamped()
    {
        await using var host = await HostAsync(WhatsAppChannel());
        var now = host.Clock.GetUtcNow();
        var recorded = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.WhatsApp("wamid.1", receivedAt: now.AddDays(30))));
        Assert.AreEqual(now + InboundChannelPipeline.MaximumFutureSkew, (await host.States.GetAsync(host.Scope, recorded.Conversation))!.State.LastIncomingAt);
        host.Clock.Advance(TimeSpan.FromHours(25));
        Assert.IsInstanceOfType<ReplyWindowClosed>((await host.Replies.GetReplyWindowAsync(host.Agent, recorded.Conversation)).Value,
            "A forged future timestamp cannot hold the reply window open.");
    }

    [TestMethod]
    public async Task DeliveryConflictsAreRetryable()
    {
        await using var host = await HostAsync(EmailChannel());
        var endpoints = host.Services.GetRequiredService<IChannelEndpointStore>();
        foreach (ConversationCommandResult outcome in new ConversationCommandResult[] { new ConversationConflict(3), new ConversationRefused(ConversationRefusal.Rejected, ["busy"]) })
        {
            var ingestor = new ChannelDeliveryIngestor(endpoints, StubConversations.Returning(outcome));
            var result = await ingestor.ReportAsync(host.Worker, new ChannelDeliveryReport(host.Scope, new ChannelKind("email"), "cfg-email", "p-1", ChannelDeliveryState.Delivered,
                host.Clock.GetUtcNow()));
            Assert.AreEqual(DeliveryReportRejection.Conflict, ChannelHost.Is<DeliveryReportRejected>(result.Value).Reason);
        }
    }

    [TestMethod]
    public void WorkerPrincipalResolutionLivesInTheAbstractions()
    {
        Assert.AreEqual("TicketFlow.Channels.Abstractions", typeof(IChannelWorkerPrincipalResolver).Assembly.GetName().Name);
        Assert.AreEqual("TicketFlow.Channels.Abstractions", typeof(IChannelAddressOwnershipValidator).Assembly.GetName().Name);
    }

    /// <summary>An <see cref="IConversationService"/> whose delivery updates return a fixed outcome.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    internal class StubConversations : DispatchProxy
    {
        private ConversationCommandResult outcome;

        public static IConversationService Returning(ConversationCommandResult outcome)
        {
            var proxy = Create<IConversationService, StubConversations>();
            ((StubConversations)(object)proxy).outcome = outcome;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => string.Equals(targetMethod?.Name, nameof(IConversationService.UpdateDeliveryStatusAsync), StringComparison.Ordinal) ? Task.FromResult(outcome) : throw new NotSupportedException();
    }
}
