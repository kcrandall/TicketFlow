using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Contacts.Tests;

/// <summary>Regression tests for the E4 post-ticket review findings.</summary>
[TestClass]
public sealed class ReviewRegressionTests
{
    [TestMethod]
    public async Task MaskedIdentityEditsAreUniformlyForbidden()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.MaskIdentitiesAsync();
        var held = ContactFixture.Email("held@example.com");
        var existing = ContactFixture.Email("existing@example.com");
        await fixture.CreateAsync("holder", identities: [fixture.Verified(held)]);
        await fixture.CreateAsync("ada", identities: [fixture.Claimed(existing)]);
        var id = new ContactId("ada");

        ContactEdit[][] probes =
        [
            [new AddContactIdentity(fixture.Verified(held))],            // verified elsewhere
            [new AddContactIdentity(fixture.Claimed(ContactFixture.Email("fresh@example.com")))],
            [new AddContactIdentity(fixture.Verified(existing))],        // upgrade an address already on the contact
            [new RemoveContactIdentity(existing)],                       // present
            [new RemoveContactIdentity(ContactFixture.Email("absent@example.com"))], // absent
            [new SetContactBlocked(true), new RemoveContactIdentity(ContactFixture.Phone("+14155550100"))],
        ];
        foreach (var edits in probes)
        {
            var refused = (ContactRefused)(await fixture.Contacts.UpdateAsync(fixture.Agent, id, 1, edits)).Value!;
            Assert.AreEqual(ContactRefusal.Forbidden, refused.Reason);
            Assert.IsEmpty(refused.Errors);
        }
        Assert.AreEqual(1L, (await fixture.Store.GetAsync(fixture.Scope, id))!.Revision);

