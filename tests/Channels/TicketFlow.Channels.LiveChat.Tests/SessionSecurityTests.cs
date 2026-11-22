using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contacts;
using TicketFlow.Core;
using TicketFlow.Realtime;
using static TicketFlow.Channels.LiveChat.Tests.LiveChatHost;

namespace TicketFlow.Channels.LiveChat.Tests;

[TestClass]
public sealed class SessionSecurityTests
{
    [TestMethod]
    public void TokensAreRandomParseableAndOnlyHashedAtRest()
    {
        var id = new VisitorSessionId("vs_abc");
        var first = VisitorSessionTokens.Issue(id);
        var second = VisitorSessionTokens.Issue(id);
        Assert.AreNotEqual(first.Token, second.Token);
        Assert.StartsWith("tfvs1.vs_abc.", first.Token);
        var secret = first.Token["tfvs1.vs_abc.".Length..];
        Assert.AreEqual(52, secret.Length, "256-bit secrets in base32.");
        CollectionAssert.AreEqual(SHA256.HashData(Encoding.ASCII.GetBytes(secret)), first.Hash.ToArray());
        Assert.IsFalse(first.Hash.ToArray().AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(first.Token).AsSpan(0, 32)));
        Assert.AreEqual(id, VisitorSessionTokens.Parse(first.Token)!.Value.Session);
        CollectionAssert.AreEqual(first.Hash.ToArray(), VisitorSessionTokens.Parse(first.Token)!.Value.SecretHash);

