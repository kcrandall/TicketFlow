using TicketFlow.Contacts;
using TicketFlow.Core;

namespace TicketFlow.Conversations.Tests;

[TestClass]
public sealed class ContactMergeFollowerTests
{
    [TestMethod]
    public async Task MergeRepointsAndUnmergeRestoresIdempotently()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        await fixture.StartAsync("of-grace", contact: "grace");
        await fixture.StartAsync("of-grace-2", contact: "grace");
        await fixture.StartAsync("of-ada", contact: "ada");

        var merge = (TicketValue<ContactMergeOutcome>)(await fixture.ContactsService.MergeAsync(fixture.Owner,
            new ContactMergeRequest(new ContactId("ada"), 1, new ContactId("grace"), 1))).Value!;
        foreach (var id in new[] { "of-grace", "of-grace-2" })
        {
            var view = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId(id)));
            Assert.AreEqual(new ContactId("ada"), view.Contact, "the in-process observer re-pointed the conversation");
            var last = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Owner, new ConversationId(id))).Events[^1].Event;
            Assert.AreEqual(new ContactChanged(new ContactId("grace"), new ContactId("ada"), merge.Value.MergeId), last.Event.Value);
            Assert.AreEqual(ConversationContactMergeFollower.Actor, last.Actor);
        }
        var untouched = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-ada")));
        Assert.AreEqual(1L, untouched.Revision);

        // Replaying the whole contact change log is a no-op.
        var position = await fixture.Follower.CatchUpAsync(fixture.Scope, 0);
        Assert.IsTrue(position > 0);
        Assert.AreEqual(2L, ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-grace"))).Revision);

        Assert.IsInstanceOfType<TicketValue<ContactUnmergeOutcome>>((await fixture.ContactsService.UnmergeAsync(fixture.Owner, merge.Value.MergeId)).Value);
        var restored = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-grace")));
        Assert.AreEqual(new ContactId("grace"), restored.Contact);
        var events = ConversationFixture.Page(await fixture.Conversations.ReadTimelineAsync(fixture.Owner, new ConversationId("of-grace"))).Events.Select(item => item.Event.Event.Value?.ToString());
        Assert.AreEqual(3L, restored.Revision, string.Join(" | ", events));
        Assert.AreEqual(new ContactId("ada"), ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-ada"))).Contact);
        var final = await fixture.Follower.CatchUpAsync(fixture.Scope, 0);
        Assert.IsTrue(final > position);
        Assert.AreEqual(3L, ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-grace"))).Revision, "replay after unmerge is a no-op");
        Assert.AreEqual(final, await fixture.Follower.CatchUpAsync(fixture.Scope, final));
    }

    [TestMethod]
    public async Task ChainedMergesUnwindNewestFirst()
    {
        using var fixture = await ConversationFixture.CreateAsync();
        Assert.IsInstanceOfType<TicketValue<ContactView>>((await fixture.ContactsService.CreateAsync(fixture.Owner,
            new ContactDraft(new ContactProfile("Lin"), Id: new ContactId("lin")))).Value);
        await fixture.StartAsync("of-grace", contact: "grace");
        var first = (TicketValue<ContactMergeOutcome>)(await fixture.ContactsService.MergeAsync(fixture.Owner,
            new ContactMergeRequest(new ContactId("ada"), 1, new ContactId("grace"), 1))).Value!;
        var adaRevision = first.Value.Target.Revision;
        var second = (TicketValue<ContactMergeOutcome>)(await fixture.ContactsService.MergeAsync(fixture.Owner,
            new ContactMergeRequest(new ContactId("lin"), 1, new ContactId("ada"), adaRevision))).Value!;
        Assert.AreEqual(new ContactId("lin"), ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-grace"))).Contact);

        Assert.IsInstanceOfType<TicketValue<ContactUnmergeOutcome>>((await fixture.ContactsService.UnmergeAsync(fixture.Owner, second.Value.MergeId)).Value);
        Assert.AreEqual(new ContactId("ada"), ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-grace"))).Contact);
        Assert.IsInstanceOfType<TicketValue<ContactUnmergeOutcome>>((await fixture.ContactsService.UnmergeAsync(fixture.Owner, first.Value.MergeId)).Value);
        var view = ConversationFixture.View(await fixture.Conversations.GetAsync(fixture.Owner, new ConversationId("of-grace")));
        Assert.AreEqual(new ContactId("grace"), view.Contact);
        Assert.AreEqual(0, (await fixture.Store.GetAsync(fixture.Scope, new ConversationId("of-grace")))!.Conversation.ContactRedirects.Count);
    }
}
