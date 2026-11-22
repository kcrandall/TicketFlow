using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Api.Tests;

[TestClass]
public sealed class ApiChannelTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    private sealed class Destinations(string? uri = "https://hooks.example.com/ticketflow") : IApiChannelDestinationResolver
    {
        public ValueTask<ApiChannelDestination?> ResolveAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(uri is null ? null : new ApiChannelDestination(new Uri(uri), Key, "key-1"));
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    private static ApiChannel Channel(HttpMessageHandler handler, string? uri = "https://hooks.example.com/ticketflow", FakeTimeProvider? clock = null, Action<ApiChannelOptions>? configure = null)
    {
        var options = new ApiChannelOptions();
        configure?.Invoke(options);
        return new ApiChannel(new Destinations(uri), Options.Create(options), clock ?? TestClock.Create(), handler);
    }

    private static OutboundChannelMessage Message() => new(TestScopes.Create(), new InboxId("api-inbox"), new ChannelBinding(new ChannelKind("api"), "cfg-api"),
        new ConversationId("c-1"), new MessageId("m-1"), new ContactId("ct-1"), new ChannelRecipient(ContactIdentityKind.ExternalUser, "user-42", "shop"),
        "Hello **there**", MessageContentType.Markdown, "tf1-key", [new AttachmentRef("store/1", "a.pdf", "application/pdf", 12)],
        new ChannelThreadContext("thread-1", "in-1", ["r1", "in-1"], "Subject"));

    [TestMethod]
    public async Task SendsSignedVersionedPayloads()
    {
        var clock = TestClock.Create();
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"externalMessageId":"remote-7"}""", Encoding.UTF8, "application/json"),
        });
        using var channel = Channel(handler, clock: clock);
        var accepted = Is<ChannelSendAccepted>((await channel.SendAsync(Message())).Value);
        Assert.AreEqual("remote-7", accepted.ExternalMessageId);

        var (request, body) = handler.Requests.Single();
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("tf1-key", request.Headers.GetValues(ApiWebhookHeaders.Id).Single());
        Assert.AreEqual("key-1", request.Headers.GetValues(ApiWebhookHeaders.KeyId).Single());
        var verified = ApiWebhookSignature.Verify(Key, request.Headers.GetValues(ApiWebhookHeaders.Id).Single(), request.Headers.GetValues(ApiWebhookHeaders.Timestamp).Single(),
            request.Headers.GetValues(ApiWebhookHeaders.Signature).Single(), body, clock.GetUtcNow());
        Assert.IsInstanceOfType<ApiWebhookVerified>(verified.Value);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.AreEqual(1, root.GetProperty("v").GetInt32());
        Assert.AreEqual("message.outgoing", root.GetProperty("type").GetString());
        Assert.AreEqual("c-1", root.GetProperty("conversation").GetString());
        Assert.AreEqual("m-1", root.GetProperty("message").GetString());
        Assert.AreEqual("external_user", root.GetProperty("recipient").GetProperty("kind").GetString());
        Assert.AreEqual("shop", root.GetProperty("recipient").GetProperty("namespace").GetString());
        Assert.AreEqual("markdown", root.GetProperty("contentType").GetString());
        Assert.AreEqual("a.pdf", root.GetProperty("attachments")[0].GetProperty("fileName").GetString());
        Assert.AreEqual("in-1", root.GetProperty("thread").GetProperty("inReplyTo").GetString());
        Assert.AreEqual(2, root.GetProperty("thread").GetProperty("references").GetArrayLength());
    }

    [TestMethod]
    public async Task MapsHttpOutcomes()
    {
        using var unavailable = Channel(new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        }));
        var retry = Is<ChannelSendRejected>((await unavailable.SendAsync(Message())).Value);
        Assert.AreEqual("http-503", retry.Code);
        Assert.IsTrue(retry.Retryable);
        Assert.AreEqual(TimeSpan.FromSeconds(30), retry.RetryAfter);

        using var throttled = Channel(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.IsTrue(Is<ChannelSendRejected>((await throttled.SendAsync(Message())).Value).Retryable);
        using var bad = Channel(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var permanent = Is<ChannelSendRejected>((await bad.SendAsync(Message())).Value);
        Assert.AreEqual("http-400", permanent.Code);
        Assert.IsFalse(permanent.Retryable);
        using var plain = Channel(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("ok") }));
        Assert.IsNull(Is<ChannelSendAccepted>((await plain.SendAsync(Message())).Value).ExternalMessageId);
        using var refused = Channel(new ScriptedHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        Assert.IsTrue(Is<ChannelSendRejected>((await refused.SendAsync(Message())).Value).Retryable);
        using var reset = Channel(new ScriptedHandler((_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded, "reset")));
        Assert.AreEqual("transport-error", Is<ChannelSendUnknown>((await reset.SendAsync(Message())).Value).Code);
        using var timeout = Channel(new ScriptedHandler((_, _) => throw new TaskCanceledException("timeout")));
        Assert.AreEqual("timeout", Is<ChannelSendUnknown>((await timeout.SendAsync(Message())).Value).Code);
        using var missing = Channel(new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)), uri: null);
        Assert.AreEqual("destination-missing", Is<ChannelSendRejected>((await missing.SendAsync(Message())).Value).Code);
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task StalledResponseBodiesAreBoundedByTheTimeout()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var content = new StreamContent(new StalledStream());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var channel = Channel(handler, configure: options => options.Timeout = TimeSpan.FromSeconds(1));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var accepted = Is<ChannelSendAccepted>((await channel.SendAsync(Message())).Value);
        Assert.IsNull(accepted.ExternalMessageId);
        Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(10), "The body read must not hang.");
    }

    [TestMethod]
    public async Task BlocksUnsafeDestinationsBeforeConnecting()
    {
        foreach (var uri in new[] { "http://hooks.example.com/x", "https://user:pw@hooks.example.com/x", "https://hooks.example.com:8443/x", "https://localhost/x",
                     "https://127.0.0.1/x", "https://10.1.2.3/x", "https://169.254.169.254/latest/meta-data", "https://[::1]/x", "https://[fe80::1]/x",
                     "https://192.168.1.10/x", "https://100.64.0.1/x", "https://[2001:db8::1]/x", "https://hooks.example.com./x" })
        {
            var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
            using var channel = Channel(handler, uri);
            Assert.AreEqual("endpoint-blocked", Is<ChannelSendRejected>((await channel.SendAsync(Message())).Value).Code, uri);
            Assert.AreEqual(0, handler.Requests.Count, uri);
        }
        var policy = new ApiWebhookDestinationPolicy(["hooks.example.com"]);
        Assert.IsTrue(policy.Allows(new Uri("https://hooks.example.com/x")));
        Assert.IsFalse(policy.Allows(new Uri("https://evil.example.com/x")));
        Assert.IsFalse(policy.Allows(new Uri("https://8.8.8.8/x")), "IP literals never match a host allow-list.");
        Assert.IsTrue(new ApiWebhookDestinationPolicy().Allows(new Uri("https://8.8.8.8/x")));
        Assert.ThrowsExactly<ArgumentException>(() => new ApiWebhookDestinationPolicy(["*.example.com"]));
    }

    [TestMethod]
    public async Task BlocksHostsResolvingToPrivateNetworks()
    {
        var policy = new ApiWebhookDestinationPolicy();
        var dialled = new List<IPAddress>();
        var guarded = ApiWebhookHttp.CreateHandler(policy,
            resolve: (host, _) => Task.FromResult(host == "rebind.example.com" ? new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5") } : [IPAddress.Parse("93.184.216.34")]),
            dial: (address, _, _) =>
            {
                dialled.Add(address);
                throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
            });
        using var rebinding = Channel(guarded, "https://rebind.example.com/x");
        Assert.AreEqual("endpoint-blocked", Is<ChannelSendRejected>((await rebinding.SendAsync(Message())).Value).Code);
        Assert.AreEqual(0, dialled.Count);

        var publicHandler = ApiWebhookHttp.CreateHandler(policy, resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }),
            dial: (address, _, _) =>
            {
                dialled.Add(address);
                throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);
            });
        using var unreachable = Channel(publicHandler, "https://public.example.com/x");
        Assert.IsTrue(Is<ChannelSendRejected>((await unreachable.SendAsync(Message())).Value).Retryable);
        CollectionAssert.AreEqual(new[] { IPAddress.Parse("93.184.216.34") }, dialled);
    }

    [TestMethod]
    public async Task ApiChannelRoundTripThroughThePipeline()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"externalMessageId":"remote-1"}""", Encoding.UTF8, "application/json"),
        });
        ApiChannel? channel = null;
        await using var host = await ChannelHost.CreateAsync(services =>
        {
            services.AddOptions<ApiChannelOptions>();
            services.AddSingleton<ITicketChannel>(provider => channel = new ApiChannel(new Destinations(), provider.GetRequiredService<IOptions<ApiChannelOptions>>(),
                provider.GetRequiredService<TimeProvider>(), handler));
            services.AddScoped<ApiChannelInbound>();
        });
        var inboxes = host.Services.GetRequiredService<IInboxStore>();
        await inboxes.CreateAsync(host.Scope, new Inbox(new InboxId("api-inbox"), "API", new ChannelBinding(new ChannelKind("api"), "cfg-api")));
        Assert.IsInstanceOfType<ChannelValue<StoredChannelEndpoint>>((await host.Endpoints.CreateAsync(host.Owner,
            new ChannelEndpoint(new ChannelKind("api"), "cfg-api", new InboxId("api-inbox")))).Value);
        var inbound = host.Services.GetRequiredService<ApiChannelInbound>();

        var sender = new ChannelSenderEvidence(ContactIdentityKind.ExternalUser, "user-42", verified: true, @namespace: "shop");
        var recorded = Is<InboundRecorded>((await inbound.ReceiveAsync(host.Worker, "cfg-api", new ApiInboundMessage("evt-1", sender, Text: "Where is my order?", ThreadId: "order-42"))).Value);
        var followUp = Is<InboundRecorded>((await inbound.ReceiveAsync(host.Worker, "cfg-api", new ApiInboundMessage("evt-2", sender, Text: "Any news?", ThreadId: "order-42"))).Value);
        Assert.AreEqual(recorded.Conversation, followUp.Conversation);
        Assert.AreEqual(InboundRejection.Invalid, Is<InboundRejected>((await inbound.ReceiveAsync(host.Worker, "cfg-api", new ApiInboundMessage("evt-3", sender))).Value).Reason);

        var reply = Is<ConversationApplied>((await host.Replies.ReplyAsync(host.Agent, recorded.Conversation, new OutgoingMessageDraft("Shipped today"))).Value);
        var messageId = reply.Events.Select(item => item.Event.Value).OfType<MessageEvent>().Single().Id;
        await host.DispatchAsync();
        var (_, body) = handler.Requests.Single();
        using (var json = JsonDocument.Parse(body))
        {
            Assert.AreEqual("user-42", json.RootElement.GetProperty("recipient").GetProperty("value").GetString());
            Assert.AreEqual("order-42", json.RootElement.GetProperty("thread").GetProperty("externalThreadId").GetString());
            Assert.AreEqual("Shipped today", json.RootElement.GetProperty("body").GetString());
        }
        Assert.IsInstanceOfType<DeliveryReportApplied>((await inbound.ReportDeliveryAsync(host.Worker, "cfg-api", "remote-1", ChannelDeliveryState.Read)).Value);
        var timeline = Is<TimelinePage>((await host.Conversations.ReadTimelineAsync(host.Owner, recorded.Conversation)).Value);
        var sent = timeline.Events.Select(item => item.Event.Event.Value).OfType<MessageEvent>().Single(message => message.Id == messageId);
        Assert.AreEqual(MessageDeliveryState.Read, sent.Delivery.State);
        Assert.AreEqual("remote-1", sent.ExternalId);
        channel?.Dispose();
    }

    private static T Is<T>(object? value) where T : class
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value!;
    }
}
