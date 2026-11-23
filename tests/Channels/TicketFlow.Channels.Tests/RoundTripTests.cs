using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;
using static TicketFlow.Channels.Tests.ChannelTestSupport;

namespace TicketFlow.Channels.Tests;

[TestClass]
public sealed class RoundTripTests
{
    [TestMethod]
    public async Task InboundReplyDispatchAndDeliveryStatusRoundTrip()
    {
        var email = EmailChannel().Then(new ChannelSendAccepted("provider-1", "<out-1@ticketflow.example>"));
        await using var host = await HostAsync(email);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>", references: ["<earlier@x>"], subject: "Order 42")));
        var reply = Applied(await ReplyAsync(host, inbound.Conversation, "We are on it"));
        var messageId = OutgoingId(reply);
        Assert.AreEqual(MessageDeliveryState.Pending, (await MessageAsync(host, inbound.Conversation, messageId)).Delivery.State);

        var run = await host.DispatchAsync();
        Assert.AreEqual(0, run.Retried + run.DeadLettered);
        var request = email.Requests.Single();
        Assert.AreEqual(new ChannelRecipient(ContactIdentityKind.Email, "ada@example.com"), request.Recipient);
        Assert.AreEqual("We are on it", request.Body);
        Assert.AreEqual(messageId, request.Message);
        Assert.AreEqual("root@x", request.Thread.InReplyTo);
        CollectionAssert.AreEqual(new[] { "root@x", "earlier@x" }, request.Thread.References.ToArray());
        Assert.AreEqual("Order 42", request.Thread.Subject);
        Assert.IsTrue(request.IdempotencyKey.StartsWith("tf1-", StringComparison.Ordinal));

        var sent = await MessageAsync(host, inbound.Conversation, messageId);
        Assert.AreEqual(MessageDeliveryState.Sent, sent.Delivery.State);
        Assert.AreEqual("provider-1", sent.ExternalId);
        var dispatch = (await host.Dispatches.GetAsync(host.Scope, inbound.Conversation, messageId))!.Dispatch;
        Assert.AreEqual(ChannelDispatchState.Accepted, dispatch.State);
        Assert.AreEqual(1, dispatch.Attempts);

        // The customer's answer cites our outgoing Message-Id and threads into the same conversation.
        var answer = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<answer@x>", inReplyTo: "<out-1@ticketflow.example>")));
        Assert.AreEqual(inbound.Conversation, answer.Conversation);

        var delivered = await host.Deliveries.ReportAsync(host.Worker, new ChannelDeliveryReport(host.Scope, new ChannelKind("email"), "cfg-email", "provider-1",
            ChannelDeliveryState.Delivered, host.Clock.GetUtcNow()));
        Assert.IsFalse(ChannelHost.Is<DeliveryReportApplied>(delivered.Value).Replayed);
        Assert.AreEqual(MessageDeliveryState.Delivered, (await MessageAsync(host, inbound.Conversation, messageId)).Delivery.State);
        var stale = await host.Deliveries.ReportAsync(host.Worker, new ChannelDeliveryReport(host.Scope, new ChannelKind("email"), "cfg-email", "provider-1",
            ChannelDeliveryState.Sent, host.Clock.GetUtcNow()));
        Assert.IsTrue(ChannelHost.Is<DeliveryReportApplied>(stale.Value).Replayed);
        Assert.AreEqual(DeliveryReportRejection.MessageNotFound, ChannelHost.Is<DeliveryReportRejected>((await host.Deliveries.ReportAsync(host.Worker,
            new ChannelDeliveryReport(host.Scope, new ChannelKind("email"), "cfg-email", "unknown", ChannelDeliveryState.Read, host.Clock.GetUtcNow()))).Value).Reason);
        Assert.AreEqual(DeliveryReportRejection.EndpointNotFound, ChannelHost.Is<DeliveryReportRejected>((await host.Deliveries.ReportAsync(host.Worker,
            new ChannelDeliveryReport(host.Scope, new ChannelKind("email"), "cfg-missing", "provider-1", ChannelDeliveryState.Read, host.Clock.GetUtcNow()))).Value).Reason);
        Assert.AreEqual(DeliveryReportRejection.Forbidden, ChannelHost.Is<DeliveryReportRejected>((await host.Deliveries.ReportAsync(host.Viewer,
            new ChannelDeliveryReport(host.Scope, new ChannelKind("email"), "cfg-email", "provider-1", ChannelDeliveryState.Read, host.Clock.GetUtcNow()))).Value).Reason);

        // Re-running the dispatcher does not resend.
        await host.DispatchAsync();
        Assert.AreEqual(1, email.Requests.Count);
    }

