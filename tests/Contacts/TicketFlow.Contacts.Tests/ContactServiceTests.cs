using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts.Tests;

[TestClass]
public sealed class ContactServiceTests
{
    [TestMethod]
    public async Task CatalogRegistersFeaturePermissionsOnce()
    {
        using var fixture = await ContactFixture.CreateAsync();
        foreach (var definition in ContactPermissions.Definitions)
            Assert.AreEqual(definition, fixture.Catalog.Find(definition.Permission));
        Assert.IsTrue(fixture.Catalog.Contains(ContactPermissions.Read));
    }

    [TestMethod]
    public async Task CreateRequiresCreatePermissionAndEmitsChange()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var draft = new ContactDraft(new ContactProfile("Ada"), [fixture.Verified(ContactFixture.Email("ada@example.com"))], Id: new ContactId("ada"));
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.CreateAsync(fixture.Agent, draft)));
        var created = ContactFixture.Applied(await fixture.Contacts.CreateAsync(fixture.Crm, draft));
        Assert.AreEqual(1L, created.Revision);
        Assert.AreEqual("ada@example.com", created.Identities.Single().Value);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), created.CreatedAt);
        Assert.AreEqual("refused:Conflict:1", ContactFixture.Describe(await fixture.Contacts.CreateAsync(fixture.Owner, draft)));
        var change = fixture.Observer.Changes.Single();
        Assert.AreEqual(ContactChangeKind.Created, change.Event.Kind);
        Assert.AreEqual(1L, change.Sequence);
        Assert.AreEqual(new ContactId("ada"), change.Event.Contact);

        var generated = ContactFixture.Applied(await fixture.Contacts.CreateAsync(fixture.Owner, new ContactDraft(ContactProfile.Empty)));
        Assert.AreEqual(32, generated.Id.Value.Length);
    }

    [TestMethod]
    public async Task VerifiedIdentityConflictsAreRefusedWithoutLeakingOwner()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var email = ContactFixture.Email("ada@example.com");
        await fixture.CreateAsync("ada", identities: [fixture.Verified(email)]);
        var result = await fixture.Contacts.CreateAsync(fixture.Owner, new ContactDraft(ContactProfile.Empty, [fixture.Verified(email)]));
        var refused = (ContactRefused)result.Value!;
        Assert.AreEqual(ContactRefusal.IdentityTaken, refused.Reason);
        Assert.IsFalse(refused.Errors.Any(error => error.Contains("ada", StringComparison.OrdinalIgnoreCase)));
        await fixture.CreateAsync("claimant", identities: [fixture.Claimed(email)]);
    }

    [TestMethod]
    public async Task AttributesAreValidatedAgainstDefinitions()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.DefineAsync(new ContactAttributeDefinition("tier", ContactAttributeType.Text));
        var undefined = await fixture.Contacts.CreateAsync(fixture.Owner, new ContactDraft(ContactProfile.Empty,
            Attributes: new Dictionary<string, ContactAttributeValue> { ["plan"] = new TextAttributeValue("pro") }));
        Assert.AreEqual("refused:Invalid", ContactFixture.Describe(undefined));
        var mismatched = await fixture.Contacts.CreateAsync(fixture.Owner, new ContactDraft(ContactProfile.Empty,
            Attributes: new Dictionary<string, ContactAttributeValue> { ["tier"] = new NumberAttributeValue(1) }));
        Assert.AreEqual("refused:Invalid", ContactFixture.Describe(mismatched));
        var contact = await fixture.CreateAsync("ada", attributes: new() { ["tier"] = new TextAttributeValue("gold") });
        Assert.AreEqual(new TextAttributeValue("gold"), contact.Attributes.Single().Value!.Value.Value);
    }

    [TestMethod]
    public async Task UpdatesApplyEditsAtomicallyWithRevisionFence()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.DefineAsync(new ContactAttributeDefinition("tier", ContactAttributeType.Text));
        var phone = ContactFixture.Phone("+14155550100");
        await fixture.CreateAsync("ada", "Ada", [fixture.Claimed(phone)]);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var updated = ContactFixture.Applied(await fixture.Contacts.UpdateAsync(fixture.Agent, new ContactId("ada"), 1,
        [
            new SetContactProfile(new ContactProfile("Ada Lovelace", locale: "en-GB")),
            new SetContactAttribute("tier", new TextAttributeValue("gold")),
            new AddContactLabel(new LabelId("vip")),
            new AddContactIdentity(fixture.Verified(phone)),
            new AddContactIdentity(fixture.Claimed(ContactFixture.Email("ada@example.com"))),
            new SetContactBlocked(true),
        ]));
        Assert.AreEqual(2L, updated.Revision);
        Assert.AreEqual("Ada Lovelace", updated.Profile.DisplayName);
        Assert.IsTrue(updated.Blocked);
        Assert.IsTrue(updated.Identities.Single(identity => identity.Kind == ContactIdentityKind.Phone).Verification.IsVerified);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), updated.UpdatedAt);

        Assert.AreEqual("refused:Conflict:2", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Agent, new ContactId("ada"), 1, [new SetContactBlocked(false)])));
        Assert.AreEqual("refused:Invalid", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Agent, new ContactId("ada"), 2,
            [new SetContactBlocked(false), new SetContactAttribute("tier", new BooleanAttributeValue(true))])));
        Assert.AreEqual("refused:Invalid", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Agent, new ContactId("ada"), 2,
            [new RemoveContactIdentity(ContactFixture.Email("other@example.com"))])));
        Assert.IsTrue(ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Agent, new ContactId("ada"))).Blocked);
        Assert.AreEqual("refused:NotFound", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Agent, new ContactId("missing"), 1, [new SetContactBlocked(false)])));
        Assert.AreEqual("refused:Rejected", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(fixture.Agent, new ContactId("ada"), 2,
            [.. Enumerable.Range(0, Contact.MaximumLabels + 1).Select(index => new AddContactLabel(new LabelId($"l{index}")))])));
    }

    [TestMethod]
    public async Task UpdateAuthorizesBothCurrentAndResultingLabels()
    {
        using var fixture = await ContactFixture.CreateAsync();
        var editor = await fixture.CustomRoleAsync("vip-editor", ContactPermissions.Read, ContactPermissions.Update);
        await fixture.GrantAsync(new UserId("vip-agent"), editor, condition: new HasLabelCondition(new LabelId("vip")));
        var vipAgent = Testing.TestPrincipals.User(fixture.Scope, "vip-agent");
        await fixture.CreateAsync("vip", labels: ["vip"]);
        await fixture.CreateAsync("plain");
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(vipAgent, new ContactId("plain"), 1, [new AddContactLabel(new LabelId("vip"))])));
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(vipAgent, new ContactId("vip"), 1, [new RemoveContactLabel(new LabelId("vip"))])));
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Contacts.UpdateAsync(vipAgent, new ContactId("vip"), 1, [new SetContactBlocked(true)])));
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.GetAsync(vipAgent, new ContactId("plain"))));
    }

    [TestMethod]
    public async Task ReadsMaskIdentitiesPerFieldPoliciesAndRevealIsAudited()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.MaskIdentitiesAsync();
        await fixture.CreateAsync("ada", "Ada", [fixture.Verified(ContactFixture.Email("ada@example.com")), fixture.Claimed(ContactFixture.Phone("+14155550100")),
            new ContactIdentity(ContactIdentityAddress.Parse(ContactIdentityKind.SocialHandle, "ada", "x"), new IdentityClaimed("agent", fixture.Clock.GetUtcNow()))]);
        var id = new ContactId("ada");

        var agentView = ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Agent, id));
        Assert.IsTrue(agentView.Identities.Where(identity => identity.Kind != ContactIdentityKind.SocialHandle).All(identity => identity.Masked && identity.Value is null && !identity.Revealable));
        Assert.AreEqual("ada", agentView.Identities.Single(identity => identity.Kind == ContactIdentityKind.SocialHandle).Value);
        CollectionAssert.AreEqual(new[] { "contact.email", "contact.phone" }, agentView.MaskedFields.Select(field => field.Field.Value).ToArray());
        Assert.AreEqual("Ada", agentView.Profile.DisplayName);

        var supervisorView = ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Supervisor, id));
        Assert.IsTrue(supervisorView.Identities.All(identity => !identity.Masked && identity.Value is not null));
        Assert.IsEmpty(supervisorView.MaskedFields);

        var ownerView = ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Owner, id));
        Assert.IsTrue(ownerView.Identities.Single(identity => identity.Kind == ContactIdentityKind.Email).Revealable);

        var refused = (ContactRevealRefused)(await fixture.Contacts.RevealAsync(fixture.Agent, id, ContactFieldPaths.Email, "customer called")).Value!;
        Assert.AreEqual(RevealRefusal.RevealDenied, refused.Reason);
        var revealed = (ContactFieldRevealed)(await fixture.Contacts.RevealAsync(fixture.Owner, id, ContactFieldPaths.Email, "verify caller")).Value!;
        Assert.AreEqual("ada@example.com", revealed.Identities.Single().Value);
        var reveals = fixture.AccessAudit.Snapshot().Where(item => item.Kind == AccessAuditKind.Reveal).ToList();
        Assert.HasCount(2, reveals);
        Assert.AreEqual(revealed.AuditEventId, reveals[1].Id);
        Assert.AreEqual("ada", reveals[1].ResourceId);
        Assert.IsFalse(fixture.AccessAudit.Snapshot().Any(item => (item.Justification ?? string.Empty).Contains('@') || (item.ResourceId ?? string.Empty).Contains('@')));
        Assert.AreEqual(ContactRefusal.Invalid, ((ContactRefused)(await fixture.Contacts.RevealAsync(fixture.Owner, id, new FieldPath("contact.unknown"))).Value!).Reason);
    }

    [TestMethod]
    public async Task SensitiveAttributesFailClosedWithoutPolicy()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.DefineAsync(new ContactAttributeDefinition("national-id", ContactAttributeType.Text, sensitive: true));
        await fixture.DefineAsync(new ContactAttributeDefinition("tier", ContactAttributeType.Text));
        await fixture.CreateAsync("ada", attributes: new() { ["national-id"] = new TextAttributeValue("AB123"), ["tier"] = new TextAttributeValue("gold") });
        var id = new ContactId("ada");

        var ownerView = ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Owner, id));
        var secret = ownerView.Attributes.Single(attribute => string.Equals(attribute.Key, "national-id", StringComparison.Ordinal));
        Assert.IsTrue(secret.Masked && secret.Value is null && !secret.Revealable && secret.Sensitive);
        Assert.AreEqual(new TextAttributeValue("gold"), ownerView.Attributes.Single(attribute => string.Equals(attribute.Key, "tier", StringComparison.Ordinal)).Value!.Value.Value);
        Assert.AreEqual(RevealRefusal.NoPolicy, ((ContactRevealRefused)(await fixture.Contacts.RevealAsync(fixture.Owner, id, ContactFieldPaths.Attribute("national-id"))).Value!).Reason);

        await fixture.PolicyAsync(ContactFieldPaths.SensitivePolicy(ContactFieldPaths.Attribute("national-id"), [BuiltInRoles.Supervisor.Id]));
        Assert.IsFalse(ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Supervisor, id)).Attributes.Single(attribute => string.Equals(attribute.Key, "national-id", StringComparison.Ordinal)).Masked);
        var agentSecret = ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Agent, id)).Attributes.Single(attribute => string.Equals(attribute.Key, "national-id", StringComparison.Ordinal));
        Assert.IsTrue(agentSecret.Masked && !agentSecret.Revealable);
        Assert.IsTrue(ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Owner, id)).Attributes.Single(attribute => string.Equals(attribute.Key, "national-id", StringComparison.Ordinal)).Revealable);
        var revealed = (ContactFieldRevealed)(await fixture.Contacts.RevealAsync(fixture.Owner, id, ContactFieldPaths.Attribute("national-id"))).Value!;
        Assert.AreEqual(new TextAttributeValue("AB123"), revealed.Attribute!.Value.Value);
        Assert.IsEmpty(revealed.Identities);
    }

    [TestMethod]
    public async Task DeletedDefinitionsHideStoredValues()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.DefineAsync(new ContactAttributeDefinition("tier", ContactAttributeType.Text));
        await fixture.CreateAsync("ada", attributes: new() { ["tier"] = new TextAttributeValue("gold") });
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Definitions.DeleteAsync(fixture.Agent, "tier", 1)));
        Assert.AreEqual("refused:Conflict:1", ContactFixture.Describe(await fixture.Definitions.DeleteAsync(fixture.Owner, "tier", 2)));
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Definitions.DeleteAsync(fixture.Owner, "tier", 1)));
        Assert.IsEmpty(ContactFixture.Applied(await fixture.Contacts.GetAsync(fixture.Owner, new ContactId("ada"))).Attributes);
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Definitions.CreateAsync(fixture.Agent, new ContactAttributeDefinition("x", ContactAttributeType.Text))));
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Definitions.ListAsync(fixture.Agent)));
        await fixture.DefineAsync(new ContactAttributeDefinition("age", ContactAttributeType.Number));
        Assert.AreEqual("refused:Rejected", ContactFixture.Describe(await fixture.Definitions.UpdateAsync(fixture.Owner, new ContactAttributeDefinition("age", ContactAttributeType.Text), 1)));
    }

    [TestMethod]
    public async Task DeleteRequiresSensitivePermissionAndIsAudited()
    {
        using var fixture = await ContactFixture.CreateAsync();
        await fixture.CreateAsync("ada", identities: [fixture.Verified(ContactFixture.Email("ada@example.com"))]);
        Assert.AreEqual("refused:Forbidden", ContactFixture.Describe(await fixture.Contacts.DeleteAsync(fixture.Supervisor, new ContactId("ada"), 1)));
        Assert.AreEqual("refused:Conflict:1", ContactFixture.Describe(await fixture.Contacts.DeleteAsync(fixture.Owner, new ContactId("ada"), 2)));
        Assert.AreEqual("applied", ContactFixture.Describe(await fixture.Contacts.DeleteAsync(fixture.Owner, new ContactId("ada"), 1, "erasure request")));
        Assert.AreEqual("refused:NotFound", ContactFixture.Describe(await fixture.Contacts.GetAsync(fixture.Owner, new ContactId("ada"))));
        var audit = fixture.Store.AuditEvents.Single();
        Assert.AreEqual(ContactAuditKind.Deleted, audit.Kind);
        Assert.AreEqual("erasure request", audit.Justification);
        Assert.IsTrue(fixture.AccessAudit.Snapshot().Any(item => item.Kind == AccessAuditKind.Decision && item.Permission == ContactPermissions.Delete));
        Assert.AreEqual(ContactChangeKind.Deleted, fixture.Observer.Changes[^1].Event.Kind);
        await fixture.CreateAsync("again", identities: [fixture.Verified(ContactFixture.Email("ada@example.com"))]);
    }
}
