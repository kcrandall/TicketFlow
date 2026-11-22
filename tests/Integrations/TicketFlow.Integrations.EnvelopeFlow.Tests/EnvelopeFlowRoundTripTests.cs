using EnvelopeFlow.Core;
using EnvelopeFlow.Inbound;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Channels;
using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Surveys;
using TicketFlow.Integrations.EnvelopeFlow.Surveys;

namespace TicketFlow.Integrations.EnvelopeFlow.Tests;

[TestClass]
public sealed class EnvelopeFlowRoundTripTests
{
    [TestMethod]
    public async Task EmailRoundTripThreadsRepliesAndAppliesReceipts()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        var first = fixture.Message("mailgun", "acct", "<m1@mail.example>", "ada@example.com", "support@acme.example", "Where is my order?", subject: "Order 42");
        await fixture.Handler.HandleAsync(first);
        await fixture.Handler.HandleAsync(first);
        var conversation = await fixture.SingleConversationAsync("email-inbox");
        Assert.AreEqual(1, (await fixture.MessagesAsync(conversation)).Count, "Redelivered events are idempotent.");

        var reply = await fixture.ReplyAsync(conversation, "It ships today.");
        await fixture.Host.DispatchAsync();
        var email = fixture.Email.Sent.Single();
        Assert.AreEqual("support@acme.example", email.From.Address);
        Assert.AreEqual("ada@example.com", email.To.Single().Address);
        Assert.AreEqual("Re: Order 42", email.Subject);
        Assert.AreEqual("It ships today.", email.TextBody);
        Assert.AreEqual("<m1@mail.example>", email.Headers.Single(header => header.Name == "In-Reply-To").Value);
        Assert.AreEqual("<m1@mail.example>", email.Headers.Single(header => header.Name == "References").Value);
        Assert.IsTrue(email.SubmissionContext!.Key.StartsWith("tf1-", StringComparison.Ordinal));
        var sent = (await fixture.MessagesAsync(conversation)).Single(message => message.Id == reply);
        Assert.AreEqual(MessageDeliveryState.Sent, sent.Delivery.State);
        Assert.AreEqual("<out-1@ticketflow.example>", sent.ExternalId);

        // The customer answers our Message-Id; the second reply carries the whole chain.
        await fixture.Handler.HandleAsync(fixture.Message("mailgun", "acct", "<m2@mail.example>", "ada@example.com", "support@acme.example", "Thanks!",
            inReplyTo: "<out-1@ticketflow.example>", subject: "Re: Order 42"));
        Assert.AreEqual(3, (await fixture.MessagesAsync(conversation)).Count);
        await fixture.ReplyAsync(conversation, "You're welcome.");
        await fixture.Host.DispatchAsync();
        var second = fixture.Email.Sent[1];
        Assert.AreEqual("<m2@mail.example>", second.Headers.Single(header => header.Name == "In-Reply-To").Value);
        Assert.AreEqual("<m1@mail.example> <out-1@ticketflow.example> <m2@mail.example>", second.Headers.Single(header => header.Name == "References").Value);
        Assert.AreEqual("Re: Order 42", second.Subject);

