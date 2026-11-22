using System.Collections.Concurrent;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Conversations.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Conversations.Stores.Postgres.Tests;

/// <summary>PostgreSQL regression tests for the E5 post-ticket review findings (indexes, label side table, outbox ordering).</summary>
[TestClass]
public sealed class PostgresReviewRegressionTests
{
    private static readonly TicketScope Target = TestScopes.Create(workspaceId: "w3");

    [TestMethod]
    public async Task ListQueriesUseIndexesOverTenThousandRows()
    {
        await using var database = await PostgresConversationStoreTests.Database.CreateAsync();
        await database.ScalarAsync("""
            INSERT INTO tf_conversations(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, format_version, inbox_id, contact_id, status,
                snoozed_until, priority, assignee_id, team_id, labels, attributes, access_attributes, participants, channel_kind, channel_thread_id, created_at,
                last_activity_at, waiting_since, first_reply_at, last_incoming_at, last_outgoing_at, last_sequence, contact_redirects, revision)
            SELECT 'tenant', 'project', 'test', 'app', 'w' || (g % 10), 'c' || lpad(g::text, 6, '0'), 1, 'inbox-' || (g % 7), 'contact-' || (g % 500),
                CASE g % 4 WHEN 0 THEN 1 WHEN 1 THEN 2 WHEN 2 THEN 4 ELSE 5 END, NULL, 1,
                CASE WHEN g % 5 = 0 THEN NULL ELSE 'agent-' || (g % 20) END, 'team-' || (g % 8),
                CASE WHEN g % 10 = 3 THEN ARRAY['vip'] ELSE ARRAY['general', 'tier-' || (g % 3)] END, '{"v":1,"a":{}}', '{}', ARRAY[]::text[], NULL, NULL,
                now() - g * interval '1 second', now() - g * interval '1 second', NULL, NULL, NULL, NULL, 0, '{"v":1,"r":[]}', 1
            FROM generate_series(1, 10000) AS g;
            INSERT INTO tf_conversation_labels(scope_key, conversation_key, label, conversation_id, last_activity_at)
                SELECT c.scope_key, c.conversation_key, l.label, c.conversation_id, c.last_activity_at
                FROM tf_conversations c CROSS JOIN LATERAL unnest(c.labels) AS l(label);
            ANALYZE tf_conversations;
            ANALYZE tf_conversation_labels;
            """);
        Assert.AreEqual(10_000L, await database.ScalarAsync("SELECT count(*) FROM tf_conversations"));

        AccessFilter filter = new AndFilter(new ScopeFilter(Target), new OrFilter(new TeamInFilter([new TeamId("team-3")]), new AssigneeIsFilter(new UserId("agent-3"))));
        var cursor = new ConversationListCursor(DateTimeOffset.UtcNow.AddMinutes(-1), new ConversationId("c000060"));
        (string Name, ConversationListQuery Query, string[] Indexes)[] cases =
        [
            ("none", new(after: cursor), ["tf_conversations_activity"]),
            ("inbox", new(inbox: new InboxId("inbox-3"), after: cursor), ["tf_conversations_inbox", "tf_conversations_inbox_status"]),
            ("inbox+status", new(inbox: new InboxId("inbox-3"), status: ConversationStatusKind.Open, after: cursor), ["tf_conversations_inbox_status"]),
            ("status", new(status: ConversationStatusKind.Pending, after: cursor), ["tf_conversations_status"]),
            ("assignee", new(assignee: new UserId("agent-3"), after: cursor), ["tf_conversations_assignee", "tf_conversations_assignee_status"]),
            ("assignee+status", new(assignee: new UserId("agent-3"), status: ConversationStatusKind.Open, after: cursor), ["tf_conversations_assignee_status"]),
            ("unassigned", new(unassigned: true, after: cursor), ["tf_conversations_assignee", "tf_conversations_assignee_status"]),
            ("team", new(team: new TeamId("team-3"), after: cursor), ["tf_conversations_team", "tf_conversations_team_status"]),
            ("team+status", new(team: new TeamId("team-3"), status: ConversationStatusKind.Resolved, after: cursor), ["tf_conversations_team_status"]),
            ("contact", new(contact: new ContactId("contact-3"), after: cursor), ["tf_conversations_contact"]),
            ("label", new(label: new LabelId("vip"), after: cursor), ["tf_conversation_labels_activity"]),
        ];
        // The criterion index must drive a scope-only plan; with a selective OR access filter the planner may instead combine the
        // filter's own indexes (BitmapOr), but never falls back to a sequential scan.
        AccessFilter scopeOnly = new ScopeFilter(Target);
        await using var connection = await database.Primary.OpenConnectionAsync();
        foreach (var (name, query, indexes) in cases)
        {
            foreach (var (label, applied, expected) in new[] { ("scope", scopeOnly, indexes), ("or-filter", filter, [.. indexes, "tf_conversations_team", "tf_conversations_assignee", "tf_conversations_team_status", "tf_conversations_assignee_status"]) })
            {
                var text = await ExplainAsync(connection, database.Store.CreateListCommand(Target, query, applied));
                Assert.IsFalse(text.Contains("Seq Scan", StringComparison.Ordinal), $"{name}/{label}:\n{text}");
                var tokens = text.Split([' ', '\n', '(', ')'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                Assert.IsTrue(expected.Any(tokens.Contains), $"{name}/{label} uses one of {string.Join(", ", expected)}:\n{text}");

                // The same statement returns only rows the in-memory reference admits.
                var page = await database.Store.ListAsync(Target, query, applied);
                Assert.IsTrue(page.Conversations.All(stored => stored.Scope == Target && query.Matches(stored.Conversation) && query.IsAfterCursor(stored.Conversation)
                    && AccessFilterEvaluator.Matches(applied, ConversationAccess.Resource(stored))), $"{name}/{label}");
            }
        }
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, NpgsqlCommand list)
    {
        await using (list)
        {
            await using var explain = new NpgsqlCommand("EXPLAIN " + list.CommandText, connection);
            foreach (NpgsqlParameter parameter in list.Parameters)
                explain.Parameters.Add(parameter.Clone());
            var plan = new List<string>();
            await using var reader = await explain.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
            return string.Join("\n", plan);
        }
    }

    [TestMethod]
    public async Task LabelSideTableFollowsWritesAndDeletes()
    {
        await using var database = await PostgresConversationStoreTests.Database.CreateAsync();
        var stored = await ConversationStoreContract.CreateAsync(database.Store, Target, ConversationStoreContract.NewConversation("l1", labels: ["vip", "billing"]));
        Assert.AreEqual(2L, await database.ScalarAsync("SELECT count(*) FROM tf_conversation_labels"));
        var later = ConversationStoreContract.At.AddHours(1);
        Assert.AreEqual("applied:2", ConversationStoreContract.Describe(await ConversationStoreContract.AppendAsync(database.Competing, stored,
            conversation => conversation.With(labels: [new LabelId("vip"), new LabelId("urgent")], lastActivityAt: later),
            new LabelsChanged([new LabelId("urgent")], [new LabelId("billing")]))));
        Assert.AreEqual("urgent,vip", await database.ScalarAsync("SELECT string_agg(label, ',' ORDER BY label) FROM tf_conversation_labels"));
        Assert.AreEqual(2L, await database.ScalarAsync($"SELECT count(*) FROM tf_conversation_labels WHERE last_activity_at = '{later:O}'::timestamptz"));
        Assert.AreEqual(1, (await database.Store.ListAsync(Target, new ConversationListQuery(label: new LabelId("urgent")), AllowAllFilter.Instance)).Conversations.Count);
        Assert.AreEqual(0, (await database.Store.ListAsync(Target, new ConversationListQuery(label: new LabelId("billing")), AllowAllFilter.Instance)).Conversations.Count);
        var current = (await database.Store.GetAsync(Target, stored.Id))!;
        await database.Store.DeleteAsync(Target, stored.Id, current.Revision,
            new ConversationChange(Guid.NewGuid(), Target, stored.Id, stored.Conversation.Inbox, current.Revision, ConversationChangeKind.Deleted, [], null, null, later));
        Assert.AreEqual(0L, await database.ScalarAsync("SELECT count(*) FROM tf_conversation_labels"));
    }

    [TestMethod]
    public async Task ConcurrentClaimersKeepPerConversationOrderAndOneLiveClaim()
    {
        await using var database = await PostgresConversationStoreTests.Database.CreateAsync();
        await database.Store.RegisterConsumerAsync("automation");
        for (var conversation = 0; conversation < 10; conversation++)
        {
            var stored = await ConversationStoreContract.CreateAsync(database.Store, Target, ConversationStoreContract.NewConversation($"o{conversation}"));
            for (var entry = 1; entry < 5; entry++)
            {
                Assert.IsInstanceOfType<ConversationWriteApplied>((await ConversationStoreContract.AppendAsync(database.Store, stored, null, new SystemEvent("test.entry"))).Value);
                stored = (await database.Store.GetAsync(Target, stored.Id))!;
            }
        }
        var stores = new[] { database.Store, database.Competing };
        var live = new ConcurrentDictionary<string, long>();
        var processed = new ConcurrentDictionary<string, ConcurrentQueue<long>>();
        var acknowledged = 0;
        var violations = new ConcurrentQueue<string>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(claimer => Task.Run(async () =>
        {
            var store = stores[claimer % 2];
            for (var round = 0; round < 500 && Volatile.Read(ref acknowledged) < 50; round++)
            {
                var claims = await store.ClaimAsync("automation", 3, TimeSpan.FromMinutes(5));
                if (claims.Count == 0)
                {
                    await Task.Delay(5);
                    continue;
                }
                foreach (var claim in claims)
                {
                    var conversation = claim.Entry.Change.Conversation.Value;
                    if (!live.TryAdd(conversation, claim.Entry.Sequence))
                        violations.Enqueue($"two live claims for {conversation}");
                    processed.GetOrAdd(conversation, _ => new()).Enqueue(claim.Entry.Sequence);
                    await Task.Delay(1);
                    live.TryRemove(conversation, out _);
                    if (await store.AcknowledgeAsync("automation", claim.Entry.Sequence, claim.Fence))
                        Interlocked.Increment(ref acknowledged);
                    else
                        violations.Enqueue($"acknowledgement refused for {claim.Entry.Sequence}");
                }
            }
        })));
        Assert.IsEmpty(violations, string.Join("; ", violations));
        Assert.AreEqual(50, acknowledged);
        Assert.AreEqual(10, processed.Count);
        foreach (var (conversation, sequences) in processed)
        {
            var order = sequences.ToArray();
            Assert.AreEqual(5, order.Length, conversation);
            CollectionAssert.AreEqual(order.Order().ToArray(), order, $"{conversation} processed in entry order");
        }
    }
}
