using System.Security.Cryptography;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Realtime;
using TicketFlow.Testing;

namespace TicketFlow.Channels.LiveChat.StoreContracts;

/// <summary>
/// Shared contract checks for the live-chat stores. <c>competing</c> is a second, independent client of the same storage (for
/// PostgreSQL a second data source; for in-memory the same instance).
/// </summary>
public static class LiveChatStoreContract
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    public static LiveChatWidget Widget(string configuration = "cfg-chat", string inbox = "chat-inbox") => new(configuration, new InboxId(inbox),
        ["https://shop.example.com", "http://localhost:5173"], "theme-dark",
        [new LiveChatPrechatField("name", "Your name", PrechatFieldKind.Name, required: true), new LiveChatPrechatField("email", "Email", PrechatFieldKind.Email),
            new LiveChatPrechatField("order", "Order number", PrechatFieldKind.Text, maximumLength: 32, sensitive: true)],
        "Hi! How can we help?", "We are away", new LiveChatDeflection(new PortalId("help"), new HelpCenterLocale("en"), 3),
        LiveChatAttachmentPolicy.Default, new LiveChatIdentityPolicy("shop", allowHmac: true, allowSignedToken: false, requireVerified: false));

    public static VisitorSession Session(string id = "vs_contract", string configuration = "cfg-chat", DateTimeOffset? expiresAt = null) =>
        new(new VisitorSessionId(id), configuration, new InboxId("chat-inbox"), "v_" + id, SHA256.HashData([1, 2, 3]), Now, Now, expiresAt ?? Now.AddDays(30));

    public static async Task VerifyWidgetsAsync(ILiveChatWidgetStore store, ILiveChatWidgetStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "livechat-widgets");
        var widget = Widget();
        var created = Applied(await store.CreateAsync(scope, widget));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(widget, created.Widget);
        Assert.AreEqual(1L, Is<LiveChatWriteConflict>((await competing.CreateAsync(scope, widget)).Value).CurrentRevision);
        Assert.AreEqual(created, await competing.GetAsync(scope, "cfg-chat"));
        foreach (var variant in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await store.GetAsync(variant, "cfg-chat"));

        var minimal = new LiveChatWidget("cfg-chat", new InboxId("chat-inbox-2"), ["https://other.example"]);
        Assert.AreEqual(1L, Is<LiveChatWriteConflict>((await store.UpdateAsync(scope, minimal, 5)).Value).CurrentRevision);
        Assert.IsInstanceOfType<LiveChatWriteNotFound>((await store.UpdateAsync(scope, new LiveChatWidget("missing", new InboxId("x"), ["https://a.example"]), 1)).Value);
        var updated = Applied(await competing.UpdateAsync(scope, minimal, 1));
        Assert.AreEqual(2, updated.Revision);
        Assert.AreEqual(minimal, (await store.GetAsync(scope, "cfg-chat"))!.Widget);

        Assert.AreEqual(2L, Is<LiveChatWriteConflict>((await store.DeleteAsync(scope, "cfg-chat", 1)).Value).CurrentRevision);
        Applied(await store.DeleteAsync(scope, "cfg-chat", 2));
        Assert.IsNull(await competing.GetAsync(scope, "cfg-chat"));
        Assert.IsInstanceOfType<LiveChatWriteNotFound>((await store.DeleteAsync(scope, "cfg-chat", 2)).Value);
    }

    public static async Task VerifySessionsAsync(ILiveChatSessionStore store, ILiveChatSessionStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "livechat-sessions");
        var session = Session();
        var created = Applied(await store.CreateAsync(scope, session));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(session, created.Session);
        Assert.AreEqual(1L, Is<LiveChatWriteConflict>((await competing.CreateAsync(scope, session)).Value).CurrentRevision);
        Assert.AreEqual(1L, Is<LiveChatWriteConflict>((await competing.CreateAsync(TestScopes.Create(workspaceId: "livechat-elsewhere"), session)).Value).CurrentRevision,
            "Session ids are unique per partition, across workspaces.");

        var found = await competing.GetAsync(AccessPartition.From(scope), session.Id);
        Assert.IsNotNull(found);
        Assert.AreEqual(scope, found.Scope);
        Assert.AreEqual(session, found.Session);
        Assert.IsNull(await store.GetAsync(AccessPartition.From(TestScopes.Create(tenantId: "other-tenant")), session.Id));
        Assert.IsNull(await store.GetAsync(AccessPartition.From(scope), new VisitorSessionId("vs_missing")));

        var identity = new VerifiedVisitorIdentity("shop", "user-42", VisitorVerificationMethod.Hmac, Now.AddMinutes(1));
        var next = session.With(tokenHash: SHA256.HashData([9]), tokenIssuedAt: Now.AddMinutes(2), expiresAt: Now.AddDays(31),
            previous: (session.TokenHash, Now.AddMinutes(4)), identity: identity, displayName: "Ada", prechat: new Dictionary<string, string> { ["name"] = "Ada", ["order"] = "42" });
        Assert.AreEqual(1L, Is<LiveChatWriteConflict>((await store.UpdateAsync(scope, next, 3)).Value).CurrentRevision);
        Assert.IsInstanceOfType<LiveChatWriteNotFound>((await store.UpdateAsync(TestScopes.Create(workspaceId: "livechat-elsewhere"), next, 1)).Value);
        var updated = Applied(await competing.UpdateAsync(scope, next, 1));
        Assert.AreEqual(2, updated.Revision);
        Assert.AreEqual(next, (await store.GetAsync(AccessPartition.From(scope), session.Id))!.Session);
        var revoked = Applied(await store.UpdateAsync(scope, next.With(revoked: true), 2));
        Assert.IsTrue(revoked.Session.Revoked);

        // Links and cursors.
        var conversation = new ConversationId("conv-1");
        Assert.IsInstanceOfType<LiveChatWriteNotFound>((await store.LinkAsync(scope, new VisitorSessionId("vs_missing"), conversation, Now)).Value);
        var link = Applied(await store.LinkAsync(scope, session.Id, conversation, Now));
        Assert.AreEqual(0, link.ReadSequence);
        Assert.AreEqual(link, Applied(await competing.LinkAsync(scope, session.Id, conversation, Now.AddMinutes(5))), "Links are idempotent.");
        Applied(await store.LinkAsync(scope, session.Id, new ConversationId("conv-2"), Now.AddMinutes(1)));
        CollectionAssert.AreEqual(new[] { "conv-2", "conv-1" }, (await competing.ListLinksAsync(scope, session.Id)).Select(item => item.Conversation.Value).ToArray());
        Assert.IsNull(await store.GetLinkAsync(TestScopes.Create(workspaceId: "livechat-elsewhere"), session.Id, conversation));
        Assert.IsNull(await store.GetLinkAsync(scope, session.Id, new ConversationId("conv-3")));

        Assert.AreEqual(7, Applied(await store.AdvanceCursorAsync(scope, session.Id, conversation, 7)).ReadSequence);
        Assert.AreEqual(7, Applied(await competing.AdvanceCursorAsync(scope, session.Id, conversation, 3)).ReadSequence, "Cursors never move back.");
        Assert.AreEqual(7, (await store.GetLinkAsync(scope, session.Id, conversation))!.ReadSequence);
        Assert.IsInstanceOfType<LiveChatWriteNotFound>((await store.AdvanceCursorAsync(scope, session.Id, new ConversationId("conv-3"), 1)).Value);

        var second = Session("vs_contract_2");
        Applied(await store.CreateAsync(scope, second));
        Applied(await store.LinkAsync(scope, second.Id, conversation, Now.AddMinutes(9)));
        CollectionAssert.AreEqual(new[] { second.Id, session.Id }, (await competing.FindSessionsAsync(scope, conversation, 32)).ToArray());
        CollectionAssert.AreEqual(new[] { second.Id }, (await store.FindSessionsAsync(scope, conversation, 1)).ToArray());
        Assert.IsEmpty(await store.FindSessionsAsync(TestScopes.Create(workspaceId: "livechat-elsewhere"), conversation, 32));

        // Capacity of links per session.
        var busy = Session("vs_contract_busy");
        Applied(await store.CreateAsync(scope, busy));
        for (var index = 0; index < ILiveChatSessionStore.MaximumLinksPerSession; index++)
            Applied(await store.LinkAsync(scope, busy.Id, new ConversationId($"busy-{index}"), Now));
        Assert.AreEqual(LiveChatWriteRejection.CapacityExceeded, Is<LiveChatWriteRejected>((await competing.LinkAsync(scope, busy.Id, new ConversationId("busy-x"), Now)).Value).Reason);

        // Pruning removes expired sessions and their links.
        var expiring = Session("vs_contract_old", expiresAt: Now.AddHours(1));
        Applied(await store.CreateAsync(scope, expiring));
        Applied(await store.LinkAsync(scope, expiring.Id, conversation, Now));
        Assert.IsGreaterThanOrEqualTo(1, await competing.PruneExpiredAsync(Now.AddHours(2), 100));
        Assert.IsNull(await store.GetAsync(AccessPartition.From(scope), expiring.Id));
        Assert.IsFalse((await store.FindSessionsAsync(scope, conversation, 32)).Contains(expiring.Id));
        Assert.IsNotNull(await store.GetAsync(AccessPartition.From(scope), session.Id));
    }

    public static async Task VerifyConcurrentLinksAsync(ILiveChatSessionStore store, ILiveChatSessionStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "livechat-race");
        var session = Session("vs_race");
        Applied(await store.CreateAsync(scope, session));
        var tasks = Enumerable.Range(0, 16).Select(index => (index % 2 == 0 ? store : competing).LinkAsync(scope, session.Id, new ConversationId("conv-race"), Now.AddSeconds(index))).ToList();
        var results = await Task.WhenAll(tasks);
        Assert.IsTrue(results.All(result => result.Value is LiveChatWriteApplied<VisitorConversationLink>));
        Assert.HasCount(1, await store.ListLinksAsync(scope, session.Id));
        var updates = await Task.WhenAll(store.UpdateAsync(scope, session.With(displayName: "A"), 1), competing.UpdateAsync(scope, session.With(displayName: "B"), 1));
        Assert.AreEqual(1, updates.Count(result => result.Value is LiveChatWriteApplied<StoredVisitorSession>), "Compare-and-set admits exactly one concurrent writer.");
    }

    private static T Applied<T>(LiveChatWriteResult<T> result) => Is<LiveChatWriteApplied<T>>(result.Value).Value;

    private static T Is<T>(object? value) where T : class
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value!;
    }
}
