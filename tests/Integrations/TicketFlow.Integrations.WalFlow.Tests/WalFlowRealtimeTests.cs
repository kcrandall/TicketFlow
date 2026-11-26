using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Conversations.Stores.Postgres;
using TicketFlow.Core;
using TicketFlow.Realtime;
using TicketFlow.Testing;
using WalFlow.Abstractions.Models;

namespace TicketFlow.Integrations.WalFlow.Tests;

[TestClass]
public sealed partial class WalFlowRealtimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TicketScope Scope = TestScopes.Create(workspaceId: "acme");

    [TestMethod]
    public void PublicationEnvelopeRoundTripsEveryTopicAndEvent()
    {
        var session = new VisitorSessionId("vs_abc12345");
        var conv = new ConversationId("c1");
        var inbox = new InboxId("email");
        var agent = new UserId("ann");
        var topics = new RealtimeTopic[]
        {
            new ConversationTopic(Scope, conv),
            new InboxQueueTopic(Scope, inbox),
            new AgentTopic(Scope, agent),
            new VisitorSessionTopic(Scope, session),
        };
        var events = new RealtimeEvent[]
        {
            new ConversationChangedEvent(Scope, conv, inbox, 3, ConversationChangeKind.EventsAppended,
                [new ConversationEventSummary(1, ConversationEventKind.Message, new MessageId("m1"), MessageDirection.Incoming)], null, null, Now),
            new PresenceChangedEvent(Scope, conv, agent, RealtimeActivity.Composing, Now),
            new ReplyLockChangedEvent(Scope, conv, agent, Now.AddMinutes(2), Now),
            new AgentStatusChangedEvent(Scope, agent, RealtimeAgentStatus.Online, Now),
            new ConversationAssignedEvent(Scope, conv, agent, 4, Now),
            new QueueChangedEvent(Scope, conv, inbox, QueueChange.Queued, Now),
            new TypingEvent(Scope, conv, new AgentActor(agent), true, Now),
            new ReadReceiptEvent(Scope, conv, VisitorActor.Instance, 5, Now),
            new VisitorMessageEvent(Scope, session, conv, new MessageId("m2"), 2, MessageDirection.Outgoing, "Hello!", MessageContentType.PlainText,
                [new AttachmentRef("s3://bucket/a.png", "a.png", "image/png", 128)], Now),
            new VisitorSessionEndedEvent(Scope, session, VisitorSessionEnd.Revoked, Now),
        };

        for (var i = 0; i < events.Length; i++)
        {
            var pub = new RealtimePublication(topics[i % topics.Length], events[i]);
            var json = RealtimePublicationEnvelope.Serialize(pub, "node-a");
            StringAssert.StartsWith(json, "{\"v\":1,", StringComparison.Ordinal);
            var decoded = RealtimePublicationEnvelope.Deserialize(json);
            Assert.AreEqual("node-a", decoded.Origin);
            Assert.AreEqual(pub.Topic, decoded.Publication.Topic);
            Assert.AreEqual(json, RealtimePublicationEnvelope.Serialize(decoded.Publication, "node-a"));
        }

        Assert.IsFalse(RealtimePublicationEnvelope.TryDeserialize("{\"v\":2}", out _, out _));
        Assert.ThrowsExactly<FormatException>(() => RealtimePublicationEnvelope.Deserialize("{\"v\":1,\"origin\":\"n\",\"topic\":{\"k\":\"unknown\",\"scope\":{\"t\":\"a\",\"p\":\"b\",\"e\":\"c\",\"a\":\"d\",\"w\":\"e\"},\"id\":\"1\"},\"event\":{\"k\":\"x\"}}"));
    }

    [TestMethod]
    public async Task WalFlowRealtimeSinkDecodesSnapshotAndStreamRowsIdempotently()
    {
        var bus = new InProcessRealtimeBus();
        var received = new List<RealtimePublication>();
        using var sub = bus.Subscribe((pub, _) => { received.Add(pub); return Task.CompletedTask; });
        var sink = new WalFlowRealtimeSink(new BusRealtimePublisher(bus), Options.Create(new WalFlowRealtimeOptions()));

        var entryId = Guid.NewGuid();
        var change = new ConversationChange(entryId, Scope, new ConversationId("c1"), new InboxId("email"), 1, ConversationChangeKind.Started,
            [new ConversationEventSummary(1, ConversationEventKind.Message, new MessageId("m1"), MessageDirection.Incoming)], null, null, Now);
        var payloadJson = ConversationJsonCodec.SerializeChangePayload(change);
        var row = new Dictionary<string, object?>
        {
            ["sequence"] = 1L,
            ["entry_id"] = entryId.ToString("D"),
            ["tenant_id"] = Scope.TenantId,
            ["project_id"] = Scope.ProjectId,
            ["environment_id"] = Scope.EnvironmentId,
            ["app_id"] = Scope.AppId,
            ["workspace_id"] = Scope.WorkspaceId.Value,
            ["conversation_id"] = "c1",
            ["inbox_id"] = "email",
            ["format_version"] = 1,
            ["kind"] = (short)ConversationChangeKind.Started,
            ["revision"] = 1L,
            ["payload"] = payloadJson,
            ["occurred_at"] = (Now.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10,
        };
        var debezium = new DebeziumPayload
        {
            Operation = "c",
            After = row,
            Source = new SourceInfo { Table = ConversationOutboxRowCodec.TableName, Schema = "public" },
        };

        await sink.SendAsync(debezium);
        await sink.SendAsync(debezium); // duplicate entry_id ignored
        await sink.SendAsync(new DebeziumPayload { Operation = "d", After = row, Source = debezium.Source }); // delete ignored
        await sink.SendAsync(new DebeziumPayload { Operation = "c", After = row, Source = new SourceInfo { Table = "other_table" } }); // other table ignored
        await sink.FlushAsync();

        Assert.AreEqual(2, received.Count, "Publishes once to conversation topic and once to inbox queue topic.");
        Assert.IsInstanceOfType<ConversationTopic>(received[0].Topic);
        Assert.IsInstanceOfType<InboxQueueTopic>(received[1].Topic);
        Assert.AreEqual(ConversationChangedEvent.From(change), received[0].Event);
    }

    [TestMethod]
    public async Task CrossNodeRealtimeBusDeliversAcrossNodesWithoutSelfEcho()
    {
        var backplane = new InMemoryRealtimeBackplane();
        using var nodeA = new CrossNodeRealtimeBus(backplane, Options.Create(new WalFlowRealtimeOptions()), nodeId: "node-a");
        using var nodeB = new CrossNodeRealtimeBus(backplane, Options.Create(new WalFlowRealtimeOptions()), nodeId: "node-b");
        var seenA = new List<RealtimePublication>();
        var seenB = new List<RealtimePublication>();
        using var subA = nodeA.Subscribe((p, _) => { seenA.Add(p); return Task.CompletedTask; });
        using var subB = nodeB.Subscribe((p, _) => { seenB.Add(p); return Task.CompletedTask; });

        var pub = new RealtimePublication(new ConversationTopic(Scope, new ConversationId("c1")),
            new ConversationAssignedEvent(Scope, new ConversationId("c1"), new UserId("ann"), 2, Now));
        await nodeA.PublishAsync(pub);

        Assert.AreEqual(1, seenA.Count, "Origin node receives once locally (no backplane echo).");
        Assert.AreEqual(1, seenB.Count, "Remote node receives once over backplane.");
        Assert.AreEqual(pub.Event, seenB[0].Event);

        var services = new ServiceCollection();
        services.AddTicketFlow().UseCrossNodeRealtimeBus();
        services.AddSingleton<IRealtimeBackplane>(backplane);
        services.AddSingleton<IRealtimePublisher, BusRealtimePublisher>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        Assert.IsInstanceOfType<CrossNodeRealtimeBus>(provider.GetRequiredService<IRealtimeBus>());
        Assert.IsNotNull(provider.GetRequiredService<WalFlowRealtimeSink>());
    }

    [TestMethod]
    public async Task PostgresRealtimeBackplaneFansOutAcrossConnections()
    {
        await using var db = await PostgresTestDatabase.CreateAsync("tf_walflow_bp_", async storage => storage);
        var channel = "tf_rt_" + Guid.NewGuid().ToString("N")[..12];
        var opts = Options.Create(new WalFlowRealtimeOptions { PostgresNotifyChannel = channel });
        await using var bpA = new PostgresRealtimeBackplane(db.First, opts);
        await using var bpB = new PostgresRealtimeBackplane(db.Second, opts);
        await bpA.StartAsync();
        await bpB.StartAsync();
        using var nodeA = new CrossNodeRealtimeBus(bpA, opts, nodeId: "pg-a");
        using var nodeB = new CrossNodeRealtimeBus(bpB, opts, nodeId: "pg-b");

        var delivered = new TaskCompletionSource<RealtimePublication>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subB = nodeB.Subscribe((p, _) => { delivered.TrySetResult(p); return Task.CompletedTask; });
        var pub = new RealtimePublication(new InboxQueueTopic(Scope, new InboxId("email")),
            new QueueChangedEvent(Scope, new ConversationId("c9"), new InboxId("email"), QueueChange.Queued, Now));
        await nodeA.PublishAsync(pub);

        var received = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(pub.Topic, received.Topic);
        Assert.AreEqual(pub.Event, received.Event);
    }

    [TestMethod]
    public async Task LogicalReplicationSlotFeedsWalFlowRealtimeSink()
    {
        await using var db = await PostgresTestDatabase.CreateAsync("tf_walflow_wal_", async storage => storage);
        string walLevel;
        await using (var cmd = db.First.CreateCommand("SHOW wal_level"))
            walLevel = (string)(await cmd.ExecuteScalarAsync())!;
        if (!string.Equals(walLevel, "logical", StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive($"PostgreSQL wal_level is '{walLevel}'; set wal_level=logical to verify logical replication CDC.");

        var store = new PostgresConversationStore(db.First);
        await store.InitializeAsync();
        var slotName = "tf_slot_" + Guid.NewGuid().ToString("N")[..12];
        await using (var createSlot = db.First.CreateCommand("SELECT pg_create_logical_replication_slot(@slot, 'test_decoding')"))
        {
            createSlot.Parameters.AddWithValue("slot", slotName);
            await createSlot.ExecuteNonQueryAsync();
        }
        try
        {
            var conv = new Conversation(new ConversationId("wal-c1"), new InboxId("email"), new ContactId("ada"), ConversationStatus.Open,
                ConversationPriority.None, null, null, [], new Dictionary<string, ConversationAttributeValue>(), [], null, Now, Now, null, null, null, null, 0);
            var change = new ConversationChange(Guid.NewGuid(), Scope, conv.Id, conv.Inbox, 1, ConversationChangeKind.Started, [], null, null, Now);
            var created = await store.WriteAsync(Scope, new ConversationWrite(conv, expectedRevision: null, change: change));
            Assert.IsInstanceOfType<ConversationWriteApplied>(created.Value);

            var bus = new InProcessRealtimeBus();
            var received = new List<RealtimePublication>();
            using var sub = bus.Subscribe((p, _) => { received.Add(p); return Task.CompletedTask; });
            var sink = new WalFlowRealtimeSink(new BusRealtimePublisher(bus), Options.Create(new WalFlowRealtimeOptions { SchemaName = db.Schema }));

            await using var readChanges = db.First.CreateCommand("SELECT data FROM pg_logical_slot_get_changes(@slot, NULL, NULL)");
            readChanges.Parameters.AddWithValue("slot", slotName);
            await using var reader = await readChanges.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var line = reader.GetString(0);
                if (!line.StartsWith($"table {db.Schema}.{ConversationOutboxRowCodec.TableName}: INSERT:", StringComparison.Ordinal))
                    continue;
                var row = ParseTestDecodingInsert(line[(line.IndexOf("INSERT:", StringComparison.Ordinal) + 7)..]);
                await sink.SendAsync(new DebeziumPayload
                {
                    Operation = "c",
                    After = row,
                    Source = new SourceInfo { Schema = db.Schema, Table = ConversationOutboxRowCodec.TableName },
                });
            }

            Assert.AreEqual(2, received.Count);
            var changed = (ConversationChangedEvent)received[0].Event;
            Assert.AreEqual(new ConversationId("wal-c1"), changed.Conversation);
            Assert.AreEqual(ConversationChangeKind.Started, changed.Kind);
        }
        finally
        {
            await using var dropSlot = db.First.CreateCommand("SELECT pg_drop_replication_slot(@slot)");
            dropSlot.Parameters.AddWithValue("slot", slotName);
            await dropSlot.ExecuteNonQueryAsync();
        }
    }

    private static Dictionary<string, object?> ParseTestDecodingInsert(string text)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (Match match in TestDecodingColumn().Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (match.Groups["quoted"].Success)
                result[name] = match.Groups["quoted"].Value.Replace("''", "'", StringComparison.Ordinal);
            else if (string.Equals(match.Groups["raw"].Value, "null", StringComparison.Ordinal))
                result[name] = null;
            else
                result[name] = match.Groups["raw"].Value;
        }
        return result;
    }

    [GeneratedRegex(@"(?<name>[a-z_]+)\[[^\]]+\]:(?:'(?<quoted>(?:''|[^'])*)'|(?<raw>[^\s]+))", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TestDecodingColumn();
}
