using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.StoreContracts;

/// <summary>
/// Behavioral contract every routing store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the same
/// storage (for example two data sources) sharing the supplied <see cref="FakeTimeProvider"/>, so atomicity and fencing are verified
/// across clients.
/// </summary>
public static class RoutingStoreContract
{
    public static readonly ChannelKind Email = new("email");
    public static readonly ChannelKind Chat = new("live-chat");

    public static string Describe<T>(StoreWriteResult<T, RoutingWriteRejection> result) => result switch
    {
        StoreApplied<T> => "applied",
        StoreNotFound => "not-found",
        StoreConflict conflict => $"conflict:{conflict.CurrentRevision}",
        StoreRejected<RoutingWriteRejection> rejected => $"rejected:{rejected.Reason}",
    };

    public static T Applied<T>(StoreWriteResult<T, RoutingWriteRejection> result) => result switch
    {
        StoreApplied<T> applied => applied.Value,
        StoreNotFound => throw new AssertFailedException("Expected applied but was not found."),
        StoreConflict conflict => throw new AssertFailedException($"Expected applied but was conflict:{conflict.CurrentRevision}."),
        StoreRejected<RoutingWriteRejection> rejected => throw new AssertFailedException($"Expected applied but was rejected:{rejected.Reason}."),
    };

    public static string Describe(RoutingReservationResult result) => result switch
    {
        RoutingReserved reserved => $"reserved:{reserved.Assignment.Agent}:{reserved.Load}",
        RoutingNoCapacity => "no-capacity",
        RoutingAlreadyReserved existing => $"already:{existing.Existing.Agent}",
    };

    public static RoutingReservationRequest Request(string conversation, IEnumerable<(string Agent, int Capacity)> candidates, RoutingSelection selection = RoutingSelection.RoundRobin,
        string cursor = "email", string? preferred = null, long revision = 1, ChannelKind? channel = null, string contact = "ada", TimeSpan? lease = null) =>
        new(new ConversationId(conversation), revision, new InboxId("email"), new ContactId(contact), channel ?? Email, cursor, selection,
            candidates.Select(candidate => new RoutingCandidate(new UserId(candidate.Agent), candidate.Capacity)), preferred is null ? null : new UserId(preferred),
            lease ?? TimeSpan.FromSeconds(30));

    public static RoutingQueueSnapshot Snapshot(string conversation, DateTimeOffset waitingSince, string inbox = "email", string? team = null, long revision = 1,
        IEnumerable<string>? labels = null, IEnumerable<KeyValuePair<string, string>>? attributes = null) =>
        new(new ConversationId(conversation), new InboxId(inbox), team is null ? null : new TeamId(team), new ContactId("ada"),
            (labels ?? []).Select(label => new LabelId(label)), attributes ?? [], waitingSince, revision);

