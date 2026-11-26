using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using static TicketFlow.Channels.LiveChat.Tests.LiveChatHost;

namespace TicketFlow.Channels.LiveChat.Tests;

/// <summary>E16: identifying a visitor links the anonymous visitor contact to the verified account; conversations follow the merge.</summary>
[TestClass]
public sealed class IdentityLinkingTests
{
    private static readonly ContactIdentityAddress Account = ContactIdentityAddress.Parse(ContactIdentityKind.ExternalUser, "user-42", "shop");

    private static async Task<LiveChatHost> CreateAsync(bool grantLinking = true, Action<LiveChatOptions>? options = null)
    {
        var host = await LiveChatHost.CreateAsync(options: options);
        if (grantLinking)
        {
            var role = new AccessRole(new RoleId("visitor-linker"), "Visitor linker", [ContactPermissions.Link, ContactPermissions.Merge]);
            Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await host.Host.Services.GetRequiredService<IAccessRoleStore>().CreateAsync(host.Scope, role)).Value);
            await host.Host.GrantAsync(new ApiClientId("channels"), role);
        }
        return host;
    }

    private static async Task<VisitorSessionContext> IdentifyAsync(LiveChatHost host, VisitorSessionContext context) =>
        Value(await host.Visitors.IdentifyAsync(context, new HmacVisitorIdentification("user-42", VisitorIdentityHmac.Compute(Secret, "user-42"))));

    private static async Task<ContactId> ContactOfAsync(LiveChatHost host, ConversationId conversation) =>
        (await host.Host.Services.GetRequiredService<IConversationStore>().GetAsync(host.Scope, conversation))!.Conversation.Contact;

    [TestMethod]
    public async Task IdentifyMergesTheAnonymousContactAndItsConversationsIntoTheAccountContact()
    {
        await using var host = await CreateAsync();
        var account = ((TicketValue<ContactView>)(await host.Host.Contacts.CreateAsync(host.Host.Owner, new ContactDraft(new ContactProfile(),
            [new ContactIdentity(Account, new IdentityVerified("shop-login", host.Host.Clock.GetUtcNow()))]))).Value!).Value;
        var grant = await host.StartAsync();
        var first = Value(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "Where is my order?")));
        var anonymous = await ContactOfAsync(host, first.Conversation);
        Assert.AreNotEqual(account.Id, anonymous);

        var context = await IdentifyAsync(host, grant.Context);
        Assert.AreEqual(account.Id, await ContactOfAsync(host, first.Conversation), "the earlier conversation follows the merge");
        var contacts = host.Host.Services.GetRequiredService<IContactStore>();
        Assert.AreEqual(account.Id, (await contacts.GetAsync(host.Scope, anonymous))!.Contact.MergedInto);
        Assert.AreEqual("Ada", (await contacts.GetAsync(host.Scope, account.Id))!.Contact.Profile.DisplayName);

        // The session continues on the merged contact: later messages thread into the same conversation.
        var second = Value(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c2", "Any news?")));
        Assert.AreEqual(first.Conversation, second.Conversation);
        Assert.IsFalse(second.StartedConversation);
        Assert.AreEqual(first.Conversation, Value(await host.Visitors.ListConversationsAsync(context)).Single().Conversation);

        // Unmerging (the documented undo) moves the conversation back to the anonymous contact.
        var audit = host.Host.Services.GetRequiredService<InMemoryContactStore>().AuditEvents;
        var merge = audit.Single(item => item.Kind == ContactAuditKind.Merged).Merge!.Value;
        Assert.AreEqual(AccessActorKind.ApiClient, audit.Single(item => item.Kind == ContactAuditKind.Linked).Actor.Kind);
        Assert.IsInstanceOfType<TicketValue<ContactUnmergeOutcome>>((await host.Host.Contacts.UnmergeAsync(host.Host.Owner, merge)).Value);
        Assert.AreEqual(anonymous, await ContactOfAsync(host, first.Conversation));
    }

    [TestMethod]
    public async Task IdentifyWithoutAnAccountContactMakesTheVisitorContactTheAccountContact()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync();
        var first = Value(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "Hello")));
        var anonymous = await ContactOfAsync(host, first.Conversation);
        var context = await IdentifyAsync(host, grant.Context);
        var stored = (await host.Host.Services.GetRequiredService<IContactStore>().GetAsync(host.Scope, anonymous))!;
        Assert.IsTrue(stored.Contact.Identities.Single(identity => identity.Address == Account).Verification.IsVerified);
        Assert.AreEqual("livechat-hmac", stored.Contact.Identities.Single(identity => identity.Address == Account).Verification.Source);
        var second = Value(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c2", "Still there?")));
        Assert.AreEqual(first.Conversation, second.Conversation);
        Assert.AreEqual(anonymous, await ContactOfAsync(host, second.Conversation));
    }

    [TestMethod]
    public async Task LinkingIsBestEffortAndCanBeDisabled()
    {
        foreach (var (grantLinking, enabled) in new[] { (false, true), (true, false) })
        {
            await using var host = await CreateAsync(grantLinking, options => options.LinkVisitorContactOnIdentify = enabled);
            var grant = await host.StartAsync();
            var first = Value(await host.Visitors.SendMessageAsync(grant.Context, new VisitorMessageDraft("c1", "Hello")));
            var anonymous = await ContactOfAsync(host, first.Conversation);
            var context = await IdentifyAsync(host, grant.Context);
            Assert.IsNotNull(context.Session.Session.Identity, "identification succeeds even when linking does not happen");
            var stored = (await host.Host.Services.GetRequiredService<IContactStore>().GetAsync(host.Scope, anonymous))!;
            Assert.IsFalse(stored.Contact.Identities.Any(identity => identity.Address == Account), $"grant={grantLinking} enabled={enabled}");
        }
    }
}