        foreach (var malformed in new[] { "", "tfvs1.", "tfvs2.vs_abc." + secret, "tfvs1.vs_abc", "tfvs1..x", $"tfvs1.vs_abc.{secret}!", $"tfvs1.vs_abc.{secret.ToUpperInvariant()}",
            $"tfvs1.vs_abc.{secret[..^1]}", new string('a', 500) })
            Assert.IsNull(VisitorSessionTokens.Parse(malformed), malformed);
        Assert.Contains("redacted", first.ToString());
    }

    [TestMethod]
    public async Task AuthenticationRejectsTamperedExpiredRevokedAndForeignTokens()
    {
        await using var host = await CreateAsync();
        var grant = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        var stored = grant.Context.Session.Session;
        Assert.IsFalse(stored.TokenHash.ToArray().AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(grant.Token)), "The token itself is never stored.");
        Assert.AreEqual(stored.Id, Value(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin)).Session.Session.Id);

        var tampered = grant.Token[..^1] + (grant.Token[^1] == 'a' ? 'b' : 'a');
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.AuthenticateAsync(host.Partition, tampered, Origin)));
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token.Replace(stored.Id.Value, "vs_other"), Origin)));
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.AuthenticateAsync(new TicketFlow.Access.AccessPartition("other", "project", "test", "app"), grant.Token, Origin)));
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.AuthenticateAsync(host.Partition, "garbage", Origin)));

        host.Host.Clock.Advance(TimeSpan.FromDays(30));
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin)), "Expired sessions fail.");
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.ListConversationsAsync(grant.Context)), "Existing contexts re-check expiry.");

        var other = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        Assert.IsTrue(Value(await host.Visitors.EndSessionAsync(other.Context)));
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.AuthenticateAsync(host.Partition, other.Token, Origin)), "Revoked sessions fail.");
    }

    [TestMethod]
    public async Task RotationKeepsThePreviousTokenOnlyForTheGraceWindow()
    {
        await using var host = await CreateAsync(options: options => { options.TokenRotationInterval = TimeSpan.FromHours(1); options.TokenRotationGrace = TimeSpan.FromMinutes(2); });
        var grant = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        host.Host.Clock.Advance(TimeSpan.FromHours(2));
        var context = Value(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin));
        Assert.IsTrue(context.RotationRecommended);

        var rotated = Value(await host.Visitors.RotateTokenAsync(context));
        Assert.AreNotEqual(grant.Token, rotated.Token);
        Assert.AreEqual(host.Host.Clock.GetUtcNow() + TimeSpan.FromDays(30), rotated.Context.Session.Session.ExpiresAt, "Rotation extends the session.");
        Assert.IsFalse(Value(await host.Visitors.AuthenticateAsync(host.Partition, rotated.Token!, Origin)).RotationRecommended);
        Assert.IsNotNull(Value(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin)), "The previous token works during the grace window.");
        host.Host.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.AreEqual(LiveChatRefusal.Unauthenticated, Refusal(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin)));
        Assert.IsNotNull(Value(await host.Visitors.AuthenticateAsync(host.Partition, rotated.Token!, Origin)));
    }

    [TestMethod]
    public async Task ConcurrentTabsDoNotRotateTwice()
    {
        await using var host = await CreateAsync(options: options => options.TokenRotationInterval = TimeSpan.FromHours(1));
        var grant = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        host.Host.Clock.Advance(TimeSpan.FromHours(2));
        var firstTab = Value(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin));
        var secondTab = Value(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin));
        Assert.IsTrue(firstTab.RotationRecommended && secondTab.RotationRecommended);

        var rotated = Value(await host.Visitors.RotateTokenAsync(firstTab));
        Assert.IsNotNull(rotated.Token);
        var skipped = Value(await host.Visitors.RotateTokenAsync(secondTab));
        Assert.IsNull(skipped.Token, "The reloaded session was rotated moments ago by the other tab.");
        Assert.AreEqual(rotated.Context.Session.Revision, skipped.Context.Session.Revision, "Skipping writes nothing.");
        Assert.IsNotNull(Value(await host.Visitors.AuthenticateAsync(host.Partition, rotated.Token, Origin)));
    }

    [TestMethod]
    public void VerificationIsConstantTimeOverBothHashes()
    {
        var token = VisitorSessionTokens.Issue(new VisitorSessionId("vs_x"));
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var session = new VisitorSession(new VisitorSessionId("vs_x"), "cfg", new InboxId("i"), "v", token.Hash, now, now, now.AddDays(1));
        var hash = VisitorSessionTokens.Parse(token.Token)!.Value.SecretHash;
        Assert.IsTrue(VisitorSessionTokens.Verify(session, hash, now));
        Assert.IsFalse(VisitorSessionTokens.Verify(session, new byte[32], now));
        Assert.IsFalse(VisitorSessionTokens.Verify(session, hash.AsSpan(0, 31), now), "Length mismatches fail.");
        var next = VisitorSessionTokens.Issue(new VisitorSessionId("vs_x"));
        var rotated = session.With(tokenHash: next.Hash, previous: (token.Hash, now.AddMinutes(1)));
        Assert.IsTrue(VisitorSessionTokens.Verify(rotated, hash, now));
        Assert.IsFalse(VisitorSessionTokens.Verify(rotated, hash, now.AddMinutes(1)), "The grace window is half-open.");
    }

    [TestMethod]
    public void OriginsNormalizeAndMatchExactly()
    {
        Assert.AreEqual("https://shop.example.com", LiveChatOrigin.Normalize("https://Shop.Example.com:443"));
        Assert.AreEqual("https://shop.example.com", LiveChatOrigin.Normalize("https://shop.example.com/"));
        Assert.AreEqual("http://localhost:5173", LiveChatOrigin.Normalize("http://localhost:5173"));
        Assert.AreEqual("https://xn--bcher-kva.example", LiveChatOrigin.Normalize("https://bücher.example"));
        foreach (var invalid in new[] { null, "", "shop.example.com", "ftp://shop.example.com", "https://user@shop.example.com", "https://shop.example.com/path",
            "https://shop.example.com?x=1", "https://shop.example.com#f", "javascript:alert(1)", "null" })
            Assert.IsNull(LiveChatOrigin.Normalize(invalid), invalid);

        var widget = StoreContracts.LiveChatStoreContract.Widget();
        Assert.IsTrue(LiveChatOrigin.IsAllowed(widget, "https://SHOP.example.com"));
        Assert.IsFalse(LiveChatOrigin.IsAllowed(widget, "http://shop.example.com"), "Scheme must match.");
        Assert.IsFalse(LiveChatOrigin.IsAllowed(widget, "https://shop.example.com:8443"), "Port must match.");
        Assert.IsFalse(LiveChatOrigin.IsAllowed(widget, "https://evil-shop.example.com"));
        Assert.IsFalse(LiveChatOrigin.IsAllowed(widget, "https://shop.example.com.evil.example"));
        Assert.IsFalse(LiveChatOrigin.IsAllowed(widget, null));
    }

    [TestMethod]
    public async Task OriginChecksGuardWidgetSessionAndAuthentication()
    {
        await using var host = await CreateAsync();
        Assert.AreEqual(LiveChatRefusal.OriginNotAllowed, Refusal(await host.Visitors.GetWidgetAsync(host.Partition, WidgetKey, "https://evil.example")));
        Assert.AreEqual(LiveChatRefusal.OriginNotAllowed, Refusal(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, "https://evil.example")));
        Assert.AreEqual(LiveChatRefusal.OriginNotAllowed, Refusal(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, null)), "Missing origins are refused by default.");
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Visitors.StartSessionAsync(host.Partition, "wk_unknown", Origin)));
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Visitors.StartSessionAsync(new TicketFlow.Access.AccessPartition("other", "project", "test", "app"), WidgetKey, Origin)));
        var grant = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, "http://localhost:5173"));
        Assert.AreEqual(LiveChatRefusal.OriginNotAllowed, Refusal(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, "https://evil.example")));
        Assert.IsNotNull(Value(await host.Visitors.AuthenticateAsync(host.Partition, grant.Token, Origin)));

        await using var lenient = await CreateAsync(options: options => options.AllowMissingOrigin = true);
        Assert.IsNotNull(Value(await lenient.Visitors.StartSessionAsync(lenient.Partition, WidgetKey, null)));
    }

    [TestMethod]
    public void HmacSignaturesVerifyInConstantTime()
    {
        var signature = VisitorIdentityHmac.Compute(Secret, "user-42");
        CollectionAssert.AreEqual(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes("user-42")), Convert.FromHexString(signature));
        Assert.IsTrue(VisitorIdentityHmac.Verify(Secret, "user-42", signature));
        Assert.IsTrue(VisitorIdentityHmac.Verify(Secret, "user-42", signature.ToUpperInvariant()));
        Assert.IsFalse(VisitorIdentityHmac.Verify(Secret, "user-43", signature));
        Assert.IsFalse(VisitorIdentityHmac.Verify(Secret, "user-42", signature[..^2] + "00"));
        Assert.IsFalse(VisitorIdentityHmac.Verify(Secret, "user-42", signature[..62]));
        Assert.IsFalse(VisitorIdentityHmac.Verify(Secret, "user-42", new string('z', 64)));
        Assert.IsFalse(VisitorIdentityHmac.Verify(Secret, "user-42", null));
        Assert.IsFalse(VisitorIdentityHmac.Verify(Encoding.UTF8.GetBytes("another-secret-of-at-least-32-bytes!!!"), "user-42", signature));
        Assert.Throws<ArgumentException>(() => VisitorIdentityHmac.Compute(new byte[16], "user-42"));
    }

    [TestMethod]
    public async Task VerifiedVisitorsBecomeVerifiedExternalUserContacts()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync();
        Assert.AreEqual(LiveChatRefusal.IdentityRejected, Refusal(await host.Visitors.IdentifyAsync(grant.Context, new HmacVisitorIdentification("user-42", new string('0', 64)))));
        Assert.AreEqual(LiveChatRefusal.IdentityRejected, Refusal(await host.Visitors.IdentifyAsync(grant.Context, new SignedTokenVisitorIdentification("good-token"))),
            "The widget disables signed tokens.");
        var context = Value(await host.Visitors.IdentifyAsync(grant.Context, new HmacVisitorIdentification("user-42", VisitorIdentityHmac.Compute(Secret, "user-42"))));
        Assert.AreEqual(new VerifiedVisitorIdentity("shop", "user-42", VisitorVerificationMethod.Hmac, host.Host.Clock.GetUtcNow()), context.Session.Session.Identity);
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Visitors.IdentifyAsync(context, new HmacVisitorIdentification("user-7", VisitorIdentityHmac.Compute(Secret, "user-7")))));

        var accepted = Value(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c1", "Hello")));
        var conversation = (await host.Host.Services.GetRequiredService<TicketFlow.Conversations.IConversationStore>().GetAsync(host.Scope, accepted.Conversation))!;
        var contact = (await host.Host.Services.GetRequiredService<IContactStore>().GetAsync(host.Scope, conversation.Conversation.Contact))!;
        var identity = contact.Contact.Identities.Single();
        Assert.AreEqual(ContactIdentityKind.ExternalUser, identity.Address.Kind);
        Assert.AreEqual("shop", identity.Address.Namespace);
        Assert.IsTrue(identity.Verification.IsVerified);
    }

    [TestMethod]
    public async Task SignedTokensUseTheValidatorAndAnonymousVisitorsAreClaimed()
    {
        var widget = new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin], identity: new LiveChatIdentityPolicy("app", allowHmac: false, allowSignedToken: true));
        await using var host = await CreateAsync(widget);
        var anonymous = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        var accepted = Value(await host.Visitors.SendMessageAsync(anonymous.Context, new VisitorMessageDraft("c1", "Hi")));
        var conversation = (await host.Host.Services.GetRequiredService<TicketFlow.Conversations.IConversationStore>().GetAsync(host.Scope, accepted.Conversation))!;
        var claimed = (await host.Host.Services.GetRequiredService<IContactStore>().GetAsync(host.Scope, conversation.Conversation.Contact))!.Contact.Identities.Single();
        Assert.AreEqual(ContactIdentityKind.WidgetVisitor, claimed.Address.Kind);
        Assert.IsFalse(claimed.Verification.IsVerified);

        var grant = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        Assert.AreEqual(LiveChatRefusal.IdentityRejected, Refusal(await host.Visitors.IdentifyAsync(grant.Context, new HmacVisitorIdentification("u", VisitorIdentityHmac.Compute(Secret, "u")))));
        Assert.AreEqual(LiveChatRefusal.IdentityRejected, Refusal(await host.Visitors.IdentifyAsync(grant.Context, new SignedTokenVisitorIdentification("bad-token"))));
        var verified = Value(await host.Visitors.IdentifyAsync(grant.Context, new SignedTokenVisitorIdentification("good-token")));
        Assert.AreEqual("idf", verified.Session.Session.Identity!.Namespace);
        Assert.AreEqual("subject-7", verified.Session.Session.Identity.Value);
        Assert.AreEqual("Grace", verified.Session.Session.DisplayName);
    }

    [TestMethod]
    public async Task RequiredVerificationBlocksAnonymousMessages()
    {
        var widget = new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), [Origin], identity: new LiveChatIdentityPolicy("shop", requireVerified: true));
        await using var host = await CreateAsync(widget);
        var grant = Value(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin));
        Assert.IsTrue(grant.Widget.RequireVerifiedIdentity);
        Assert.AreEqual(LiveChatRefusal.IdentityRequired, Refusal(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "Hi"))));
        var context = Value(await host.Visitors.IdentifyAsync(grant.Context, new HmacVisitorIdentification("user-1", VisitorIdentityHmac.Compute(Secret, "user-1"))));
        Assert.IsNotNull(Value(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c1", "Hi"))));
    }
}
