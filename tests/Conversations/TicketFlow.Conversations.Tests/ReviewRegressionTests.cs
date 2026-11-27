using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Tests;

/// <summary>Regression tests for the E5 post-ticket review findings.</summary>
[TestClass]
public sealed class ReviewRegressionTests
{
    private static readonly ConversationId C1 = new("c1");

    [TestMethod]
    public async Task CommandResultsDoNotLeakMaskedAttributeValues()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.DefineAsync(new ConversationAttributeDefinition("ssn", ConversationAttributeType.Text, sensitive: true));
        await fixture.DefineAsync(new ConversationAttributeDefinition("plan", ConversationAttributeType.Text));
        await fixture.StartAsync("c1");
        var set = ConversationFixture.Applied(await fixture.Conversations.UpdateAttributesAsync(fixture.Owner, C1, new Dictionary<string, ConversationAttributeValue?>
        {
            ["ssn"] = new ConversationTextValue("123-45-6789"),
            ["plan"] = new ConversationTextValue("pro"),
        }));
        Assert.AreEqual(new AttributeChanged("plan", null, new ConversationTextValue("pro")), set.Events.Single(item => item.Event.Value is AttributeChanged { Key: "plan" }).Event.Value);
        Assert.AreEqual(new AttributeChanged("ssn", null, null), set.Events.Single(item => item.Event.Value is AttributeChanged { Key: "ssn" }).Event.Value,
            "a sensitive attribute without a policy is masked even in the writer's own result");