    public static async Task VerifyPoliciesAsync(IRoutingPolicyStore primary, IRoutingPolicyStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "policies");
        var policy = new RoutingPolicy(new RoutingPolicyId("default"), "Default", new StickyOwnerStrategy(TimeSpan.FromDays(7), RoutingStrategy.LeastBusy),
            new TeamId("billing"), [new OverflowRule(TimeSpan.FromMinutes(5), new OverflowToTeam(new TeamId("tier2"))),
                new OverflowRule(TimeSpan.FromMinutes(10), new OverflowToInbox(new InboxId("overflow")))], TimeSpan.FromMinutes(30), OfflineReassignment.ReturnToQueue);
        var created = Applied(await primary.CreateAsync(scope, policy));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(policy, created.Policy);
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(scope, policy)));
        Assert.AreEqual(policy, (await competing.GetAsync(scope, policy.Id))!.Policy);
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await competing.GetAsync(other, policy.Id));

        var skills = new RoutingPolicy(policy.Id, "Skills", new SkillsBasedStrategy([
            new SkillRule(new LabelSkillMatch(new LabelId("vip")), "billing", 3),
            new SkillRule(new AttributeSkillMatch("language", "de"), "german")]));
        Assert.AreEqual("conflict:1", Describe(await competing.UpdateAsync(scope, skills, 5)));
        var updated = Applied(await competing.UpdateAsync(scope, skills, 1));
        Assert.AreEqual(2, updated.Revision);
        Assert.AreEqual(skills, (await primary.GetAsync(scope, policy.Id))!.Policy);
        Assert.AreEqual("not-found", Describe(await primary.UpdateAsync(scope, new RoutingPolicy(new RoutingPolicyId("missing"), "x", RoutingStrategy.Manual), 1)));

        Applied(await primary.CreateAsync(scope, new RoutingPolicy(new RoutingPolicyId("a-first"), "First", RoutingStrategy.RoundRobin)));
        CollectionAssert.AreEqual(new[] { "a-first", "default" }, (await competing.ListAsync(scope)).Select(item => item.Policy.Id.Value).ToArray());
        Assert.AreEqual("conflict:2", Describe(await primary.DeleteAsync(scope, policy.Id, 1)));
        Applied(await competing.DeleteAsync(scope, policy.Id, 2));
        Assert.IsNull(await primary.GetAsync(scope, policy.Id));
        Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(scope, policy.Id, 2)));
    }

    public static async Task VerifyAgentsAsync(IAgentAvailabilityStore primary, IAgentAvailabilityStore competing, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "agents");
        var ttl = TimeSpan.FromMinutes(2);
        var settings = new AgentRoutingSettings(new UserId("ada"), 3, [new(Chat, 5)], [new AgentSkill("billing", 4), new AgentSkill("german")]);
        var created = Applied(await primary.SaveSettingsAsync(scope, settings, null));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(AgentStatus.Offline, created.Presence.Status);
        Assert.AreEqual(settings, created.Settings);
        Assert.IsFalse(created.ReassignmentPending);
        Assert.AreEqual("conflict:1", Describe(await competing.SaveSettingsAsync(scope, settings, null)));
        Assert.AreEqual("not-found", Describe(await competing.SaveSettingsAsync(scope, new AgentRoutingSettings(new UserId("nobody"), 1), 1)));
        Assert.AreEqual("not-found", Describe(await competing.SetStatusAsync(scope, new UserId("nobody"), AgentStatus.Online, ttl)));
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await competing.GetAsync(other, settings.Agent));

        Assert.AreEqual(0, (await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Email, limit: 10))).Count);
        var online = Applied(await competing.SetStatusAsync(scope, settings.Agent, AgentStatus.Online, ttl));
        Assert.AreEqual(2, online.Revision);
        Assert.AreEqual(clock.GetUtcNow() + ttl, online.Presence.ExpiresAt);
        Assert.AreEqual("conflict:2", Describe(await primary.SaveSettingsAsync(scope, settings, 1)));
        var reconfigured = Applied(await primary.SaveSettingsAsync(scope, new AgentRoutingSettings(settings.Agent, 2), 2));
        Assert.AreEqual(AgentStatus.Online, reconfigured.Presence.Status);
        Assert.AreEqual(2, reconfigured.Settings.DefaultCapacity);
        CollectionAssert.AreEqual(new[] { "ada" }, (await primary.ListAvailableAsync(scope, new AvailableAgentQuery(Email, limit: 10))).Select(item => item.Agent.Value).ToArray());

        clock.Advance(TimeSpan.FromMinutes(1));
        var beat = Applied(await primary.HeartbeatAsync(scope, settings.Agent, ttl));
        Assert.AreEqual(reconfigured.Revision, beat.Revision);
        Assert.AreEqual(clock.GetUtcNow() + ttl, beat.Presence.ExpiresAt);
        Assert.AreEqual(clock.GetUtcNow(), beat.Presence.LastHeartbeatAt);

        // Lapsed presence is effectively offline, never revived by heartbeats, and recorded by the expiry sweep.
        clock.Advance(ttl);
        Assert.AreEqual(0, (await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Email, limit: 10))).Count);
        Assert.AreEqual(AgentStatus.Online, Applied(await competing.HeartbeatAsync(scope, settings.Agent, ttl)).Presence.Status);
        Assert.AreEqual(0, (await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Email, limit: 10))).Count);
        var expired = await primary.ExpirePresenceAsync(100);
        Assert.AreEqual(1, expired.Count(item => item.Scope == scope && item.Agent == settings.Agent));
        Assert.AreEqual(0, (await competing.ExpirePresenceAsync(100)).Count(item => item.Scope == scope));
        var offline = (await competing.GetAsync(scope, settings.Agent))!;
        Assert.AreEqual(AgentStatus.Offline, offline.Presence.Status);
        Assert.IsTrue(offline.ReassignmentPending);

        // Reassignment claims are exclusive and fenced.
        var claims = await Task.WhenAll(primary.ClaimReassignmentsAsync(10, TimeSpan.FromMinutes(1)), competing.ClaimReassignmentsAsync(10, TimeSpan.FromMinutes(1)));
        var mine = claims.SelectMany(batch => batch).Where(claim => claim.Scope == scope).ToList();
        Assert.AreEqual(1, mine.Count);
        Assert.IsFalse(await competing.CompleteReassignmentAsync(scope, settings.Agent, mine[0].Fence + 1));
        clock.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = (await competing.ClaimReassignmentsAsync(10, TimeSpan.FromMinutes(1))).Single(claim => claim.Scope == scope);
        Assert.IsTrue(reclaimed.Fence > mine[0].Fence);
        Assert.IsFalse(await primary.CompleteReassignmentAsync(scope, settings.Agent, mine[0].Fence));
        Assert.IsTrue(await primary.CompleteReassignmentAsync(scope, settings.Agent, reclaimed.Fence));
        Assert.IsFalse((await primary.GetAsync(scope, settings.Agent))!.ReassignmentPending);

        // Explicit offline from online sets the flag; offline to offline does not.
        Applied(await primary.SetStatusAsync(scope, settings.Agent, AgentStatus.Offline, ttl));
        Assert.IsFalse((await primary.GetAsync(scope, settings.Agent))!.ReassignmentPending);
        Applied(await primary.SetStatusAsync(scope, settings.Agent, AgentStatus.Busy, ttl));
        Assert.AreEqual(0, (await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Email, limit: 10))).Count);
        Assert.IsTrue(Applied(await competing.SetStatusAsync(scope, settings.Agent, AgentStatus.Offline, ttl)).ReassignmentPending);

        foreach (var name in new[] { "dan", "cy", "bo" })
            Applied(await competing.SaveSettingsAsync(scope, new AgentRoutingSettings(new UserId(name), 1), null));
        CollectionAssert.AreEqual(new[] { "ada", "bo" }, (await primary.ListAsync(scope, null, 2)).Select(item => item.Agent.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "cy", "dan" }, (await primary.ListAsync(scope, new UserId("bo"), 5)).Select(item => item.Agent.Value).ToArray());
        var current = (await primary.GetAsync(scope, new UserId("dan")))!;
        Assert.AreEqual("conflict:1", Describe(await primary.DeleteAsync(scope, current.Agent, 9)));
        Applied(await competing.DeleteAsync(scope, current.Agent, 1));
        Assert.IsNull(await primary.GetAsync(scope, current.Agent));
    }

    /// <summary>Availability filters (agent set, capacity per channel, skills) run in the store query and page by agent identifier.</summary>
    public static async Task VerifyAvailableFiltersAsync(IAgentAvailabilityStore primary, IAgentAvailabilityStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "available");
        var ttl = TimeSpan.FromMinutes(5);
        async Task OnlineAsync(AgentRoutingSettings settings, AgentStatus status = AgentStatus.Online)
        {
            Applied(await primary.SaveSettingsAsync(scope, settings, null));
            Applied(await primary.SetStatusAsync(scope, settings.Agent, status, ttl));
        }
        await OnlineAsync(new AgentRoutingSettings(new UserId("a1"), 2, skills: [new AgentSkill("billing", 5), new AgentSkill("german", 2)]));
        await OnlineAsync(new AgentRoutingSettings(new UserId("a2"), 2, [new(Chat, 0)], [new AgentSkill("billing", 2)]));
        await OnlineAsync(new AgentRoutingSettings(new UserId("a3"), 0, [new(Chat, 4)]));
        await OnlineAsync(new AgentRoutingSettings(new UserId("a4"), 1, skills: [new AgentSkill("billing", 9)]), AgentStatus.Away);
        for (var index = 0; index < 5; index++)
            await OnlineAsync(new AgentRoutingSettings(new UserId($"b{index}"), 1));

        string[] Ids(IReadOnlyList<StoredAgentProfile> page) => [.. page.Select(profile => profile.Agent.Value)];
        CollectionAssert.AreEqual(new[] { "a1", "a2", "b0", "b1", "b2", "b3", "b4" }, Ids(await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Email))));
        CollectionAssert.AreEqual(new[] { "a1", "a3", "b0", "b1", "b2", "b3", "b4" }, Ids(await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Chat))));
        CollectionAssert.AreEqual(new[] { "a1", "a2" }, Ids(await competing.ListAvailableAsync(scope,
            new AvailableAgentQuery(Email, requiredSkills: new Dictionary<string, int> { ["billing"] = 2 }))));
        CollectionAssert.AreEqual(new[] { "a1" }, Ids(await competing.ListAvailableAsync(scope,
            new AvailableAgentQuery(Email, requiredSkills: new Dictionary<string, int> { ["billing"] = 3, ["german"] = 1 }))));
        CollectionAssert.AreEqual(new[] { "a2", "b3" }, Ids(await competing.ListAvailableAsync(scope,
            new AvailableAgentQuery(Email, [new UserId("b3"), new UserId("a2"), new UserId("a3"), new UserId("a4"), new UserId("zz")]))));
        Assert.AreEqual(0, (await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Email, []))).Count);
        var first = await competing.ListAvailableAsync(scope, new AvailableAgentQuery(Email, limit: 3));
        CollectionAssert.AreEqual(new[] { "a1", "a2", "b0" }, Ids(first));
        var next = new AvailableAgentQuery(Email, limit: 3).WithAfter(first[^1].Agent);
        CollectionAssert.AreEqual(new[] { "b1", "b2", "b3" }, Ids(await primary.ListAvailableAsync(scope, next)));
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.AreEqual(0, (await competing.ListAvailableAsync(other, new AvailableAgentQuery(Email))).Count);
    }

    public static async Task VerifyAssignmentsAsync(IRoutingAssignmentStore primary, IRoutingAssignmentStore competing, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "assignments");
        (string, int)[] trio = [("ann", 2), ("bob", 2), ("cat", 2)];

        // Round robin follows identifier order after the cursor and wraps.
        var order = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var store = index % 2 == 0 ? primary : competing;
            var result = await store.ReserveAsync(scope, Request($"rr-{index}", trio));
            var reserved = Assert.IsInstanceOfType<RoutingReserved>(result.Value);
            order.Add(reserved.Assignment.Agent.Value);
            Assert.IsTrue(await store.ConfirmAsync(scope, reserved.Assignment.Conversation, reserved.Assignment.Agent, 2));
        }
        CollectionAssert.AreEqual(new[] { "ann", "bob", "cat", "ann" }, order);
        Assert.AreEqual("ann", await competing.GetCursorAsync(scope, "email"));
        Assert.IsNull(await competing.GetCursorAsync(scope, "never-used"));
        var loads = await primary.GetLoadAsync(scope, new UserId("ann"));
        Assert.AreEqual(2, loads[Email]);

        // Capacity: ann is full, so the rotation skips her; then everyone is full.
        Assert.AreEqual("reserved:bob:2", Describe(await competing.ReserveAsync(scope, Request("rr-4", trio))));
        Assert.AreEqual("reserved:cat:2", Describe(await primary.ReserveAsync(scope, Request("rr-5", trio))));
        Assert.AreEqual("no-capacity", Describe(await primary.ReserveAsync(scope, Request("rr-6", trio))));
        Assert.AreEqual("already:bob", Describe(await primary.ReserveAsync(scope, Request("rr-4", trio))));
        // Other channel kinds have their own load.
        Assert.AreEqual("reserved:ann:1", Describe(await primary.ReserveAsync(scope, Request("chat-1", trio, channel: Chat, cursor: "chat"))));

        // Releasing a reservation frees capacity; an expired reservation stops counting and can be replaced.
        Assert.IsFalse(await primary.ReleaseAsync(scope, new ConversationId("rr-4"), new UserId("cat")));
        Assert.IsTrue(await primary.ReleaseAsync(scope, new ConversationId("rr-4"), new UserId("bob")));
        Assert.IsNull(await competing.GetAsync(scope, new ConversationId("rr-4")));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.AreEqual(1, (await primary.GetLoadAsync(scope, new UserId("bob")))[Email]);
        Assert.AreEqual("reserved:bob:2", Describe(await competing.ReserveAsync(scope, Request("rr-5", trio))));

        // Least busy is capacity-aware: dee (0 of 4) beats everyone else.
        (string, int)[] wide = [("ann", 2), ("bob", 2), ("cat", 2), ("dee", 4)];
        Assert.AreEqual("reserved:dee:1", Describe(await primary.ReserveAsync(scope, Request("lb-1", wide, RoutingSelection.LeastBusy, cursor: "lb"))));
        // Preferred agent wins while under capacity, otherwise selection falls back.
        Assert.AreEqual("reserved:dee:2", Describe(await primary.ReserveAsync(scope, Request("lb-2", wide, RoutingSelection.RoundRobin, cursor: "lb", preferred: "dee"))));
        Assert.AreEqual("reserved:cat:2", Describe(await competing.ReserveAsync(scope, Request("lb-3", wide, RoutingSelection.RoundRobin, cursor: "lb", preferred: "ann"))));

        // Recorded assignments ignore capacity, set the sticky owner and are revision guarded.
        var record = new RoutingAssignmentRecord(new ConversationId("manual-1"), 5, new UserId("ann"), new InboxId("email"), new ContactId("grace"), Email);
        await primary.RecordAssignedAsync(scope, record);
        Assert.AreEqual(3, (await primary.GetLoadAsync(scope, new UserId("ann")))[Email]);
        Assert.AreEqual(new UserId("ann"), (await competing.GetStickyOwnerAsync(scope, new ContactId("grace")))!.Agent);
        await competing.RecordAssignedAsync(scope, new RoutingAssignmentRecord(record.Conversation, 4, new UserId("bob"), record.Inbox, record.Contact, Email));
        Assert.AreEqual(new UserId("ann"), (await primary.GetAsync(scope, record.Conversation))!.Agent);
        Assert.AreEqual("already:ann", Describe(await primary.ReserveAsync(scope, Request("manual-1", wide, revision: 4))));
        Assert.IsFalse(await primary.ClearAsync(scope, record.Conversation, 4));
        Assert.IsTrue(await competing.ClearAsync(scope, record.Conversation, 6));
        Assert.IsNull(await primary.GetAsync(scope, record.Conversation));
        // Live reservations are never cleared.
        Assert.IsFalse(await primary.ClearAsync(scope, new ConversationId("lb-3"), long.MaxValue));

        var annRows = await competing.ListForAgentAsync(scope, new UserId("ann"), null, 2);
        CollectionAssert.AreEqual(new[] { "chat-1", "rr-0" }, annRows.Select(row => row.Conversation.Value).ToArray());
        Assert.AreEqual(RoutingAssignmentState.Reserved, annRows[0].State);
        Assert.AreEqual(RoutingAssignmentState.Assigned, annRows[1].State);
        CollectionAssert.AreEqual(new[] { "rr-3" }, (await competing.ListForAgentAsync(scope, new UserId("ann"), new ConversationId("rr-0"), 5))
            .Select(row => row.Conversation.Value).ToArray());
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await competing.GetAsync(other, new ConversationId("rr-0")));
    }

    /// <summary>Concurrent reservations across clients never exceed capacity and give each conversation one live reservation.</summary>
    public static async Task VerifyReservationRacesAsync(IRoutingAssignmentStore primary, IRoutingAssignmentStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "races");
        (string, int)[] agents = [("a1", 3), ("a2", 3), ("a3", 3), ("a4", 3)];
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            (index % 2 == 0 ? primary : competing).ReserveAsync(scope, Request($"race-{index}", agents, index % 3 == 0 ? RoutingSelection.LeastBusy : RoutingSelection.RoundRobin)))));
        Assert.AreEqual(12, results.Count(result => result.Value is RoutingReserved));
        Assert.AreEqual(28, results.Count(result => result.Value is RoutingNoCapacity));
        foreach (var (agent, capacity) in agents)
            Assert.AreEqual(capacity, (await primary.GetLoadAsync(scope, new UserId(agent))).GetValueOrDefault(Email));

        var same = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            (index % 2 == 0 ? primary : competing).ReserveAsync(scope, Request("same", [("b1", 10), ("b2", 10)])))));
        Assert.AreEqual(1, same.Count(result => result.Value is RoutingReserved));
        Assert.AreEqual(11, same.Count(result => result.Value is RoutingAlreadyReserved));
    }

    public static async Task VerifyQueueAsync(IRoutingQueueStore primary, IRoutingQueueStore competing, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "queue");
        var start = clock.GetUtcNow();
        var first = await primary.EnqueueAsync(scope, Snapshot("q-1", start.AddMinutes(-5), team: "billing", labels: ["vip"], attributes: [new("tier", "gold")]));
        Assert.AreEqual(new InboxId("email"), first.Entry.TargetInbox);
        Assert.AreEqual(start, first.Entry.QueuedAt);
        Assert.AreEqual(start, first.Entry.NextAttemptAt);
        await competing.EnqueueAsync(scope, Snapshot("q-2", start.AddMinutes(-9), inbox: "chat"));
        await competing.EnqueueAsync(scope, Snapshot("q-3", start.AddMinutes(-1)));
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await competing.GetAsync(other, new ConversationId("q-1")));

        // Refresh keeps queue position; older snapshots are ignored.
        clock.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await competing.EnqueueAsync(scope, Snapshot("q-1", start.AddMinutes(-5), team: "sales", revision: 3, labels: ["vip"]));
        Assert.AreEqual(start, refreshed.Entry.QueuedAt);
        Assert.AreEqual(new TeamId("sales"), refreshed.Entry.Snapshot.Team);
        Assert.AreEqual(new TeamId("sales"), (await primary.EnqueueAsync(scope, Snapshot("q-1", start, team: "billing", revision: 2))).Entry.Snapshot.Team);

        // Lists: oldest waiting first, keyset paged, Access filter parity.
        var all = await primary.ListAsync(scope, new RoutingQueueQuery(limit: 2), AllowAllFilter.Instance);
        CollectionAssert.AreEqual(new[] { "q-2", "q-1" }, all.Entries.Select(item => item.Entry.Conversation.Value).ToArray());
        var rest = await competing.ListAsync(scope, new RoutingQueueQuery(limit: 2, after: all.Next), AllowAllFilter.Instance);
        CollectionAssert.AreEqual(new[] { "q-3" }, rest.Entries.Select(item => item.Entry.Conversation.Value).ToArray());
        Assert.IsNull(rest.Next);
        CollectionAssert.AreEqual(new[] { "q-1", "q-3" }, (await competing.ListAsync(scope, new RoutingQueueQuery(inbox: new InboxId("email")), AllowAllFilter.Instance))
            .Entries.Select(item => item.Entry.Conversation.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "q-1" }, (await competing.ListAsync(scope, new RoutingQueueQuery(team: new TeamId("sales")), AllowAllFilter.Instance))
            .Entries.Select(item => item.Entry.Conversation.Value).ToArray());
        AccessFilter[] filters =
        [
            DenyAllFilter.Instance,
            new InboxInFilter([new InboxId("chat")]),
            new HasLabelFilter(new LabelId("vip")),
            new NotFilter(new HasLabelFilter(new LabelId("vip"))),
            new TeamInFilter([new TeamId("sales")]),
            new AndFilter([new ScopeFilter(scope), new NotFilter(new TeamInFilter([new TeamId("sales")]))]),
            new AssigneeIsFilter(new UserId("ann")),
            new NotFilter(new AttributeEqualsFilter("tier", "gold")),
        ];
        var entries = (await primary.ListAsync(scope, new RoutingQueueQuery(limit: 100), AllowAllFilter.Instance)).Entries;
        foreach (var filter in filters)
        {
            var expected = entries.Where(item => AccessFilterEvaluator.Matches(filter, item.Entry.Resource(scope))).Select(item => item.Entry.Conversation.Value).ToArray();
            var actual = (await competing.ListAsync(scope, new RoutingQueueQuery(limit: 100), filter)).Entries.Select(item => item.Entry.Conversation.Value).ToArray();
            CollectionAssert.AreEqual(expected, actual, filter.GetType().Name);
        }

        // Claims are exclusive, fenced and leased.
        var claims = await Task.WhenAll(primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1)), competing.ClaimDueAsync(10, TimeSpan.FromMinutes(1)));
        var mine = claims.SelectMany(batch => batch).Where(claim => claim.Scope == scope).ToList();
        Assert.AreEqual(3, mine.Count);
        Assert.AreEqual(3, mine.Select(claim => claim.Entry.Conversation).Distinct().Count());
        Assert.AreEqual(0, (await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Count(claim => claim.Scope == scope));
        var claim1 = mine.Single(claim => claim.Entry.Conversation.Value == "q-1");
        var later = clock.GetUtcNow().AddMinutes(5);
        Assert.IsFalse(await competing.CompleteAttemptAsync(scope, claim1.Entry.Conversation, claim1.Fence + 1000, new RoutingQueueUpdate(later, new InboxId("email"), 0, false)));
        Assert.IsTrue(await competing.CompleteAttemptAsync(scope, claim1.Entry.Conversation, claim1.Fence, new RoutingQueueUpdate(later, new InboxId("overflow"), 1, true)));
        Assert.IsFalse(await primary.CompleteAttemptAsync(scope, claim1.Entry.Conversation, claim1.Fence, new RoutingQueueUpdate(later, new InboxId("email"), 0, false)));
        var updated = (await primary.GetAsync(scope, claim1.Entry.Conversation))!.Entry;
        Assert.AreEqual(new InboxId("overflow"), updated.TargetInbox);
        Assert.AreEqual(1, updated.OverflowStage);
        Assert.IsTrue(updated.WaitExceeded);
        Assert.AreEqual(1, updated.Attempts);
        Assert.AreEqual(later, updated.NextAttemptAt);

        // Expired leases are reclaimable with a higher fence; nudging makes entries due now.
        clock.Advance(TimeSpan.FromMinutes(2));
        var reclaimed = (await competing.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Where(claim => claim.Scope == scope).ToList();
        CollectionAssert.AreEquivalent(new[] { "q-2", "q-3" }, reclaimed.Select(claim => claim.Entry.Conversation.Value).ToArray());
        Assert.IsTrue(reclaimed.All(claim => claim.Fence > mine.Max(item => item.Fence) || mine.All(item => item.Entry.Conversation != claim.Entry.Conversation || item.Fence < claim.Fence)));
        Assert.AreEqual(1, await primary.NudgeAsync(scope, new InboxId("overflow")));
        Assert.AreEqual("q-1", (await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Single(claim => claim.Scope == scope).Entry.Conversation.Value);

        // Removal is revision guarded.
        Assert.IsFalse(await primary.RemoveAsync(scope, new ConversationId("q-1"), 2));
        Assert.IsTrue(await competing.RemoveAsync(scope, new ConversationId("q-1"), 3));
        Assert.IsNull(await primary.GetAsync(scope, new ConversationId("q-1")));
        Assert.IsFalse(await primary.RemoveAsync(scope, new ConversationId("q-1"), long.MaxValue));

        // Claims are taken in next-attempt order (index-backed), then returned oldest waiting first.
        Assert.IsTrue(await primary.RemoveAsync(scope, new ConversationId("q-2"), long.MaxValue));
        Assert.IsTrue(await primary.RemoveAsync(scope, new ConversationId("q-3"), long.MaxValue));
        var order = TestScopes.Create(workspaceId: "queue-order");
        var now = clock.GetUtcNow();
        await primary.EnqueueAsync(order, Snapshot("old", now.AddMinutes(-30)));
        await primary.EnqueueAsync(order, Snapshot("new", now.AddMinutes(-1)));
        await primary.EnqueueAsync(order, Snapshot("mid", now.AddMinutes(-10)));
        foreach (var claim in await primary.ClaimDueAsync(100, TimeSpan.FromMinutes(1)))
        {
            var next = claim.Entry.Conversation.Value switch { "old" => now.AddMinutes(1), "new" => now.AddSeconds(30), _ => now.AddSeconds(45) };
            Assert.IsTrue(await competing.CompleteAttemptAsync(order, claim.Entry.Conversation, claim.Fence, new RoutingQueueUpdate(next, new InboxId("email"), 0, false)));
        }
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual("new", (await competing.ClaimDueAsync(1, TimeSpan.FromMinutes(1))).Single().Entry.Conversation.Value);
        CollectionAssert.AreEqual(new[] { "old", "mid" }, (await primary.ClaimDueAsync(10, TimeSpan.FromMinutes(1))).Select(claim => claim.Entry.Conversation.Value).ToArray());
    }

    public static async Task VerifyPresenceAsync(IConversationPresenceStore primary, IConversationPresenceStore competing, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "presence");
        var conversation = new ConversationId("p-1");
        var ttl = TimeSpan.FromSeconds(30);
        var touched = Assert.IsInstanceOfType<PresenceTouched>((await primary.TouchAsync(scope, conversation, new UserId("bob"), ConversationActivity.Viewing, ttl)).Value);
        Assert.IsTrue(touched.Changed);
        clock.Advance(TimeSpan.FromSeconds(10));
        var again = Assert.IsInstanceOfType<PresenceTouched>((await competing.TouchAsync(scope, conversation, new UserId("bob"), ConversationActivity.Viewing, ttl)).Value);
        Assert.IsFalse(again.Changed);
        Assert.AreEqual(touched.Presence.Since, again.Presence.Since);
        Assert.IsTrue((Assert.IsInstanceOfType<PresenceTouched>((await competing.TouchAsync(scope, conversation, new UserId("bob"), ConversationActivity.Composing, ttl)).Value)).Changed);
        await primary.TouchAsync(scope, conversation, new UserId("ann"), ConversationActivity.Viewing, ttl);
        var listed = await competing.ListAsync(scope, conversation);
        CollectionAssert.AreEqual(new[] { "ann", "bob" }, listed.Select(item => item.User.Value).ToArray());
        Assert.AreEqual(ConversationActivity.Composing, listed[1].Activity);
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.AreEqual(0, (await competing.ListAsync(other, conversation)).Count);
        Assert.IsTrue(await competing.LeaveAsync(scope, conversation, new UserId("ann")));
        Assert.IsFalse(await primary.LeaveAsync(scope, conversation, new UserId("ann")));
        clock.Advance(ttl);
        Assert.AreEqual(0, (await primary.ListAsync(scope, conversation)).Count);

        var crowded = new ConversationId("p-crowded");
        for (var index = 0; index < IConversationPresenceStore.MaximumPresencePerConversation; index++)
            Assert.IsInstanceOfType<PresenceTouched>((await primary.TouchAsync(scope, crowded, new UserId($"u{index:D2}"), ConversationActivity.Viewing, ttl)).Value);
        Assert.IsInstanceOfType<PresenceCapacityExceeded>((await competing.TouchAsync(scope, crowded, new UserId("late"), ConversationActivity.Viewing, ttl)).Value);
        Assert.IsInstanceOfType<PresenceTouched>((await competing.TouchAsync(scope, crowded, new UserId("u00"), ConversationActivity.Composing, ttl)).Value);

        // Reply locks: exclusive, renewable by the holder, fenced release, expiry.
        var locked = Assert.IsInstanceOfType<ReplyLockAcquired>((await primary.AcquireLockAsync(scope, conversation, new UserId("bob"), TimeSpan.FromMinutes(2))).Value);
        Assert.IsFalse(locked.Renewed);
        var held = Assert.IsInstanceOfType<ReplyLockHeld>((await competing.AcquireLockAsync(scope, conversation, new UserId("ann"), TimeSpan.FromMinutes(2))).Value);
        Assert.AreEqual(new UserId("bob"), held.Current.Holder);
        var renewed = Assert.IsInstanceOfType<ReplyLockAcquired>((await competing.AcquireLockAsync(scope, conversation, new UserId("bob"), TimeSpan.FromMinutes(2))).Value);
        Assert.IsTrue(renewed.Renewed);
        Assert.IsTrue(renewed.Lock.Fence > locked.Lock.Fence);
        Assert.IsFalse(await primary.ReleaseLockAsync(scope, conversation, new UserId("bob"), locked.Lock.Fence));
        Assert.IsFalse(await primary.ReleaseLockAsync(scope, conversation, new UserId("ann"), renewed.Lock.Fence));
        Assert.AreEqual(new UserId("bob"), (await competing.GetLockAsync(scope, conversation))!.Holder);
        Assert.IsTrue(await primary.ReleaseLockAsync(scope, conversation, new UserId("bob"), renewed.Lock.Fence));
        Assert.IsNull(await competing.GetLockAsync(scope, conversation));
        var annLock = Assert.IsInstanceOfType<ReplyLockAcquired>((await competing.AcquireLockAsync(scope, conversation, new UserId("ann"), TimeSpan.FromMinutes(1))).Value);
        Assert.IsTrue(annLock.Lock.Fence > renewed.Lock.Fence);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsNull(await primary.GetLockAsync(scope, conversation));
        Assert.IsInstanceOfType<ReplyLockAcquired>((await primary.AcquireLockAsync(scope, conversation, new UserId("bob"), TimeSpan.FromMinutes(1))).Value);

        var racing = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Task.Run(() =>
            (index % 2 == 0 ? primary : competing).AcquireLockAsync(scope, new ConversationId("p-race"), new UserId($"r{index}"), TimeSpan.FromMinutes(1)))));
        Assert.AreEqual(1, racing.Count(result => result.Value is ReplyLockAcquired));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.IsTrue(await primary.PruneAsync(IConversationPresenceStore.MaximumPrune) >= IConversationPresenceStore.MaximumPresencePerConversation);
        Assert.AreEqual(0, (await competing.ListAsync(scope, crowded)).Count);
    }
}