    [TestMethod]
    public async Task RetryableRejectionsBackOffAndThenSucceed()
    {
        var email = EmailChannel().Then(new ChannelSendRejected("rate-limited", retryable: true)).ThenThrow();
        await using var host = await HostAsync(email);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation)));

        Assert.AreEqual(1, (await host.DispatchAsync()).Retried);
        var scheduled = (await host.Dispatches.GetAsync(host.Scope, inbound.Conversation, messageId))!.Dispatch;
        Assert.AreEqual(ChannelDispatchState.RetryScheduled, scheduled.State);
        Assert.AreEqual("rate-limited", scheduled.Code);
        Assert.AreEqual(0, (await host.DispatchAsync()).Handled, "The outbox backoff has not elapsed.");
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, (await host.DispatchAsync()).Retried, "A thrown channel error is retried.");
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, (await host.DispatchAsync()).Retried);
        Assert.AreEqual(3, email.Requests.Count);
        Assert.AreEqual(1, email.Requests.Select(request => request.IdempotencyKey).Distinct().Count());
        var dispatch = (await host.Dispatches.GetAsync(host.Scope, inbound.Conversation, messageId))!.Dispatch;
        Assert.AreEqual(ChannelDispatchState.Accepted, dispatch.State);
        Assert.AreEqual(3, dispatch.Attempts);
        Assert.AreEqual(MessageDeliveryState.Sent, (await MessageAsync(host, inbound.Conversation, messageId)).Delivery.State);
    }

    [TestMethod]
    public async Task ExhaustedRetriesFailTheMessage()
    {
        var email = EmailChannel();
        for (var attempt = 0; attempt < 3; attempt++)
            email.Then(new ChannelSendRejected("unavailable", retryable: true));
        await using var host = await ChannelHost.CreateAsync(services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<ITicketChannel>(services, email),
            channelOptions: options => options.MaximumSendAttempts = 3);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation)));
        for (var run = 0; run < 3; run++)
        {
            await host.DispatchAsync();
            host.Clock.Advance(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(3, email.Requests.Count);
        var failed = (await MessageAsync(host, inbound.Conversation, messageId)).Delivery;
        Assert.AreEqual(MessageDeliveryState.Failed, failed.State);
        Assert.AreEqual("unavailable", failed.FailureCode);
        Assert.AreEqual(ChannelDispatchState.Rejected, (await host.Dispatches.GetAsync(host.Scope, inbound.Conversation, messageId))!.Dispatch.State);
        await host.DispatchAsync();
        Assert.AreEqual(3, email.Requests.Count);
    }

    [TestMethod]
    public async Task PermanentRejectionsAndUnknownOutcomesAreNotRetried()
    {
        var email = EmailChannel().Then(new ChannelSendRejected("invalid-recipient", retryable: false)).Then(new ChannelSendUnknown());
        await using var host = await HostAsync(email);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var rejected = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation, "one")));
        await host.DispatchAsync();
        var unknown = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation, "two")));
        await host.DispatchAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.DispatchAsync();
        Assert.AreEqual(2, email.Requests.Count);
        Assert.AreEqual("invalid-recipient", (await MessageAsync(host, inbound.Conversation, rejected)).Delivery.FailureCode);
        Assert.AreEqual("outcome-unknown", (await MessageAsync(host, inbound.Conversation, unknown)).Delivery.FailureCode);
        Assert.AreEqual(ChannelDispatchState.Unknown, (await host.Dispatches.GetAsync(host.Scope, inbound.Conversation, unknown))!.Dispatch.State);
    }

    [TestMethod]
    public async Task InterruptedAttemptsAreTreatedAsUnknown()
    {
        var email = EmailChannel();
        await using var host = await HostAsync(email);
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation)));
        // Simulate a crash after the Sending record was written.
        Assert.IsInstanceOfType<StoreApplied<StoredChannelDispatch>>((await host.Dispatches.WriteAsync(host.Scope, new ChannelDispatch(inbound.Conversation, messageId,
            new InboxId("email-inbox"), new ChannelKind("email"), ChannelDispatchState.Sending, 1, host.Clock.GetUtcNow()), null)).Value);
        await host.DispatchAsync();
        Assert.AreEqual(0, email.Requests.Count);
        Assert.AreEqual("outcome-unknown", (await MessageAsync(host, inbound.Conversation, messageId)).Delivery.FailureCode);
    }

    [TestMethod]
    public async Task RecipientFallsBackToContactIdentities()
    {
        var email = EmailChannel();
        await using var host = await HostAsync(email);
        var created = (TicketValue<ContactView>)(await host.Contacts.CreateAsync(host.Owner, new ContactDraft(new ContactProfile("Grace"),
            Identities: [new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.Email, "grace@example.com"), new IdentityClaimed("agent", host.Clock.GetUtcNow()))]))).Value!;
        var started = (ConversationApplied)(await host.Conversations.StartAsync(host.Owner, new StartConversationRequest(new InboxId("email-inbox"), created.Value.Id))).Value!;
        var noRecipient = (TicketValue<ContactView>)(await host.Contacts.CreateAsync(host.Owner, new ContactDraft(new ContactProfile("Nobody")))).Value!;
        var orphan = (ConversationApplied)(await host.Conversations.StartAsync(host.Owner, new StartConversationRequest(new InboxId("email-inbox"), noRecipient.Value.Id))).Value!;
        var first = OutgoingId(Applied(await ReplyAsync(host, started.Conversation)));
        var second = OutgoingId(Applied(await ReplyAsync(host, orphan.Conversation)));
        await host.DispatchAsync();
        Assert.AreEqual("grace@example.com", email.Requests.Single().Recipient.Value);
        Assert.AreEqual(MessageDeliveryState.Sent, (await MessageAsync(host, started.Conversation, first)).Delivery.State);
        Assert.AreEqual("no-recipient", (await MessageAsync(host, orphan.Conversation, second)).Delivery.FailureCode);
        Assert.AreEqual(ChannelDispatchState.Refused, (await host.Dispatches.GetAsync(host.Scope, orphan.Conversation, second))!.Dispatch.State);
    }

    [TestMethod]
    public async Task MisconfiguredWorkerPrincipalsSurfaceAsRetries()
    {
        var email = EmailChannel();
        await using var host = await ChannelHost.CreateAsync(services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<ITicketChannel>(services, email),
            channelOptions: options => options.WorkerPrincipal = scope => new UserPrincipal(scope, new UserId("viewer")));
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation)));
        Assert.AreEqual(1, (await host.DispatchAsync()).Retried);
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        await host.DispatchAsync();
        Assert.AreEqual(1, email.Requests.Count, "An accepted submission is never resent; only the delivery update is retried.");
        Assert.AreEqual(ChannelDispatchState.Accepted, (await host.Dispatches.GetAsync(host.Scope, inbound.Conversation, messageId))!.Dispatch.State);
    }

    [TestMethod]
    public async Task EndpointAdministrationIsAuthorized()
    {
        await using var host = await HostAsync(EmailChannel());
        var kind = new ChannelKind("email");
        Assert.AreEqual(TicketFailureKind.Forbidden, ChannelHost.Is<TicketFailure>((await host.Endpoints.GetAsync(host.Agent, kind, "cfg-email")).Value).Kind);
        var stored = ChannelHost.Is<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.GetAsync(host.Owner, kind, "cfg-email")).Value).Value;
        Assert.AreEqual(new InboxId("email-inbox"), stored.Endpoint.Inbox);
        Assert.AreEqual(TicketFailureKind.Forbidden, ChannelHost.Is<TicketFailure>((await host.Endpoints.CreateAsync(host.Agent,
            new ChannelEndpoint(kind, "cfg-x", new InboxId("email-inbox")))).Value).Kind);
        Assert.AreEqual(TicketFailureKind.Invalid, ChannelHost.Is<TicketFailure>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-x", new InboxId("email-inbox")))).Value).Kind);
        Assert.AreEqual(TicketFailureKind.Invalid, ChannelHost.Is<TicketFailure>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email", new InboxId("missing")))).Value).Kind);
        Assert.AreEqual(TicketFailureKind.Conflict, ChannelHost.Is<TicketFailure>((await host.Endpoints.UpdateAsync(host.Owner, stored.Endpoint, 9)).Value).Kind);
        Assert.IsInstanceOfType<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.UpdateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email", new InboxId("email-inbox"), "mailgun:new"), 1)).Value);
        var inboxes = (IInboxStore)host.Services.GetService(typeof(IInboxStore))!;
        await inboxes.CreateAsync(host.Scope, new Inbox(new InboxId("email-2"), "Email 2", new ChannelBinding(kind, "cfg-email-2")));
        Assert.AreEqual(TicketFailureKind.Rejected, ChannelHost.Is<TicketFailure>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(kind, "cfg-email-2", new InboxId("email-2"), "mailgun:new"))).Value).Kind);
        Assert.AreEqual(TicketFailureKind.NotFound, ChannelHost.Is<TicketFailure>((await host.Endpoints.DeleteAsync(host.Owner, kind, "cfg-none", 1)).Value).Kind);
        Assert.AreEqual(TicketFailureKind.Forbidden, ChannelHost.Is<TicketFailure>((await host.Endpoints.DeleteAsync(host.Agent, kind, "cfg-email", 2)).Value).Kind);
        Assert.IsInstanceOfType<TicketValue<StoredChannelEndpoint>>((await host.Endpoints.DeleteAsync(host.Owner, kind, "cfg-email", 2)).Value);
    }

    [TestMethod]
    public async Task DispatchRecordsAreReadableWithChannelRead()
    {
        await using var host = await HostAsync(EmailChannel());
        var inbound = Recorded(await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>")));
        var messageId = OutgoingId(Applied(await ReplyAsync(host, inbound.Conversation)));
        await host.DispatchAsync();
        Assert.AreEqual(ChannelDispatchState.Accepted, ChannelHost.Is<TicketValue<StoredChannelDispatch>>(
            (await host.Endpoints.GetDispatchAsync(host.Owner, inbound.Conversation, messageId)).Value).Value.Dispatch.State);
        Assert.AreEqual(TicketFailureKind.Forbidden, ChannelHost.Is<TicketFailure>((await host.Endpoints.GetDispatchAsync(host.Agent, inbound.Conversation, messageId)).Value).Kind);
        Assert.AreEqual(TicketFailureKind.NotFound, ChannelHost.Is<TicketFailure>((await host.Endpoints.GetDispatchAsync(host.Owner, new ConversationId("missing"), messageId)).Value).Kind);
    }
}
