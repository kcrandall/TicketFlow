using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Channels.Mailbox.Tests;

[TestClass]
public sealed class MailboxChannelTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TicketScope Scope = TestScopes.Create(workspaceId: "acme");
    private static readonly PrincipalRef Ingestor = new ApiClientPrincipal(Scope, new ApiClientId("mailbox"));

    private sealed class FakeMailboxClient(uint uidValidity, Dictionary<uint, MailboxFetchedMessage> messages) : IMailboxClient
    {
        public Task<MailboxFolderState> OpenFolderAsync(string folder, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailboxFolderState(uidValidity, (messages.Keys.DefaultIfEmpty(0u).Max()) + 1));

        public Task<IReadOnlyList<uint>> ListUidsAfterAsync(uint afterUid, int maxCount, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<uint>>([.. messages.Keys.Where(u => u > afterUid).Order().Take(maxCount)]);

        public Task<MailboxFetchedMessage?> FetchMessageAsync(uint uid, int maxBytes, CancellationToken cancellationToken = default)
        {
            if (!messages.TryGetValue(uid, out var msg))
                return Task.FromResult<MailboxFetchedMessage?>(null);
            if (msg.SizeBytes > maxBytes)
                return Task.FromResult<MailboxFetchedMessage?>(new MailboxFetchedMessage(uid, msg.SizeBytes, null, TooLarge: true));
            return Task.FromResult<MailboxFetchedMessage?>(msg);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClientFactory : IMailboxClientFactory
    {
        public uint UidValidity { get; set; } = 100;
        public Dictionary<uint, MailboxFetchedMessage> Messages { get; } = [];

        public Task<IMailboxClient> ConnectAsync(MailboxEndpointConfig config, MailboxCredentials credentials, CancellationToken cancellationToken = default) =>
            Task.FromResult<IMailboxClient>(new FakeMailboxClient(UidValidity, Messages));
    }

    private sealed class RecordingPipeline : IInboundChannelPipeline
    {
        public List<InboundChannelMessage> Received { get; } = [];
        public Func<InboundChannelMessage, InboundResult>? Handler { get; set; }

        public Task<InboundResult> ReceiveAsync(PrincipalRef actor, InboundChannelMessage message, CancellationToken cancellationToken = default)
        {
            Received.Add(message);
            if (Handler is not null)
                return Task.FromResult(Handler(message));
            return Task.FromResult<InboundResult>(new InboundRecorded(new ConversationId("c-" + Received.Count), new MessageId("m-" + Received.Count), true, false));
        }
    }

    private static MailboxFetchedMessage MimeBytes(uint uid, string messageId, string from, string subject, string textBody, string? htmlBody = null,
        string? inReplyTo = null, string[]? references = null, (string Name, string ContentType, byte[] Data)[]? attachments = null)
    {
        var mime = new MimeMessage();
        mime.MessageId = messageId;
        mime.From.Add(MailboxAddress.Parse(from));
        mime.To.Add(MailboxAddress.Parse("support@acme.example.com"));
        mime.Subject = subject;
        mime.Date = Now;
        if (inReplyTo is not null)
            mime.InReplyTo = inReplyTo;
        if (references is not null)
            foreach (var r in references)
                mime.References.Add(r);
        var builder = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody };
        if (attachments is not null)
            foreach (var (name, contentType, data) in attachments)
                builder.Attachments.Add(name, data, ContentType.Parse(contentType));
        mime.Body = builder.ToMessageBody();
        using var ms = new MemoryStream();
        mime.WriteTo(ms);
        var bytes = ms.ToArray();
        return new MailboxFetchedMessage(uid, bytes.Length, bytes);
    }

    [TestMethod]
    public async Task ParsesMimeThreadingAttachmentsAndAdvancesCheckpoints()
    {
        var clock = TestClock.Create(Now);
        var factory = new FakeClientFactory { UidValidity = 42 };
        var pdfBytes = Encoding.UTF8.GetBytes("%PDF-1.4 invoice");
        factory.Messages[1] = MimeBytes(1, "<msg-1@customer.example.com>", "Ada Lovelace <ada@customer.example.com>", "Invoice question",
            "Please check my invoice.", "<p>Please check my invoice.</p>",
            attachments: [("invoice.pdf", "application/pdf", pdfBytes)]);
        factory.Messages[2] = MimeBytes(2, "<msg-2@customer.example.com>", "ada@customer.example.com", "Re: Invoice question",
            "Follow up", inReplyTo: "<msg-1@customer.example.com>", references: ["<root@customer.example.com>", "<msg-1@customer.example.com>"]);

        var checkpoints = new InMemoryMailboxCheckpointStore();
        var attachments = new InMemoryMailboxAttachmentStore();
        var pipeline = new RecordingPipeline();
        var poller = new MailboxPoller(factory, new StaticMailboxCredentialProvider(new MailboxCredentials("u", "p")),
            checkpoints, new MailboxMimeNormalizer(attachments), pipeline, clock);
        var config = new MailboxEndpointConfig(Scope, "imap-support", "imap.example.com", maximumMessagesPerPoll: 1);

        // Bounded fetch: 1 message per poll.
        var first = await poller.PollAsync(Ingestor, config);
        Assert.AreEqual((1, 1, 0, 0, false, 42u, 1u), (first.Fetched, first.Ingested, first.Deduplicated, first.Skipped, first.Rescanned, first.UidValidity, first.LastUid));
        Assert.AreEqual(1, pipeline.Received.Count);
        var msg1 = pipeline.Received[0];
        Assert.AreEqual("msg-1@customer.example.com", msg1.ExternalMessageId);
        Assert.AreEqual((ContactIdentityKind.Email, "ada@customer.example.com", false), (msg1.Sender.Kind, msg1.Sender.Value, msg1.Sender.Verified));
        Assert.AreEqual("Ada Lovelace", msg1.SenderDisplayName);
        Assert.AreEqual("Invoice question", msg1.Subject);
        Assert.AreEqual(1, msg1.Attachments.Count);
        CollectionAssert.AreEqual(pdfBytes, attachments.GetBytes(msg1.Attachments[0].StorageRef));

        // Second poll resumes after UID 1 and preserves In-Reply-To + References order.
        var second = await poller.PollAsync(Ingestor, config);
        Assert.AreEqual((1, 1, 0, 0, false, 42u, 2u), (second.Fetched, second.Ingested, second.Deduplicated, second.Skipped, second.Rescanned, second.UidValidity, second.LastUid));
        var msg2 = pipeline.Received[1];
        Assert.AreEqual("msg-1@customer.example.com", msg2.InReplyTo);
        CollectionAssert.AreEqual(new[] { "root@customer.example.com", "msg-1@customer.example.com" }, msg2.References.ToArray());

        // Third poll sees nothing new.
        var third = await poller.PollAsync(Ingestor, config);
        Assert.AreEqual(0, third.Fetched);
    }

    [TestMethod]
    public async Task UidValidityChangeTriggersSafeRescanDeduplicatedByMessageId()
    {
        var clock = TestClock.Create(Now);
        var factory = new FakeClientFactory { UidValidity = 10 };
        factory.Messages[1] = MimeBytes(1, "<m1@example.com>", "a@example.com", "One", "Body 1");
        factory.Messages[2] = MimeBytes(2, "<m2@example.com>", "a@example.com", "Two", "Body 2");

        var checkpoints = new InMemoryMailboxCheckpointStore();
        var pipeline = new RecordingPipeline();
        var poller = new MailboxPoller(factory, new StaticMailboxCredentialProvider(new MailboxCredentials("u", "p")),
            checkpoints, new MailboxMimeNormalizer(new InMemoryMailboxAttachmentStore()), pipeline, clock);
        var config = new MailboxEndpointConfig(Scope, "imap-support", "imap.example.com");

        var initial = await poller.PollAsync(Ingestor, config);
        Assert.AreEqual(2, initial.Ingested);

        // Server rebuilds folder with new UIDVALIDITY and renumbers UIDs + adds a new message.
        factory.UidValidity = 99;
        factory.Messages.Clear();
        factory.Messages[10] = MimeBytes(10, "<m1@example.com>", "a@example.com", "One", "Body 1");
        factory.Messages[11] = MimeBytes(11, "<m2@example.com>", "a@example.com", "Two", "Body 2");
        factory.Messages[12] = MimeBytes(12, "<m3@example.com>", "a@example.com", "Three", "Body 3");

        var rescan = await poller.PollAsync(Ingestor, config);
        Assert.IsTrue(rescan.Rescanned);
        Assert.AreEqual((3, 1, 2, 0, 99u, 12u), (rescan.Fetched, rescan.Ingested, rescan.Deduplicated, rescan.Skipped, rescan.UidValidity, rescan.LastUid));
        Assert.AreEqual(3, pipeline.Received.Count, "Already-seen Message-Ids were deduplicated without hitting the pipeline.");
    }

    [TestMethod]
    public async Task OversizedMessagesAreSkippedAndTransientPipelineRejectionsStopCheckpointAdvancement()
    {
        var clock = TestClock.Create(Now);
        var factory = new FakeClientFactory { UidValidity = 7 };
        factory.Messages[1] = new MailboxFetchedMessage(1, 50 * 1024 * 1024, null, TooLarge: true);
        factory.Messages[2] = MimeBytes(2, "<m2@example.com>", "a@example.com", "Hi", "Body");

        var checkpoints = new InMemoryMailboxCheckpointStore();
        var pipeline = new RecordingPipeline { Handler = _ => InboundRejected.Of(InboundRejection.Rejected, "transient") };
        var poller = new MailboxPoller(factory, new StaticMailboxCredentialProvider(new MailboxCredentials("u", "p")),
            checkpoints, new MailboxMimeNormalizer(new InMemoryMailboxAttachmentStore()), pipeline, clock);
        var config = new MailboxEndpointConfig(Scope, "imap-support", "imap.example.com", maximumMessageBytes: 1024 * 1024);

        var run1 = await poller.PollAsync(Ingestor, config);
        Assert.AreEqual((2, 0, 0, 1, 1u), (run1.Fetched, run1.Ingested, run1.Deduplicated, run1.Skipped, run1.LastUid), "Checkpoint stops at UID 1 so UID 2 retries.");

        pipeline.Handler = null;
        var run2 = await poller.PollAsync(Ingestor, config);
        Assert.AreEqual((1, 1, 0, 0, 2u), (run2.Fetched, run2.Ingested, run2.Deduplicated, run2.Skipped, run2.LastUid));

        Assert.IsFalse(new MailboxCredentials("user", "super-secret").ToString().Contains("super-secret", StringComparison.Ordinal));
        var services = new ServiceCollection();
        services.AddTicketFlow().AddMailboxChannel();
        services.AddSingleton<IMailboxCredentialProvider>(new StaticMailboxCredentialProvider(new MailboxCredentials("u", "p")));
        services.AddSingleton<IInboundChannelPipeline>(pipeline);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsNotNull(provider.GetRequiredService<IMailboxCheckpointStore>());
    }
}
