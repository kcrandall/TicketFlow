using TicketFlow.Core;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class MaskingTests
{
    private const string SecretEmail = "secret.person@example.com";
    private const string SecretPhone = "+15550100999";

    private static async Task<AccessFixture> CreateAsync()
    {
        var fixture = AccessFixture.Create();
        foreach (var policy in new[]
        {
            new AccessFieldPolicy(new FieldPath("contact.phone"), [AccessPermissions.ContactRead], [BuiltInRoles.Supervisor.Id], AccessPermissions.ContactReveal),
            new AccessFieldPolicy(new FieldPath("contact.email"), [AccessPermissions.ContactRead, AccessPermissions.ConversationRead], [], AccessPermissions.ContactReveal),
            new AccessFieldPolicy(new FieldPath("attribute.ssn"), [AccessPermissions.ContactRead]),
            new AccessFieldPolicy(new FieldPath("contact.notes"), [AccessPermissions.ContactExport], [], AccessPermissions.ContactReveal),
        })
            await fixture.Policies.CreateAsync(fixture.Scope, policy);
        await fixture.GrantAsync(new UserId("agent"), BuiltInRoles.Agent);
        await fixture.GrantAsync(new UserId("supervisor"), BuiltInRoles.Supervisor);
        await fixture.GrantAsync(new UserId("admin"), BuiltInRoles.Admin);
        return fixture;
    }

    private static AccessResource Contact(AccessFixture fixture) =>
        fixture.Resource(id: "contact-1", attributes: new() { ["email"] = SecretEmail, ["phone"] = SecretPhone });

    [TestMethod]
    public async Task MaskedFieldsAreListedDeterministicallyPerRole()
    {
        using var fixture = await CreateAsync();
        var contact = Contact(fixture);
        Assert.AreEqual("masked:attribute.ssn,contact.email,contact.phone", await fixture.DescribeAsync(fixture.User("agent"), AccessPermissions.ContactRead, contact));
        Assert.AreEqual("masked:attribute.ssn,contact.email", await fixture.DescribeAsync(fixture.User("supervisor"), AccessPermissions.ContactRead, contact));
        Assert.AreEqual("masked:attribute.ssn,contact.email+,contact.phone+", await fixture.DescribeAsync(fixture.User("admin"), AccessPermissions.ContactRead, contact));
        Assert.AreEqual("masked:contact.email", await fixture.DescribeAsync(fixture.User("agent"), AccessPermissions.ConversationRead, contact));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fixture.User("agent"), AccessPermissions.ConversationReply, contact));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fixture.User("nobody"), AccessPermissions.ContactRead, contact));
        var masked = (AccessMasked)(await fixture.AuthorizeAsync(fixture.User("agent"), AccessPermissions.ContactRead, contact)).Value!;
        Assert.IsTrue(new AccessDecision(masked).IsPermitted);
        Assert.ThrowsExactly<ArgumentException>(() => new AccessMasked(AccessPermissions.ContactRead, []));
    }

    [TestMethod]
    public async Task RevealRequiresTheExplicitPermissionAndIsAlwaysAudited()
    {
        using var fixture = await CreateAsync();
        var contact = Contact(fixture);
        static string Describe(RevealResult result) => result switch
        {
            RevealGranted granted => $"granted:{granted.Field}",
            RevealRefused refused => $"refused:{refused.Reason}",
        };
        Task<RevealResult> Reveal(string user, string field, Permission? permission = null) =>
            fixture.Evaluator.RevealAsync(new RevealRequest(fixture.User(user), contact, permission ?? AccessPermissions.ContactRead, new FieldPath(field), "Customer asked to verify identity"));

        Assert.AreEqual("refused:RevealDenied", Describe(await Reveal("agent", "contact.email")));
        Assert.AreEqual("refused:NotRevealable", Describe(await Reveal("admin", "attribute.ssn")));
        Assert.AreEqual("refused:NoPolicy", Describe(await Reveal("admin", "contact.address")));
        Assert.AreEqual("refused:NoPolicy", Describe(await Reveal("admin", "contact.notes")));
        Assert.AreEqual("refused:ReadDenied", Describe(await Reveal("nobody", "contact.email")));
        Assert.AreEqual("refused:ReadDenied", Describe(await Reveal("admin", "contact.email", new Permission("contact:unknown"))));
        var granted = await Reveal("admin", "contact.email");
        Assert.AreEqual("granted:contact.email", Describe(granted));
        Assert.AreEqual("granted:contact.phone", Describe(await Reveal("supervisor", "contact.phone")));

        var events = fixture.Audit.Snapshot();
        Assert.HasCount(8, events);
        Assert.IsTrue(events.All(e => e.Kind == AccessAuditKind.Reveal && string.Equals(e.ResourceId, "contact-1", StringComparison.Ordinal) && string.Equals(e.Justification, "Customer asked to verify identity", StringComparison.Ordinal)));
        var revealed = events.Single(e => e.Id == ((RevealGranted)granted.Value!).AuditEventId);
        Assert.AreEqual(AccessAuditOutcome.Allowed, revealed.Outcome);
        Assert.AreEqual(new FieldPath("contact.email"), revealed.Field);
        Assert.AreEqual(new AccessActor(AccessActorKind.User, "admin"), revealed.Actor);
        Assert.AreEqual(6, events.Count(e => e.Outcome == AccessAuditOutcome.Denied));
    }

    [TestMethod]
    public async Task AuditContentNeverContainsMaskedOrAttributeValues()
    {
        using var fixture = await CreateAsync();
        var contact = Contact(fixture);
        foreach (var user in new[] { "agent", "supervisor", "admin" })
        {
            await fixture.AuthorizeAsync(fixture.User(user), AccessPermissions.ContactExport, contact);
            await fixture.AuthorizeAsync(fixture.User(user), AccessPermissions.ContactMerge, contact);
            await fixture.Evaluator.RevealAsync(new RevealRequest(fixture.User(user), contact, AccessPermissions.ContactRead, new FieldPath("contact.email")));
            await fixture.Planner.PlanAsync(new AccessQuery(fixture.User(user), AccessPermissions.ContactExport, fixture.Scope));
        }
        var events = fixture.Audit.Snapshot();
        Assert.HasCount(12, events);
        foreach (var auditEvent in events)
        {
            var text = auditEvent.ToString() + string.Join("|", typeof(AccessAuditEvent).GetProperties().Select(property => property.GetValue(auditEvent)?.ToString()));
            Assert.DoesNotContain(SecretEmail, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SecretPhone, text, StringComparison.Ordinal);
        }
        Assert.AreEqual("AccessResource { <redacted> }", contact.ToString());
    }

    [TestMethod]
    public async Task UnmaskingRolesApplyOnlyWhereTheirGrantApplies()
    {
        using var fixture = await CreateAsync();
        var user = fixture.User("lead");
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Supervisor, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Supervisor, condition: new AttributeEqualsCondition("tier", "vip"));
        Assert.AreEqual("masked:attribute.ssn,contact.email", await fixture.DescribeAsync(user, AccessPermissions.ContactRead, fixture.Resource(team: "billing")));
        Assert.AreEqual("masked:attribute.ssn,contact.email,contact.phone", await fixture.DescribeAsync(user, AccessPermissions.ContactRead, fixture.Resource(team: "sales")));
        // The conditional supervisor grant is unknown without the attribute, so it cannot unmask.
        Assert.AreEqual("masked:attribute.ssn,contact.email,contact.phone", await fixture.DescribeAsync(user, AccessPermissions.ContactRead, fixture.Resource()));
        Assert.AreEqual("masked:attribute.ssn,contact.email", await fixture.DescribeAsync(user, AccessPermissions.ContactRead, fixture.Resource(attributes: new() { ["tier"] = "vip" })));
    }

    [TestMethod]
    public async Task BreakGlassThatChangesMaskingIsFlaggedAndAudited()
    {
        using var fixture = await CreateAsync();
        var responder = fixture.User("responder");
        await fixture.GrantAsync(responder.UserId, BuiltInRoles.Agent);
        await fixture.GrantAsync(responder.UserId, await fixture.CustomRoleAsync("on-call", AccessPermissions.AccessBreakGlass));
        var revealer = await fixture.CustomRoleAsync("revealer", AccessPermissions.ContactRead, AccessPermissions.ContactReveal);
        foreach (var (id, role) in new[] { ("bg-supervisor", BuiltInRoles.Supervisor), ("bg-revealer", revealer) })
            Assert.AreEqual("applied", AccessFixture.Describe(await fixture.Admin.ActivateBreakGlassAsync(
                new BreakGlassRequest(responder, new GrantId(id), role.Id, fixture.Workspace, TimeSpan.FromMinutes(30), "Incident 99 identity verification"))));
        var before = fixture.Audit.Snapshot().Count;
        var contact = Contact(fixture);

        // contact:read itself is allowed by the standard Agent grant, but phone is unmasked and email revealable only via break-glass.
        var decision = (AccessMasked)(await fixture.AuthorizeAsync(responder, AccessPermissions.ContactRead, contact)).Value!;
        Assert.AreEqual("masked:attribute.ssn,contact.email+", AccessFixture.Describe(decision));
        Assert.IsTrue(decision.UsedBreakGlass);
        var audited = fixture.Audit.Snapshot().Skip(before).Single();
        Assert.AreEqual((AccessAuditKind.Decision, AccessAuditOutcome.Masked, true), (audited.Kind, audited.Outcome, audited.UsedBreakGlass));

        var phone = await fixture.Evaluator.RevealAsync(new RevealRequest(responder, contact, AccessPermissions.ContactRead, new FieldPath("contact.phone")));
        var email = await fixture.Evaluator.RevealAsync(new RevealRequest(responder, contact, AccessPermissions.ContactRead, new FieldPath("contact.email")));
        foreach (var result in new[] { phone, email })
        {
            var granted = (RevealGranted)result.Value!;
            Assert.IsTrue(fixture.Audit.Snapshot().Single(e => e.Id == granted.AuditEventId).UsedBreakGlass, granted.Field.Value);
        }

        // Without break-glass the same reveal is refused, and an agent decision is not flagged.
        Assert.AreEqual("masked:attribute.ssn,contact.email,contact.phone", await fixture.DescribeAsync(fixture.User("agent"), AccessPermissions.ContactRead, contact));
    }
}