        await fixture.Handler.HandleAsync(fixture.Receipt("mailgun", "acct", "<out-1@ticketflow.example>", InboundDeliveryState.Delivered));
        await fixture.Handler.HandleAsync(fixture.Receipt("mailgun", "acct", "<out-1@ticketflow.example>", InboundDeliveryState.Accepted));
        Assert.AreEqual(MessageDeliveryState.Delivered, (await fixture.MessagesAsync(conversation)).Single(message => message.Id == reply).Delivery.State);
    }

    [TestMethod]
    public async Task SmsUsesTheBusinessNumberEndpointAndReportsFailures()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        await fixture.Handler.HandleAsync(fixture.Message("twilio", "AC1", "SM0001", "+15550001111", "+19995550000", "Hi there"));
        await fixture.Handler.HandleAsync(fixture.Message("twilio", "AC1", "SM0002", "+15550001111", "+19995550000", "Still there?"));
        var conversation = await fixture.SingleConversationAsync("sms-inbox");
        Assert.AreEqual(2, (await fixture.MessagesAsync(conversation)).Count);
        var view = ChannelHost.Is<ConversationView>((await fixture.Host.Conversations.GetAsync(fixture.Host.Owner, conversation)).Value);
        var contact = ChannelHost.Is<ContactApplied<ContactView>>((await fixture.Host.Contacts.GetAsync(fixture.Host.Owner, view.Contact)).Value).Value;
        Assert.IsFalse(contact.Identities.Single().Verification.IsVerified, "SMS sender numbers are claims.");

        var reply = await fixture.ReplyAsync(conversation, "Yes!");
        await fixture.Host.DispatchAsync();
        var sms = fixture.Sms.Sent.Single();
        Assert.AreEqual("+15550001111", sms.To);
        Assert.AreEqual("+19995550000", sms.From);
        await fixture.Handler.HandleAsync(fixture.Receipt("twilio", "AC1", "SMout1", InboundDeliveryState.Undelivered, "30007", from: "+19995550000"));
        var failed = (await fixture.MessagesAsync(conversation)).Single(message => message.Id == reply).Delivery;
        Assert.AreEqual(MessageDeliveryState.Failed, failed.State);
        Assert.AreEqual("e-30007", failed.FailureCode);
    }

    [TestMethod]
    public async Task UncorrelatedReceiptsRetryWithinTheGraceWindowThenDrop()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        await Assert.ThrowsExactlyAsync<EnvelopeFlowReceiptPendingException>(() =>
            fixture.Handler.HandleAsync(fixture.Receipt("twilio", "AC1", "SM9999", InboundDeliveryState.Delivered, from: "+19995550000")));
        await fixture.Handler.HandleAsync(fixture.Receipt("twilio", "AC1", "SM9999", InboundDeliveryState.Delivered, observedAt: fixture.Host.Clock.GetUtcNow().AddHours(-1), from: "+19995550000"));
    }

    [TestMethod]
    public async Task WhatsAppEnforcesTheWindowAndResolvesBindingsFromChannelState()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        await fixture.Handler.HandleAsync(fixture.Message("whatsapp-meta", "waba-1", "wamid.in1", "+15550001111", "phone-1", "Hola"));
        var conversation = await fixture.SingleConversationAsync("wa-inbox");
        var reply = await fixture.ReplyAsync(conversation, "Hi!");
        await fixture.Host.DispatchAsync();
        var (request, context) = fixture.WhatsApp.Sent.Single();
        Assert.AreEqual("Hi!", request.Message.Text);
        Assert.IsNull(request.Message.ReplyToMessageId);
        Assert.AreEqual("+15550001111", context.Recipient.Value);
        Assert.AreEqual(conversation, context.Conversation);
        Assert.AreEqual(fixture.Host.Clock.GetUtcNow(), context.LastIncomingAt);
        Assert.AreEqual(EnvelopeFlowDestinations.IdentityNamespace, request.Destination.Owner.Identity.IdentityNamespace);
        Assert.AreEqual("wamid.out1", (await fixture.MessagesAsync(conversation)).Single(message => message.Id == reply).ExternalId);

        await fixture.Handler.HandleAsync(fixture.Receipt("whatsapp-meta", "waba-1", "wamid.out1", InboundDeliveryState.Read));
        Assert.AreEqual(MessageDeliveryState.Read, (await fixture.MessagesAsync(conversation)).Single(message => message.Id == reply).Delivery.State);

        fixture.Host.Clock.Advance(TimeSpan.FromHours(25));
        Assert.IsInstanceOfType<ReplyWindowClosed>((await fixture.Host.Replies.ReplyAsync(fixture.Host.Agent, conversation, new OutgoingMessageDraft("Late"))).Value);

        // A destination for a different contact or scope does not resolve.
        var resolver = fixture.Host.Services.GetRequiredService<EnvelopeFlowDestinationContextResolver>();
        var destination = request.Destination;
        Assert.IsNull(await resolver.ResolveAsync(new RecipientBinding(destination.Owner.Scope, "someone-else", new IdentityReference(EnvelopeFlowDestinations.IdentityNamespace, "someone-else")),
            destination.DestinationId));
        Assert.IsNull(await resolver.ResolveAsync(new RecipientBinding(new EnvelopeScope("other", "project", "test", "app"), destination.Owner.RecipientId, destination.Owner.Identity),
            destination.DestinationId));
        Assert.IsNull(await resolver.ResolveAsync(destination.Owner, "garbage"));
    }

    [TestMethod]
    public async Task SlackRepliesThreadUnderTheRootTs()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        await fixture.Handler.HandleAsync(fixture.Message("slack", "T1", "T1:C1:1700000000.000100", "U123", "C1", "Help with billing"));
        await fixture.Handler.HandleAsync(fixture.Message("slack", "T1", "T1:C1:1700000005.000200", "U123", "C1", "Any update?", inReplyTo: "T1:C1:1700000000.000100"));
        var conversation = await fixture.SingleConversationAsync("slack-inbox");
        Assert.AreEqual(2, (await fixture.MessagesAsync(conversation)).Count);
        var view = ChannelHost.Is<ConversationView>((await fixture.Host.Conversations.GetAsync(fixture.Host.Owner, conversation)).Value);
        Assert.AreEqual("C1:1700000000.000100", view.ChannelThread!.ExternalThreadId);
        await fixture.ReplyAsync(conversation, "Looking into it");
        await fixture.Host.DispatchAsync();
        var chat = fixture.Chat.Sent.Single();
        Assert.AreEqual("1700000000.000100", chat.Message.ThreadId);
        Assert.AreEqual("Looking into it", chat.Message.Text);

        // A new top-level Slack message is a new thread.
        await fixture.Handler.HandleAsync(fixture.Message("slack", "T1", "T1:C1:1700000100.000100", "U123", "C1", "Different topic"));
        var page = ChannelHost.Is<ConversationListPage>((await fixture.Host.Conversations.ListAsync(fixture.Host.Owner, new ConversationListQuery(inbox: new InboxId("slack-inbox")))).Value);
        Assert.AreEqual(2, page.Conversations.Count);
    }

    [TestMethod]
    public async Task AttachmentReferencesAreRecordedWithoutDownloading()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        await fixture.Handler.HandleAsync(fixture.Message("twilio", "AC1", "MM0001", "+15550001111", "+19995550000", text: null,
            attachments: [new InboundAttachment("https://api.twilio.com/media/ME1", "image/jpeg"), new InboundAttachment("https://api.twilio.com/media/ME2")]));
        var conversation = await fixture.SingleConversationAsync("sms-inbox");
        var message = (await fixture.MessagesAsync(conversation)).Single();
        Assert.AreEqual(2, message.Attachments.Count);
        Assert.AreEqual("envelopeflow:twilio:https://api.twilio.com/media/ME1", message.Attachments[0].StorageRef);
        Assert.AreEqual("image/jpeg", message.Attachments[0].MediaType);
        Assert.AreEqual("application/octet-stream", message.Attachments[1].MediaType);
    }

    [TestMethod]
    public async Task UnroutableEventsAreDropped()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        await fixture.Handler.HandleAsync(fixture.Message("unknown-provider", "acct", "m1", "ada@example.com"));
        await fixture.Handler.HandleAsync(fixture.Message("mailgun", "other-account", "<m1@x>", "ada@example.com"));
        await fixture.Handler.HandleAsync(fixture.Message("mailgun", "acct", "<m2@x>", "ada@example.com", scope: new EnvelopeScope("other-tenant", "project", "test", "app")));
        await fixture.Handler.HandleAsync(fixture.Message("mailgun", "acct", "<m3@x>", "not-an-email"));
        await fixture.Handler.HandleAsync(new InboundEvent
        {
            Scope = fixture.EnvelopeScope, Provider = "mailgun", ProviderAccountId = "acct", EventId = "f1", ProviderMessageId = "<out@x>",
            Kind = InboundEventKind.Feedback, ObservedAt = fixture.Host.Clock.GetUtcNow(), FeedbackKind = InboundFeedbackKind.Complaint, To = "ada@example.com",
        });
        var page = ChannelHost.Is<ConversationListPage>((await fixture.Host.Conversations.ListAsync(fixture.Host.Owner, new ConversationListQuery())).Value);
        Assert.AreEqual(0, page.Conversations.Count);
    }

    [TestMethod]
    public async Task EmailRejectionsMapToRetryAdvice()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        fixture.Email.Results.Enqueue(new global::EnvelopeFlow.Email.EmailSubmissionResult(global::EnvelopeFlow.Email.EmailSubmissionStatus.Rejected, global::EnvelopeFlow.Email.EmailRetryAdvice.Retry,
            global::EnvelopeFlow.Email.EmailFailureCode.TransportUnavailable));
        fixture.Email.Results.Enqueue(new global::EnvelopeFlow.Email.EmailSubmissionResult(global::EnvelopeFlow.Email.EmailSubmissionStatus.Unknown));
        await fixture.Handler.HandleAsync(fixture.Message("mailgun", "acct", "<m1@x>", "ada@example.com", subject: "Hi"));
        var conversation = await fixture.SingleConversationAsync("email-inbox");
        var reply = await fixture.ReplyAsync(conversation);
        Assert.AreEqual(1, (await fixture.Host.DispatchAsync()).Retried);
        fixture.Host.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Host.DispatchAsync();
        Assert.AreEqual(2, fixture.Email.Sent.Count);
        Assert.AreEqual("outcome-unknown", (await fixture.MessagesAsync(conversation)).Single(message => message.Id == reply).Delivery.FailureCode);
        fixture.Host.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Host.DispatchAsync();
        Assert.AreEqual(2, fixture.Email.Sent.Count, "Unknown outcomes are never resent.");
    }

    [TestMethod]
    public async Task SurveyInvitationsGoThroughTheConversationChannel()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync(options =>
        {
            options.LinkFactory = message => new Uri($"https://support.acme.example/s/{message.Token.Value}");
            options.WhatsAppTemplate = ("csat_invite", "en");
        });
        var store = fixture.Host.Services.GetRequiredService<ISurveyStore>();
        Assert.IsInstanceOfType<SurveyWriteApplied<StoredSurvey>>((await store.CreateSurveyAsync(fixture.Host.Scope, new SurveyDefinition(new SurveyId("csat"), "CSAT",
            SurveyKind.Csat, new SurveyLocale("en"), [new SurveyQuestion(new SurveyLocale("en"), "How did we do?")],
            new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(7))))).Value);

        await fixture.Handler.HandleAsync(fixture.Message("mailgun", "acct", "<m1@x>", "ada@example.com", subject: "Order 42"));
        var emailConversation = await fixture.SingleConversationAsync("email-inbox");
        await fixture.Handler.HandleAsync(fixture.Message("whatsapp-meta", "waba-1", "wamid.in1", "+15550001111", "phone-1", "Hola"));
        var whatsAppConversation = await fixture.SingleConversationAsync("wa-inbox");
        foreach (var conversation in new[] { emailConversation, whatsAppConversation })
            Assert.IsInstanceOfType<ConversationApplied>((await fixture.Host.Conversations.ChangeStatusAsync(fixture.Host.Owner, conversation, ConversationStatus.Resolved)).Value);
        await fixture.Host.DispatchAsync();
        fixture.Host.Clock.Advance(TimeSpan.FromHours(30));

        var summary = await fixture.Host.Services.GetRequiredService<ISurveyDispatcher>().DispatchDueAsync(fixture.Host.Scope);
        Assert.AreEqual(2, summary.Sent);
        var email = fixture.Email.Sent.Single();
        Assert.AreEqual("ada@example.com", email.To.Single().Address);
        StringAssert.Contains(email.TextBody, "How did we do?");
        StringAssert.Contains(email.TextBody, "https://support.acme.example/s/tfs1.");
        var (request, _) = fixture.WhatsApp.Sent.Single();
        Assert.AreEqual("csat_invite", request.Message.Template!.Name, "Outside the 24-hour window an approved template is used.");
        StringAssert.StartsWith(request.Message.Template.Components.Single().Parameters.Single(), "https://support.acme.example/s/tfs1.");
    }

    [TestMethod]
    public async Task AlternatingSlackSpeakersKeepOneConversationEach()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        await fixture.Handler.HandleAsync(fixture.Message("slack", "T1", "T1:C1:1700000000.000100", "U-A", "C1", "Question"));
        await fixture.Handler.HandleAsync(fixture.Message("slack", "T1", "T1:C1:1700000001.000100", "U-B", "C1", "Me too", inReplyTo: "T1:C1:1700000000.000100"));
        await fixture.Handler.HandleAsync(fixture.Message("slack", "T1", "T1:C1:1700000002.000100", "U-A", "C1", "Any update?", inReplyTo: "T1:C1:1700000000.000100"));
        await fixture.Handler.HandleAsync(fixture.Message("slack", "T1", "T1:C1:1700000003.000100", "U-B", "C1", "Same", inReplyTo: "T1:C1:1700000000.000100"));
        var page = ChannelHost.Is<ConversationListPage>((await fixture.Host.Conversations.ListAsync(fixture.Host.Owner, new ConversationListQuery(inbox: new InboxId("slack-inbox")))).Value);
        Assert.AreEqual(2, page.Conversations.Count);
        foreach (var conversation in page.Conversations)
            Assert.AreEqual(2, (await fixture.MessagesAsync(conversation.Id)).Count);
    }

    [TestMethod]
    public async Task ConflictingReceiptsAreRetried()
    {
        await using var fixture = await EnvelopeFlowFixture.CreateAsync();
        var handler = new EnvelopeFlowInboundEventHandler(fixture.Host.Services.GetRequiredService<IChannelEndpointStore>(),
            fixture.Host.Services.GetRequiredService<IInboundChannelPipeline>(), new ConflictingDeliveries(),
            fixture.Host.Services.GetRequiredService<IChannelWorkerPrincipalResolver>(),
            fixture.Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EnvelopeFlowIntegrationOptions>>(), fixture.Host.Clock);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            handler.HandleAsync(fixture.Receipt("mailgun", "acct", "<out@x>", InboundDeliveryState.Delivered, observedAt: fixture.Host.Clock.GetUtcNow().AddDays(-1))));
    }

    [TestMethod]
    public void AdapterDependsOnlyOnChannelAbstractionsAndRegistersEachKindOnce()
    {
        var references = typeof(EnvelopeFlowInboundEventHandler).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToList();
        CollectionAssert.Contains(references, "TicketFlow.Channels.Abstractions");
        CollectionAssert.DoesNotContain(references, "TicketFlow.Channels");
        CollectionAssert.DoesNotContain(references, "TicketFlow.Surveys.Abstractions");
        var services = new ServiceCollection();
        services.AddTicketFlow().AddEnvelopeFlowEmailChannel().AddEnvelopeFlowEmailChannel().AddEnvelopeFlowSlackChannel().AddEnvelopeFlowSlackChannel();
        Assert.AreEqual(2, services.Count(descriptor => descriptor.ServiceType == typeof(ITicketChannel)));
    }

    private sealed class ConflictingDeliveries : IChannelDeliveryIngestor
    {
        public Task<DeliveryReportResult> ReportAsync(PrincipalRef actor, ChannelDeliveryReport report, CancellationToken cancellationToken = default) =>
            Task.FromResult<DeliveryReportResult>(new DeliveryReportRejected(DeliveryReportRejection.Conflict));
    }
}