        // Social handles are not masked, so the agent may still edit them; the supervisor sees email unmasked.
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Agent, id, 1,
            [new AddContactIdentity(new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.SocialHandle, "ada", "x"), new IdentityClaimed("agent", fixture.Clock.GetUtcNow())))])));
        Assert.AreEqual("refused:IdentityTaken", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Supervisor, id, 2, [new AddContactIdentity(fixture.Verified(held))])));
        Assert.AreEqual("refused:Invalid", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Supervisor, id, 2,
            [new RemoveContactIdentity(ContactFixture.Email("absent@example.com"))])));
    }

    [TestMethod]
    public async Task IdentityTakenIsHiddenWhenTheHolderIsUnreadable()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var editor = await fixture.CustomRoleAsync("vip-editor", ContactPermissions.Read, ContactPermissions.Update, ContactPermissions.Create);
        await fixture.GrantAsync(new UserId("vip-agent"), editor, condition: new HasLabelCondition(new LabelId("vip")));
        var vipAgent = TestPrincipals.User(fixture.Scope, "vip-agent");
        var hidden = ContactFixture.Email("hidden@example.com");
        await fixture.CreateAsync("plain", identities: [fixture.Verified(hidden)]);
        await fixture.CreateAsync("vip-holder", identities: [fixture.Verified(ContactFixture.Email("seen@example.com"))], labels: ["vip"]);
        await fixture.CreateAsync("vip", labels: ["vip"]);

        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(vipAgent, new ContactId("vip"), 1, [new AddContactIdentity(fixture.Verified(hidden))])));
        Assert.AreEqual("refused:IdentityTaken", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(vipAgent, new ContactId("vip"), 1,
            [new AddContactIdentity(fixture.Verified(ContactFixture.Email("seen@example.com")))])));
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.CreateAsync(vipAgent,
            new ContactDraft(ContactProfile.Empty, [fixture.Verified(hidden)], Labels: [new LabelId("vip")]))));
    }

    [TestMethod]
    public async Task ExportAuditsEverySameLabelContactAndTheEmittedCount()
    {
        using var fixture = await ContactFixture.CreateAsync();
        for (var index = 0; index < 3; index++)
            await fixture.CreateAsync($"c{index}", $"Contact {index}", labels: ["same"]);
        var before = fixture.AccessAudit.Snapshot().Count;

        var started = (ContactExportStarted)(await fixture.Contacts.ExportAsync(fixture.Supervisor, new ContactExportRequest(10, "audit check"))).Value!;
        var rows = new List<string>();
        await foreach (var row in started.Contacts)
            rows.Add(row.Id.Value);
        CollectionAssert.AreEqual(new[] { "c0", "c1", "c2" }, rows);

        var decisions = fixture.AccessAudit.Snapshot().Skip(before).Where(item => item.Kind == AccessAuditKind.Decision && item.Permission == ContactPermissions.Export).ToList();
        Assert.HasCount(4, decisions); // the workspace check plus one per exported contact
        CollectionAssert.AreEquivalent(new[] { "c0", "c1", "c2" }, decisions.Where(item => item.ResourceId is not null).Select(item => item.ResourceId).ToArray());
        var contactAudit = fixture.Store.AuditEvents;
        Assert.AreEqual(ContactAuditKind.Exported, contactAudit[0].Kind);
        Assert.AreEqual(ContactAuditKind.ExportCompleted, contactAudit[1].Kind);
        Assert.AreEqual(3, contactAudit[1].Count);
        Assert.AreEqual("audit check", contactAudit[1].Justification);

        // An abandoned stream records what was actually emitted.
        var abandoned = (ContactExportStarted)(await fixture.Contacts.ExportAsync(fixture.Supervisor, new ContactExportRequest(10, "partial export"))).Value!;
        await using (var enumerator = abandoned.Contacts.GetAsyncEnumerator())
            Assert.IsTrue(await enumerator.MoveNextAsync());
        Assert.AreEqual(1, fixture.Store.AuditEvents[^1].Count);
        Assert.AreEqual(ContactAuditKind.ExportCompleted, fixture.Store.AuditEvents[^1].Kind);
    }

    [TestMethod]
    public async Task SearchDecisionsAreStillCachedForNonSensitiveReads()
    {
        using var fixture = await ContactFixture.CreateAsync();
        for (var index = 0; index < 3; index++)
            await fixture.CreateAsync($"c{index}", $"Contact {index}", labels: ["same"]);
        var before = fixture.AccessAudit.Snapshot().Count;
        Assert.HasCount(3, ((ContactSearchPage)(await fixture.Contacts.SearchAsync(fixture.Agent, ContactSearch.ByName())).Value!).Contacts);
        Assert.AreEqual(before, fixture.AccessAudit.Snapshot().Count);
    }

    [TestMethod]
    public async Task DeleteAuthorizesAndReportsEveryCascadedTombstone()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.CreateAsync("target", labels: ["vip"]);
        await fixture.CreateAsync("source");
        var merge = ContactFixture.Applied(await fixture.Contacts.MergeAsync(fixture.Supervisor, new ContactMergeRequest(new ContactId("target"), 1, new ContactId("source"), 1)));

        var deleter = await fixture.CustomRoleAsync("vip-deleter", ContactPermissions.Read, ContactPermissions.Delete);
        await fixture.GrantAsync(new UserId("vip-deleter"), deleter, condition: new HasLabelCondition(new LabelId("vip")));
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.DeleteAsync(TestPrincipals.User(fixture.Scope, "vip-deleter"), new ContactId("target"), 2)));
        Assert.IsNotNull(await fixture.Store.GetAsync(fixture.Scope, new ContactId("source")));

        var observed = fixture.Observer.Changes.Count;
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Contacts.DeleteAsync(fixture.Owner, new ContactId("target"), 2, "erasure")));
        var deleted = fixture.Observer.Changes.Skip(observed).ToList();
        CollectionAssert.AreEqual(new[] { "target", "source" }, deleted.Select(change => change.Event.Contact.Value).ToArray());
        Assert.IsTrue(deleted.All(change => change.Event.Kind == ContactChangeKind.Deleted));
        Assert.AreEqual(new ContactId("target"), deleted[1].Event.Related);
        Assert.IsNull(await fixture.Store.GetAsync(fixture.Scope, new ContactId("source")));
        Assert.IsNull(await fixture.Store.GetMergeAsync(fixture.Scope, merge.MergeId));
        Assert.AreEqual(2, fixture.Store.AuditEvents.Single(item => item.Kind == ContactAuditKind.Deleted).Count);
    }

    [TestMethod]
    public async Task UnmergeAuthorizesTheRestoredTarget()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.CreateAsync("target");
        await fixture.CreateAsync("source", labels: ["vip"]);
        var merge = ContactFixture.Applied(await fixture.Contacts.MergeAsync(fixture.Supervisor, new ContactMergeRequest(new ContactId("target"), 1, new ContactId("source"), 1)));
        var merger = await fixture.CustomRoleAsync("vip-merger", ContactPermissions.Read, ContactPermissions.Merge);
        await fixture.GrantAsync(new UserId("vip-merger"), merger, condition: new HasLabelCondition(new LabelId("vip")));

        // The merged target carries "vip" (from the source) and the source snapshot does too, but the restored target would not.
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.UnmergeAsync(TestPrincipals.User(fixture.Scope, "vip-merger"), merge.MergeId)));
        Assert.IsNotNull(await fixture.Store.GetMergeAsync(fixture.Scope, merge.MergeId));
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Contacts.UnmergeAsync(fixture.Supervisor, merge.MergeId)));
    }

    [TestMethod]
    public void AddContactsLeavesTimeProviderToAddTicketFlow()
    {
        var services = new ServiceCollection();
        new TicketFlowBuilder(services).AddContacts();
        Assert.IsFalse(services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)));
    }
}