        var cleared = ConversationFixture.Applied(await fixture.Conversations.UpdateAttributesAsync(fixture.Agent, C1,
            new Dictionary<string, ConversationAttributeValue?> { ["ssn"] = null }));
        var change = (AttributeChanged)cleared.Events.Single().Event.Value!;
        Assert.IsNull(change.Previous, "the previous sensitive value is not disclosed to the agent");
        Assert.IsNull(change.Current);
        Assert.IsFalse((await fixture.Store.GetAsync(fixture.Scope, C1))!.Conversation.Attributes.ContainsKey("ssn"), "the removal itself was applied");
    }

    [TestMethod]
    public async Task ReplayedInboundMessagesHideChannelIdentifiersFromActorsWithoutRead()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var started = await fixture.StartAsync("c1", externalId: "<m1@example.com>");
        Assert.IsNull(((MessageEvent)started.Events.Single().Event.Value!).ExternalId, "the channel client lacks conversation:read, so the result is fully masked");
        var replay = ConversationFixture.Applied(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("x", ExternalId: "<m1@example.com>")));
        Assert.IsTrue(replay.Replayed);
        Assert.IsNull(((MessageEvent)replay.Events.Single().Event.Value!).ExternalId);
        Assert.AreEqual(((MessageEvent)started.Events.Single().Event.Value!).Id, ((MessageEvent)replay.Events.Single().Event.Value!).Id);
    }

    [TestMethod]
    public async Task RedeliveredMessageAfterCloseIsReplayedNotRefused()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.StartAsync("c1", externalId: "ext-1");
        ConversationFixture.Applied(await fixture.Conversations.ChangeStatusAsync(fixture.Agent, C1, ConversationStatus.Closed));
        Assert.AreEqual("replayed:2", ConversationFixture.Describe(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("Hi", ExternalId: "ext-1"))));
        Assert.AreEqual("refused:Closed", ConversationFixture.Describe(await fixture.Conversations.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("Hi", ExternalId: "ext-2"))));
    }

    [TestMethod]
    public async Task MergeRepointsConversationsAcrossManyPages()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        for (var index = 0; index < 130; index++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.StartAsync($"g{index:D3}", contact: "grace");
        }
        await MergeGraceIntoAdaAsync(fixture);
        Assert.AreEqual(0, await CountAsync(fixture.Store, fixture.Scope, "grace"));
        Assert.AreEqual(130, await CountAsync(fixture.Store, fixture.Scope, "ada"));
    }

    [TestMethod]
    public async Task ActivityDuringTheWalkCannotHideAConversation()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var store = new HookedStore(new InMemoryConversationStore(fixture.Clock));
        for (var index = 0; index < 150; index++)
            await ConversationStoreContract.CreateAsync(store, fixture.Scope, ConversationStoreContract.NewConversation($"g{index:D3}", contact: "grace",
                activity: ConversationStoreContract.At.AddMinutes(index)));
        // After the first page, the oldest conversation (on the last page) becomes the most recently active one.
        store.AfterList = async () =>
        {
            store.AfterList = null;
            var oldest = (await store.Inner.GetAsync(fixture.Scope, new ConversationId("g000")))!;
            await ConversationStoreContract.AppendAsync(store.Inner, oldest, conversation => conversation.With(lastActivityAt: ConversationStoreContract.At.AddDays(1)),
                new SystemEvent("test.activity"));
        };
        await MergeGraceIntoAdaAsync(fixture);
        var follower = Follower(fixture, store);
        await follower.CatchUpAsync(fixture.Scope, 0);
        Assert.AreEqual(0, await CountAsync(store, fixture.Scope, "grace"));
        Assert.AreEqual(150, await CountAsync(store, fixture.Scope, "ada"));
        Assert.IsTrue(store.ListCalls > 2, "a second pass verified that nothing was left");
    }

    [TestMethod]
    public async Task ObserverFailuresAreSwallowedAndCatchUpRepairs()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        var store = new HookedStore(new InMemoryConversationStore(fixture.Clock));
        for (var index = 0; index < 3; index++)
            await ConversationStoreContract.CreateAsync(store, fixture.Scope, ConversationStoreContract.NewConversation($"g{index}", contact: "grace"));
        await MergeGraceIntoAdaAsync(fixture);
        var changes = await fixture.Provider.GetRequiredService<IContactStore>().ReadChangesAsync(fixture.Scope, 0, 100);
        var follower = Follower(fixture, store);
        store.FailLists = true;
        await follower.OnChangedAsync(changes);
        Assert.AreEqual(3, await CountAsync(store.Inner, fixture.Scope, "grace"), "the failed in-process attempt changed nothing and did not throw");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => follower.CatchUpAsync(fixture.Scope, 0), "the durable path reports failures");
        store.FailLists = false;
        await follower.CatchUpAsync(fixture.Scope, 0);
        Assert.AreEqual(3, await CountAsync(store, fixture.Scope, "ada"));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => follower.OnChangedAsync(changes, cancelled.Token), "cancellation is not swallowed");
        await Assert.ThrowsAsync<OperationCanceledException>(() => follower.CatchUpAsync(fixture.Scope, 0, cancellationToken: cancelled.Token));
    }

    [TestMethod]
    public async Task StartRacingAMergeIsRepointed()
    {
        StaleContactStore? stale = null;
        using var fixture = await ConversationFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton(provider => stale = new StaleContactStore(provider.GetRequiredService<InMemoryContactStore>()));
            services.AddSingleton<IContactStore>(provider => provider.GetRequiredService<StaleContactStore>());
            services.AddSingleton<InMemoryContactStore>();
        });
        var contacts = fixture.Provider.GetRequiredService<IContactStore>();
        var before = (await contacts.GetAsync(fixture.Scope, new ContactId("grace")))!;
        var mergeId = await MergeGraceIntoAdaAsync(fixture);
        stale!.StaleOnce = before;
        var started = ConversationFixture.Applied(await fixture.Conversations.StartAsync(fixture.Channel,
            new StartConversationRequest(new InboxId("email"), new ContactId("grace"), Id: new ConversationId("late"))));
        Assert.AreEqual(2L, started.Revision, "the start was followed by a repair write");
        var stored = (await fixture.Store.GetAsync(fixture.Scope, new ConversationId("late")))!;
        Assert.AreEqual(new ContactId("ada"), stored.Conversation.Contact);
        Assert.AreEqual(mergeId, stored.Conversation.ContactRedirects.Single().Merge);
    }

    [TestMethod]
    public async Task UnauthorizedCallersCannotProbeConversationRevisionsThroughMutateOrDelete()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.StartAsync("c1");

        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(
            await fixture.Conversations.DeleteAsync(fixture.Viewer, C1, expectedRevision: 99)));
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(
            await fixture.Conversations.ChangeStatusAsync(fixture.Viewer, C1, ConversationStatus.Closed, expectedRevision: 99)));
        Assert.AreEqual("refused:Forbidden", ConversationFixture.Describe(
            await fixture.Conversations.UpdateLabelsAsync(fixture.Viewer, C1, add: [new LabelId("vip")], remove: [], expectedRevision: 99)));

        // Authorized caller with stale revision still observes Conflict.
        Assert.AreEqual("conflict:1", ConversationFixture.Describe(
            await fixture.Conversations.DeleteAsync(fixture.Owner, C1, expectedRevision: 99)));
        Assert.AreEqual("conflict:1", ConversationFixture.Describe(
            await fixture.Conversations.ChangeStatusAsync(fixture.Agent, C1, ConversationStatus.Closed, expectedRevision: 99)));
    }

    [TestMethod]
    public async Task DispatcherDeadLettersPoisonEntriesWithoutHandlingThem()
    {
        var clock = TestClock.Create();
        var store = new InMemoryConversationStore(clock);
        var calls = 0;
        var handler = new DelegateHandler("reporting", _ =>
        {
            calls++;
            return Task.CompletedTask;
        });
        var dispatcher = new ConversationOutboxDispatcher(store, [handler],
            Options.Create(new ConversationOutboxDispatcherOptions { MaximumAttempts = 2, Lease = TimeSpan.FromMinutes(1) }), clock);
        await dispatcher.RegisterAsync();
        await ConversationStoreContract.CreateAsync(store, TestScopes.Create(), ConversationStoreContract.NewConversation("poison"));
        // Two earlier claims never completed (a handler hung or the process crashed) and their leases expired.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.AreEqual(1, (await store.ClaimAsync("reporting", 10, TimeSpan.FromSeconds(10))).Count);
            clock.Advance(TimeSpan.FromSeconds(11));
        }
        Assert.AreEqual(new ConversationOutboxDispatchResult(0, 0, 1, 0), await dispatcher.RunOnceAsync());
        Assert.AreEqual(0, calls);
        Assert.AreEqual(1, await store.PruneAsync(clock.GetUtcNow().AddDays(1), 10));
    }

    [TestMethod]
    public async Task DispatcherSkipsClaimsWhoseLeaseIsAboutToExpire()
    {
        var clock = TestClock.Create();
        var store = new InMemoryConversationStore(clock);
        var seen = new List<string>();
        var handler = new DelegateHandler("realtime", entry =>
        {
            seen.Add(entry.Change.Conversation.Value);
            clock.Advance(TimeSpan.FromSeconds(6));
            return Task.CompletedTask;
        });
        var dispatcher = new ConversationOutboxDispatcher(store, [handler],
            Options.Create(new ConversationOutboxDispatcherOptions { Lease = TimeSpan.FromSeconds(10), LeaseSafetyMargin = TimeSpan.FromSeconds(5) }), clock);
        await dispatcher.RegisterAsync();
        var scope = TestScopes.Create();
        await ConversationStoreContract.CreateAsync(store, scope, ConversationStoreContract.NewConversation("a"));
        await ConversationStoreContract.CreateAsync(store, scope, ConversationStoreContract.NewConversation("b"));
        Assert.AreEqual(new ConversationOutboxDispatchResult(1, 0, 0, 0, Skipped: 1), await dispatcher.RunOnceAsync());
        CollectionAssert.AreEqual(new[] { "a" }, seen);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual(new ConversationOutboxDispatchResult(1, 0, 0, 0), await dispatcher.RunOnceAsync());
        CollectionAssert.AreEqual(new[] { "a", "b" }, seen);
        Assert.ThrowsExactly<InvalidOperationException>(() => new ConversationOutboxDispatcher(store, [],
            Options.Create(new ConversationOutboxDispatcherOptions { Lease = TimeSpan.FromSeconds(5), LeaseSafetyMargin = TimeSpan.FromSeconds(5) }), clock));
    }

    [TestMethod]
    public async Task ListPagesLoadAccessDataOncePerPage()
    {
        var grants = new CountingGrantStore(new InMemoryAccessGrantStore());
        var policies = new CountingPolicyStore(new InMemoryAccessFieldPolicyStore());
        using var fixture = await ConversationFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton<IAccessGrantStore>(grants);
            services.AddSingleton<IAccessFieldPolicyStore>(policies);
        });
        for (var index = 0; index < 60; index++)
            await fixture.StartAsync($"c{index:D2}");

        async Task<(int Grants, int Policies)> CallsForPageAsync(int limit)
        {
            var (grantsBefore, policiesBefore) = (grants.Loads, policies.Lists);
            Assert.AreEqual(limit, ConversationFixture.List(await fixture.Conversations.ListAsync(fixture.Agent, new ConversationListQuery(limit: limit))).Conversations.Count);
            return (grants.Loads - grantsBefore, policies.Lists - policiesBefore);
        }
        var small = await CallsForPageAsync(5);
        var large = await CallsForPageAsync(50);
        Assert.AreEqual(small, large, "access store round trips do not grow with the page size");
        Assert.IsTrue(large.Grants <= 2 && large.Policies <= 2, $"grants {large.Grants}, policies {large.Policies}");

        var (grantsBefore, _) = (grants.Loads, policies.Lists);
        ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Agent, new ConversationId("c00")));
        Assert.AreEqual(1, grants.Loads - grantsBefore, "read and note:read are decided from one snapshot");
    }

    private static async Task<Guid> MergeGraceIntoAdaAsync(ConversationFixture fixture)
    {
        var merge = await fixture.ContactsService.MergeAsync(fixture.Owner, new ContactMergeRequest(new ContactId("ada"), 1, new ContactId("grace"), 1));
        return ((TicketValue<ContactMergeOutcome>)merge.Value!).Value.MergeId;
    }

    private static ConversationContactMergeFollower Follower(ConversationFixture fixture, IConversationStore store) =>
        new(store, fixture.Provider.GetRequiredService<IContactStore>(), Options.Create(new ConversationsOptions()), fixture.Clock);

    private static async Task<int> CountAsync(IConversationStore store, TicketScope scope, string contact)
    {
        var count = 0;
        var query = new ConversationListQuery(contact: new ContactId(contact), limit: 100);
        while (true)
        {
            var page = await store.ListAsync(scope, query, AllowAllFilter.Instance);
            count += page.Conversations.Count;
            if (page.Next is null)
                return count;
            query = query.WithAfter(page.Next);
        }
    }

    private sealed class DelegateHandler(string consumer, Func<ConversationOutboxEntry, Task> handle) : IConversationOutboxHandler
    {
        public string Consumer => consumer;

        public Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default) => handle(entry);
    }

    /// <summary>Delegating store with hooks on list calls.</summary>
    private sealed class HookedStore(InMemoryConversationStore inner) : IConversationStore
    {
        public InMemoryConversationStore Inner { get; } = inner;
        public Func<Task>? AfterList { get; set; }
        public bool FailLists { get; set; }
        public int ListCalls { get; private set; }

        public async Task<ConversationPage> ListAsync(TicketScope scope, ConversationListQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
        {
            if (FailLists)
                throw new InvalidOperationException("injected store failure");
            ListCalls++;
            var page = await Inner.ListAsync(scope, query, filter, cancellationToken);
            if (AfterList is { } hook)
                await hook();
            return page;
        }

        public Task<StoredConversation?> GetAsync(TicketScope scope, ConversationId id, CancellationToken cancellationToken = default) => Inner.GetAsync(scope, id, cancellationToken);

        public Task<ConversationWriteResult> WriteAsync(TicketScope scope, ConversationWrite write, CancellationToken cancellationToken = default) => Inner.WriteAsync(scope, write, cancellationToken);

        public Task<ConversationWriteResult> DeleteAsync(TicketScope scope, ConversationId id, long expectedRevision, ConversationChange change, CancellationToken cancellationToken = default) =>
            Inner.DeleteAsync(scope, id, expectedRevision, change, cancellationToken);

        public Task<IReadOnlyList<TimelineEvent>> ReadTimelineAsync(TicketScope scope, ConversationId id, long afterSequence, int limit, bool includeNotes, CancellationToken cancellationToken = default) =>
            Inner.ReadTimelineAsync(scope, id, afterSequence, limit, includeNotes, cancellationToken);

        public Task<StoredMessage?> GetMessageAsync(TicketScope scope, ConversationId conversation, MessageId message, CancellationToken cancellationToken = default) =>
            Inner.GetMessageAsync(scope, conversation, message, cancellationToken);

        public Task<StoredMessage?> FindMessageByExternalIdAsync(TicketScope scope, InboxId inbox, string externalId, CancellationToken cancellationToken = default) =>
            Inner.FindMessageByExternalIdAsync(scope, inbox, externalId, cancellationToken);

        public Task<MessageDeliveryWriteResult> UpdateDeliveryAsync(TicketScope scope, MessageDeliveryWrite write, CancellationToken cancellationToken = default) =>
            Inner.UpdateDeliveryAsync(scope, write, cancellationToken);

        public Task<StoredConversation?> FindByThreadAsync(TicketScope scope, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default) =>
            Inner.FindByThreadAsync(scope, inbox, thread, cancellationToken);
    }

    /// <summary>Returns one stale (pre-merge) snapshot to the next read of that contact, simulating a start racing a merge.</summary>
    private sealed class StaleContactStore(InMemoryContactStore inner) : IContactStore
    {
        public StoredContact? StaleOnce { get; set; }

        public Task<StoredContact?> GetAsync(TicketScope scope, ContactId id, CancellationToken cancellationToken = default)
        {
            if (StaleOnce is { } stale && stale.Id == id)
            {
                StaleOnce = null;
                return Task.FromResult<StoredContact?>(stale);
            }
            return inner.GetAsync(scope, id, cancellationToken);
        }

        public Task<IReadOnlyList<ContactIdentityOwner>> FindByIdentityAsync(TicketScope scope, ContactIdentityAddress address, CancellationToken cancellationToken = default) =>
            inner.FindByIdentityAsync(scope, address, cancellationToken);

        public Task<ContactPage> SearchAsync(TicketScope scope, ContactSearch search, AccessFilter filter, CancellationToken cancellationToken = default) =>
            inner.SearchAsync(scope, search, filter, cancellationToken);

        public Task<IReadOnlyList<ContactId>> ListMergedIntoAsync(TicketScope scope, ContactId target, int limit, CancellationToken cancellationToken = default) =>
            inner.ListMergedIntoAsync(scope, target, limit, cancellationToken);

        public Task<ContactWriteResult> WriteAsync(TicketScope scope, ContactWriteBatch batch, CancellationToken cancellationToken = default) => inner.WriteAsync(scope, batch, cancellationToken);

        public Task<ContactMergeRecord?> GetMergeAsync(TicketScope scope, Guid mergeId, CancellationToken cancellationToken = default) => inner.GetMergeAsync(scope, mergeId, cancellationToken);

        public Task<IReadOnlyList<ContactChange>> ReadChangesAsync(TicketScope scope, long afterSequence, int limit, CancellationToken cancellationToken = default) =>
            inner.ReadChangesAsync(scope, afterSequence, limit, cancellationToken);
    }

    private sealed class CountingGrantStore(IAccessGrantStore inner) : IAccessGrantStore
    {
        private int loads;

        public int Loads => Volatile.Read(ref loads);

        public Task<StoredAccessGrant?> GetAsync(AccessPartition partition, GrantId id, CancellationToken cancellationToken = default) => inner.GetAsync(partition, id, cancellationToken);

        public Task<IReadOnlyList<StoredAccessGrant>> ListForSubjectsAsync(AccessPartition partition, WorkspaceId workspace, IReadOnlyCollection<GrantSubject> subjects, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref loads);
            return inner.ListForSubjectsAsync(partition, workspace, subjects, cancellationToken);
        }

        public Task<IReadOnlyList<StoredAccessGrant>> ListForTargetAsync(AccessPartition partition, GrantScope target, int limit, GrantId? after = null, CancellationToken cancellationToken = default) =>
            inner.ListForTargetAsync(partition, target, limit, after, cancellationToken);

        public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> CreateAsync(AccessGrant grant, CancellationToken cancellationToken = default) => inner.CreateAsync(grant, cancellationToken);

        public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> UpdateAsync(AccessGrant grant, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(grant, expectedRevision, cancellationToken);

        public Task<StoreWriteResult<StoredAccessGrant, AccessWriteRejection>> DeleteAsync(AccessPartition partition, GrantId id, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(partition, id, expectedRevision, cancellationToken);
    }

    private sealed class CountingPolicyStore(IAccessFieldPolicyStore inner) : IAccessFieldPolicyStore
    {
        private int lists;

        public int Lists => Volatile.Read(ref lists);

        public Task<StoredAccessFieldPolicy?> GetAsync(TicketScope scope, FieldPath field, CancellationToken cancellationToken = default) => inner.GetAsync(scope, field, cancellationToken);

        public Task<IReadOnlyList<StoredAccessFieldPolicy>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref lists);
            return inner.ListAsync(scope, cancellationToken);
        }

        public Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> CreateAsync(TicketScope scope, AccessFieldPolicy policy, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(scope, policy, cancellationToken);

        public Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> UpdateAsync(TicketScope scope, AccessFieldPolicy policy, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(scope, policy, expectedRevision, cancellationToken);

        public Task<StoreWriteResult<StoredAccessFieldPolicy, AccessWriteRejection>> DeleteAsync(TicketScope scope, FieldPath field, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(scope, field, expectedRevision, cancellationToken);
    }
}
