using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Channels.StoreContracts;
using TicketFlow.Channels.Testing;
using TicketFlow.Contacts;
using TicketFlow.Contacts.Stores.Postgres;
using TicketFlow.Conversations;
using TicketFlow.Conversations.Stores.Postgres;
using TicketFlow.Testing;
using TicketFlow.Workspaces;
using TicketFlow.Workspaces.Stores.Postgres;

namespace TicketFlow.Channels.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresChannelStoreTests
{
    private sealed class Database : IAsyncDisposable
    {
        private readonly PostgresTestDatabase storage;

        private Database(PostgresTestDatabase storage)
        {
            this.storage = storage;
            Store = new(storage.First);
            Competing = new(storage.Second);
        }

        public PostgresChannelStore Store { get; }
        public PostgresChannelStore Competing { get; }
        public PostgresTestDatabase Storage => storage;

        public static Task<Database> CreateAsync() => PostgresTestDatabase.CreateAsync("tf_channels_test_", async storage =>
        {
            var database = new Database(storage);
            await database.Store.InitializeAsync();
            await database.Competing.InitializeAsync();
            return database;
        }, maximumPoolSize: 16);

        public ValueTask DisposeAsync() => storage.DisposeAsync();
    }

    [TestMethod]
    public async Task EndpointContract()
    {
        await using var database = await Database.CreateAsync();
        await ChannelStoreContract.VerifyEndpointsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ConversationStateContract()
    {
        await using var database = await Database.CreateAsync();
        await ChannelStoreContract.VerifyConversationStateAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task ThreadContract()
    {
        await using var database = await Database.CreateAsync();
        await ChannelStoreContract.VerifyThreadsAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task DispatchContract()
    {
        await using var database = await Database.CreateAsync();
        await ChannelStoreContract.VerifyDispatchesAsync(database.Store, database.Competing);
    }

    [TestMethod]
    public async Task UndecodableRowsThrow()
    {
        await using var database = await Database.CreateAsync();
        var scope = TestScopes.Create();
        var conversation = new Core.ConversationId("c-1");
        await database.Store.WriteAsync(scope, new ChannelConversationState(conversation, new Core.InboxId("inbox"), new ChannelKind("email"),
            new ChannelRecipient(ContactIdentityKind.Email, "ada@example.com")), null);
        await using (var command = database.Storage.First.CreateCommand("UPDATE tf_channel_conversation_states SET channel_kind = 'Not A Token'"))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => database.Store.GetAsync(scope, conversation));
    }

    [TestMethod]
    public async Task PostgresRoundTripFromInboundToDeliveryStatus()
    {
        await using var database = await Database.CreateAsync();
        var source = database.Storage.First;
        var email = new FakeChannel(new ChannelKind("email"), new ChannelCapabilities(ChannelFeatures.Threading | ChannelFeatures.DeliveryReceipts), ContactIdentityKind.Email)
            .Then(new ChannelSendAccepted("provider-1", "<out-1@ticketflow.example>"));
        await using var host = await ChannelHost.CreateAsync(
            services => services.AddSingleton<ITicketChannel>(email),
            builder => builder.AddPostgresAccessStores(source).AddPostgresWorkspaceStores(source).AddPostgresContactStores(source)
                .AddPostgresConversationStores(source).AddPostgresChannelStores(source),
            initialize: async provider =>
            {
                await new PostgresAccessStore(source).InitializeAsync();
                await new PostgresWorkspaceStore(source).InitializeAsync();
                await new PostgresContactStore(source).InitializeAsync();
                await new PostgresConversationStore(source).InitializeAsync();
            });

        var inbound = await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>", subject: "Order 42"));
        var recorded = ChannelHost.Is<InboundRecorded>(inbound.Value);
        Assert.IsTrue(ChannelHost.Is<InboundRecorded>((await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<root@x>"))).Value).Replayed);
        var reply = ChannelHost.Is<ConversationApplied>((await host.Replies.ReplyAsync(host.Agent, recorded.Conversation, new OutgoingMessageDraft("On it"))).Value);
        var messageId = reply.Events.Select(item => item.Event.Value).OfType<MessageEvent>().Single().Id;
        await host.DispatchAsync();
        var request = email.Requests.Single();
        Assert.AreEqual("root@x", request.Thread.InReplyTo);
        Assert.AreEqual("Order 42", request.Thread.Subject);
        Assert.AreEqual(ChannelDispatchState.Accepted, (await host.Dispatches.GetAsync(host.Scope, recorded.Conversation, messageId))!.Dispatch.State);

        var answer = ChannelHost.Is<InboundRecorded>((await host.Pipeline.ReceiveAsync(host.Worker, host.Email("<answer@x>", inReplyTo: "<out-1@ticketflow.example>"))).Value);
        Assert.AreEqual(recorded.Conversation, answer.Conversation);
        Assert.IsInstanceOfType<DeliveryReportApplied>((await host.Deliveries.ReportAsync(host.Worker, new ChannelDeliveryReport(host.Scope, new ChannelKind("email"), "cfg-email",
            "provider-1", ChannelDeliveryState.Read, host.Clock.GetUtcNow()))).Value);
        var timeline = ChannelHost.Is<TimelinePage>((await host.Conversations.ReadTimelineAsync(host.Owner, recorded.Conversation)).Value);
        var sent = timeline.Events.Select(item => item.Event.Event.Value).OfType<MessageEvent>().Single(message => message.Id == messageId);
        Assert.AreEqual(MessageDeliveryState.Read, sent.Delivery.State);
        Assert.AreEqual("provider-1", sent.ExternalId);
    }
}
