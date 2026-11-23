using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Channels.LiveChat.StoreContracts;
using TicketFlow.Contacts;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.Testing;
using static TicketFlow.Channels.LiveChat.Tests.LiveChatHost;

namespace TicketFlow.Channels.LiveChat.Tests;

[TestClass]
public sealed class WidgetAndAgentTests
{
    [TestMethod]
    public async Task WidgetAdministrationIsAuthorizedOnTheInboxAndBindingChecked()
    {
        await using var host = await CreateAsync(seedWidget: false);
        var widget = LiveChatStoreContract.Widget();
        Assert.AreEqual(LiveChatRefusal.Forbidden, Refusal(await host.Widgets.CreateAsync(host.Host.Agent, widget)));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Widgets.CreateAsync(host.Host.Owner, LiveChatStoreContract.Widget("cfg-other"))));
        Assert.AreEqual(LiveChatRefusal.Invalid, Refusal(await host.Widgets.CreateAsync(host.Host.Owner, LiveChatStoreContract.Widget(inbox: "email-inbox"))));
        var created = Value(await host.Widgets.CreateAsync(host.Host.Owner, widget));
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual(LiveChatRefusal.Conflict, Refusal(await host.Widgets.CreateAsync(host.Host.Owner, widget)));
        Assert.AreEqual(LiveChatRefusal.Forbidden, Refusal(await host.Widgets.GetAsync(host.Host.Viewer, "cfg-chat")));
        Assert.AreEqual(created, Value(await host.Widgets.GetAsync(host.Host.Owner, "cfg-chat")));
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Widgets.GetAsync(host.Host.Owner, "cfg-missing")));

        var changed = new LiveChatWidget("cfg-chat", new InboxId("chat-inbox"), ["https://new.example"]);
        Assert.AreEqual(LiveChatRefusal.Conflict, Refusal(await host.Widgets.UpdateAsync(host.Host.Owner, changed, 7)));
        Assert.AreEqual(LiveChatRefusal.Forbidden, Refusal(await host.Widgets.UpdateAsync(host.Host.Agent, changed, 1)));
        Assert.AreEqual(2, Value(await host.Widgets.UpdateAsync(host.Host.Owner, changed, 1)).Revision);
        Assert.AreEqual(LiveChatRefusal.OriginNotAllowed, Refusal(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, Origin)), "Origin changes apply immediately.");
        Assert.AreEqual(LiveChatRefusal.Forbidden, Refusal(await host.Widgets.DeleteAsync(host.Host.Agent, "cfg-chat", 2)));
        Value(await host.Widgets.DeleteAsync(host.Host.Owner, "cfg-chat", 2));
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await host.Visitors.StartSessionAsync(host.Partition, WidgetKey, "https://new.example")));
    }

    [TestMethod]
    public void WidgetModelsValidateBounds()
    {
        Assert.Throws<ArgumentException>(() => new LiveChatWidget("cfg", new InboxId("i"), []));
        Assert.Throws<ArgumentException>(() => new LiveChatWidget("cfg", new InboxId("i"), ["https://a.example/path"]));
        Assert.Throws<ArgumentException>(() => new LiveChatWidget("cfg", new InboxId("i"), ["https://a.example"],
            prechatFields: [new LiveChatPrechatField("a", "A", PrechatFieldKind.Text), new LiveChatPrechatField("a", "B", PrechatFieldKind.Text)]));
        Assert.Throws<ArgumentException>(() => new LiveChatWidget("cfg", new InboxId("i"), ["https://a.example"],
            prechatFields: [new LiveChatPrechatField("a", "A", PrechatFieldKind.Email), new LiveChatPrechatField("b", "B", PrechatFieldKind.Email)]));
        Assert.Throws<ArgumentException>(() => new LiveChatPrechatField("Bad Key", "A", PrechatFieldKind.Text));
        Assert.Throws<ArgumentException>(() => new LiveChatAttachmentPolicy(10, ["IMAGE/PNG"]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveChatAttachmentPolicy(0, ["image/png"]));
        Assert.Throws<ArgumentException>(() => new LiveChatIdentityPolicy("Not A Token"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveChatDeflection(new PortalId("p"), new HelpCenterLocale("en"), 11));
        var widget = new LiveChatWidget("cfg", new InboxId("i"), ["https://B.example", "https://a.example:443"]);
        CollectionAssert.AreEqual(new[] { "https://a.example", "https://b.example" }, widget.AllowedOrigins.ToArray());
    }

    [TestMethod]
    public async Task AgentsSeeMaskedVisitorContext()
    {
        await using var host = await CreateAsync();
        var grant = await host.StartAsync();
        var context = Value(await host.Visitors.IdentifyAsync(grant.Context, new HmacVisitorIdentification("user-42", VisitorIdentityHmac.Compute(Secret, "user-42"))));
        var accepted = Value(await host.Visitors.SendMessageAsync(context, new VisitorMessageDraft("c1", "Hi")));
        var agents = host.Host.Services.GetRequiredService<ILiveChatAgentService>();

        var view = Value(await agents.GetVisitorAsync(host.Host.Agent, accepted.Conversation));
        Assert.AreEqual(context.Session.Session.VisitorId, view.VisitorId);
        Assert.IsTrue(view.Verified);
        Assert.AreEqual("user-42", view.IdentityValue);
        Assert.AreEqual("Ada", view.DisplayName);
        Assert.AreEqual("ada@example.com", view.Prechat.Single(answer => answer.Key == "email").Value);
        var order = view.Prechat.Single(answer => answer.Key == "order");
        Assert.IsTrue(order.Masked, "Sensitive answers without a field policy are masked (fail closed).");
        Assert.IsNull(order.Value);

        var policies = host.Host.Services.GetRequiredService<IAccessFieldPolicyStore>();
        Assert.IsInstanceOfType<StoreApplied<StoredAccessFieldPolicy>>((await policies.CreateAsync(host.Scope,
            new AccessFieldPolicy(ContactFieldPaths.Email, [ContactPermissions.Read], [BuiltInRoles.Owner.Id]))).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessFieldPolicy>>((await policies.CreateAsync(host.Scope,
            new AccessFieldPolicy(ContactFieldPaths.Attribute("order"), [ContactPermissions.Read], [BuiltInRoles.Agent.Id]))).Value);
        var masked = Value(await agents.GetVisitorAsync(host.Host.Agent, accepted.Conversation));
        Assert.IsNull(masked.Prechat.Single(answer => answer.Key == "email").Value);
        Assert.AreEqual("A-42", masked.Prechat.Single(answer => answer.Key == "order").Value, "A field policy unmasks the answer for the Agent role.");
        Assert.IsTrue(masked.MaskedFields.Any(field => field.Field == ContactFieldPaths.Email));
        Assert.AreEqual("ada@example.com", Value(await agents.GetVisitorAsync(host.Host.Owner, accepted.Conversation)).Prechat.Single(answer => answer.Key == "email").Value);

        Assert.AreEqual(LiveChatRefusal.Forbidden, Refusal(await agents.GetVisitorAsync(TestPrincipals.User(host.Scope, "stranger"), accepted.Conversation)));
        Assert.AreEqual(LiveChatRefusal.NotFound, Refusal(await agents.GetVisitorAsync(host.Host.Agent, new ConversationId("missing"))));
    }
}
