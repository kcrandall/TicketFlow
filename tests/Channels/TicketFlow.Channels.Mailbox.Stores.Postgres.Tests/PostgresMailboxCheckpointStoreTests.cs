using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Channels.Mailbox.Stores.Postgres.Tests;

[TestClass]
public sealed class PostgresMailboxCheckpointStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CheckpointsAndMessageIdDedupePersistAcrossScopesAndPrune()
    {
        await using var db = await PostgresTestDatabase.CreateAsync("tf_mailbox_test_", async storage => storage);
        var store = new PostgresMailboxCheckpointStore(db.First);
        await store.InitializeAsync();
        await store.InitializeAsync(); // idempotent

        var scopeA = TestScopes.Create(workspaceId: "ws-a");
        var scopeB = TestScopes.Create(workspaceId: "ws-b");

        Assert.IsNull(await store.GetCheckpointAsync(scopeA, "imap-1", "INBOX"));
        await store.SaveCheckpointAsync(scopeA, "imap-1", "INBOX", new MailboxCheckpoint(42, 100, Now));
        var loaded = await store.GetCheckpointAsync(scopeA, "imap-1", "INBOX");
        Assert.AreEqual(new MailboxCheckpoint(42, 100, Now), loaded);
        Assert.IsNull(await store.GetCheckpointAsync(scopeB, "imap-1", "INBOX"), "Scopes are isolated.");

        // UIDVALIDITY update overwrites cleanly.
        await store.SaveCheckpointAsync(scopeA, "imap-1", "INBOX", new MailboxCheckpoint(99, 5, Now.AddMinutes(1)));
        Assert.AreEqual(new MailboxCheckpoint(99, 5, Now.AddMinutes(1)), await store.GetCheckpointAsync(scopeA, "imap-1", "INBOX"));

        // Message-Id deduplication is atomic and scoped.
        Assert.IsTrue(await store.TryRecordMessageIdAsync(scopeA, "imap-1", "INBOX", "msg-1@example.com", Now));
        Assert.IsFalse(await store.TryRecordMessageIdAsync(scopeA, "imap-1", "INBOX", "msg-1@example.com", Now));
        Assert.IsTrue(await store.HasSeenMessageIdAsync(scopeA, "imap-1", "INBOX", "msg-1@example.com"));
        Assert.IsFalse(await store.HasSeenMessageIdAsync(scopeB, "imap-1", "INBOX", "msg-1@example.com"));
        Assert.IsTrue(await store.TryRecordMessageIdAsync(scopeA, "imap-1", "INBOX", "msg-2@example.com", Now.AddDays(5)));

        // Prune removes only old entries.
        Assert.AreEqual(1, await store.PruneSeenMessageIdsAsync(Now.AddDays(1), 100));
        Assert.IsFalse(await store.HasSeenMessageIdAsync(scopeA, "imap-1", "INBOX", "msg-1@example.com"));
        Assert.IsTrue(await store.HasSeenMessageIdAsync(scopeA, "imap-1", "INBOX", "msg-2@example.com"));
    }
}
