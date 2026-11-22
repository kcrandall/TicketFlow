using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Tests;

[TestClass]
public sealed class OutboxDispatcherTests
{
    private sealed class Handler(string consumer, Func<ConversationOutboxEntry, bool> succeed) : IConversationOutboxHandler
    {
        public List<long> Seen { get; } = [];

        public string Consumer => consumer;

        public Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
        {
            Seen.Add(entry.Sequence);
            return succeed(entry) ? Task.CompletedTask : throw new InvalidOperationException("handler failed");
        }
    }

    [TestMethod]
    public async Task HandlersAreAcknowledgedRetriedAndDeadLettered()
    {
        var clock = TestClock.Create();
        var store = new InMemoryConversationStore(clock);
        var good = new Handler("realtime", _ => true);
        var bad = new Handler("reporting", _ => false);
        var dispatcher = new ConversationOutboxDispatcher(store, [good, bad],
            Options.Create(new ConversationOutboxDispatcherOptions { MaximumAttempts = 2, InitialRetryDelay = TimeSpan.FromSeconds(10) }), clock);
        await dispatcher.RegisterAsync();
        var scope = TestScopes.Create();
        await StoreContracts.ConversationStoreContract.CreateAsync(store, scope, StoreContracts.ConversationStoreContract.NewConversation("a"));
        await StoreContracts.ConversationStoreContract.CreateAsync(store, scope, StoreContracts.ConversationStoreContract.NewConversation("b"));

        Assert.AreEqual(new ConversationOutboxDispatchResult(2, 2, 0, 0), await dispatcher.RunOnceAsync());
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 0, 0), await dispatcher.RunOnceAsync(), "retries wait for their backoff");
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 2, 0), await dispatcher.RunOnceAsync());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 0, 0), await dispatcher.RunOnceAsync());
        Assert.AreEqual(2, good.Seen.Count);
        Assert.AreEqual(4, bad.Seen.Count);
        Assert.AreEqual(2, await store.PruneAsync(clock.GetUtcNow(), 10));
    }

    [TestMethod]
    public void HandlerNamesMustBeValidAndUnique()
    {
        var store = new InMemoryConversationStore();
        var options = Options.Create(new ConversationOutboxDispatcherOptions());
        Assert.ThrowsExactly<ArgumentException>(() => new ConversationOutboxDispatcher(store, [new Handler("a", _ => true), new Handler("a", _ => true)], options, TimeProvider.System));
        Assert.ThrowsExactly<ArgumentException>(() => new ConversationOutboxDispatcher(store, [new Handler("Bad Name", _ => true)], options, TimeProvider.System));
        Assert.ThrowsExactly<InvalidOperationException>(() => new ConversationOutboxDispatcher(store, [],
            Options.Create(new ConversationOutboxDispatcherOptions { BatchSize = 0 }), TimeProvider.System));
    }

    private sealed class BatchHandler(string consumer, Func<IReadOnlyList<ConversationOutboxEntry>, bool> succeed) : IConversationOutboxBatchHandler
    {
        public List<IReadOnlyList<long>> Batches { get; } = [];
        public string Consumer => consumer;
        public Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default) => HandleBatchAsync([entry], cancellationToken);

        public Task HandleBatchAsync(IReadOnlyList<ConversationOutboxEntry> entries, CancellationToken cancellationToken = default)
        {
            Batches.Add([.. entries.Select(entry => entry.Sequence)]);
            return succeed(entries) ? Task.CompletedTask : throw new InvalidOperationException("batch failed");
        }
    }

    [TestMethod]
    public async Task BatchHandlerReceivesAllEligibleClaimsInOneCallAndRetriesOnFailure()
    {
        var clock = TestClock.Create();
        var store = new InMemoryConversationStore(clock);
        var ok = new BatchHandler("events", _ => true);
        var fail = new BatchHandler("failing", _ => false);
        var dispatcher = new ConversationOutboxDispatcher(store, [ok, fail],
            Options.Create(new ConversationOutboxDispatcherOptions { MaximumAttempts = 2, InitialRetryDelay = TimeSpan.FromSeconds(10) }), clock);
        await dispatcher.RegisterAsync();
        var scope = TestScopes.Create();
        await StoreContracts.ConversationStoreContract.CreateAsync(store, scope, StoreContracts.ConversationStoreContract.NewConversation("a"));
        await StoreContracts.ConversationStoreContract.CreateAsync(store, scope, StoreContracts.ConversationStoreContract.NewConversation("b"));
        await StoreContracts.ConversationStoreContract.CreateAsync(store, scope, StoreContracts.ConversationStoreContract.NewConversation("c"));

        Assert.AreEqual(new ConversationOutboxDispatchResult(3, 3, 0, 0), await dispatcher.RunOnceAsync());
        Assert.AreEqual(1, ok.Batches.Count);
        Assert.AreEqual(3, ok.Batches[0].Count);
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 3, 0), await dispatcher.RunOnceAsync());
        Assert.AreEqual(2, fail.Batches.Count);
    }

    [TestMethod]
    public void OutboxRowCodecDecodesSnapshotAndStreamingRowTypes()
    {
        var scope = TestScopes.Create(workspaceId: "ws");
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var change = new ConversationChange(id, scope, new ConversationId("c1"), new InboxId("email"), 2, ConversationChangeKind.EventsAppended,
            [new ConversationEventSummary(1, ConversationEventKind.Message, new Core.MessageId("m1"), MessageDirection.Incoming)], null, null, at);
        var payload = ConversationJsonCodec.SerializeChangePayload(change);

        // Snapshot-style CLR primitives
        var clrRow = new Dictionary<string, object?>
        {
            ["sequence"] = 7L,
            ["entry_id"] = id,
            ["tenant_id"] = scope.TenantId,
            ["project_id"] = scope.ProjectId,
            ["environment_id"] = scope.EnvironmentId,
            ["app_id"] = scope.AppId,
            ["workspace_id"] = scope.WorkspaceId.Value,
            ["conversation_id"] = "c1",
            ["inbox_id"] = "email",
            ["format_version"] = (short)1,
            ["kind"] = (short)ConversationChangeKind.EventsAppended,
            ["revision"] = 2L,
            ["payload"] = payload,
            ["occurred_at"] = at,
        };
        var decodedClr = ConversationOutboxRowCodec.Decode(clrRow);
        Assert.AreEqual(7L, decodedClr.Sequence);
        Assert.AreEqual(change, decodedClr.Change);

        // Streaming-style JSON elements + Unix microseconds
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sequence"] = "7",
            ["entry_id"] = id.ToString("D"),
            ["tenant_id"] = scope.TenantId,
            ["project_id"] = scope.ProjectId,
            ["environment_id"] = scope.EnvironmentId,
            ["app_id"] = scope.AppId,
            ["workspace_id"] = scope.WorkspaceId.Value,
            ["conversation_id"] = "c1",
            ["inbox_id"] = "email",
            ["format_version"] = 1,
            ["kind"] = "2",
            ["revision"] = "2",
            ["payload"] = payload,
            ["occurred_at"] = (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10,
        }));
        var jsonRow = jsonDoc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var decodedJson = ConversationOutboxRowCodec.Decode(jsonRow);
        Assert.AreEqual(decodedClr, decodedJson);

        clrRow["format_version"] = 2;
        Assert.IsFalse(ConversationOutboxRowCodec.TryDecode(clrRow, out _, out _));
        Assert.ThrowsExactly<FormatException>(() => ConversationOutboxRowCodec.Decode(clrRow));
    }
}
