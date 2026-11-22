using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class DecisionContextTests
{
    [TestMethod]
    public async Task BatchDecisionsEqualPointDecisions()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User("ada", "billing");
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing")));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Viewer, condition: new HasLabelCondition(new LabelId("public")));
        await fixture.GrantAsync(user.UserId, BuiltInRoles.LightAgent, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip")), GrantEffect.Deny);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessFieldPolicy>>((await fixture.Policies.CreateAsync(fixture.Scope,
            new AccessFieldPolicy(new FieldPath("conversation.channel"), [AccessPermissions.ConversationRead], [BuiltInRoles.Agent.Id], AccessPermissions.ContactReveal))).Value);
        Permission[] permissions = [AccessPermissions.ConversationRead, AccessPermissions.NoteRead, AccessPermissions.ConversationReply, new("macro:run")];
        var context = await fixture.Evaluator.LoadDecisionContextAsync(user, fixture.Scope, permissions);
        AccessResource[] resources =
        [
            fixture.Resource(team: "billing"),
            fixture.Resource(team: "sales"),
            fixture.Resource(team: "sales", labels: ["public"]),
            fixture.Resource(team: "sales"),
            fixture.Resource(team: "billing", inbox: "vip"),
            fixture.Resource(inbox: "vip", labels: ["public"]),
            fixture.Resource(scope: TestScopes.Create(workspaceId: "other")),
        ];
        foreach (var resource in resources)
            foreach (var permission in permissions)
                Assert.AreEqual(await fixture.AuthorizeAsync(user, permission, resource), await context.DecideAsync(permission, resource), $"{permission}");
        Assert.ThrowsExactly<ArgumentException>(() => context.DecideAsync(AccessPermissions.ConversationDelete, resources[0]).AsTask().GetAwaiter().GetResult());

        var automation = await fixture.Evaluator.LoadDecisionContextAsync(TestPrincipals.Automation(fixture.Scope), fixture.Scope, [AccessPermissions.ConversationRead]);
        Assert.AreEqual("denied:UnsupportedPrincipal", AccessFixture.Describe(await automation.DecideAsync(AccessPermissions.ConversationRead, resources[0])));
    }

    [TestMethod]
    public async Task SensitiveAndBreakGlassDecisionsAreAuditedPerResource()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User("sam");
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Supervisor);
        await fixture.SeedAsync(new AccessGrant(new GrantId("bg"), fixture.Partition, user.UserId, BuiltInRoles.Agent.Id,
            new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("ops")), justification: "incident 42 response", kind: GrantKind.BreakGlass,
            validFrom: fixture.Clock.GetUtcNow(), validUntil: fixture.Clock.GetUtcNow().AddHours(1)));
        var other = fixture.User("lee");
        await fixture.GrantAsync(other.UserId, BuiltInRoles.Agent, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("ops")));
        var plain = await fixture.Evaluator.LoadDecisionContextAsync(other, fixture.Scope, [AccessPermissions.ConversationRead]);
        for (var index = 0; index < 3; index++)
            await plain.DecideAsync(AccessPermissions.ConversationRead, fixture.Resource(team: "ops", id: $"c{index}"));
        Assert.IsEmpty(fixture.Audit.Snapshot());

        var context = await fixture.Evaluator.LoadDecisionContextAsync(user, fixture.Scope, [AccessPermissions.ConversationDelete, AccessPermissions.ConversationReply]);
        for (var index = 0; index < 3; index++)
            Assert.IsTrue((await context.DecideAsync(AccessPermissions.ConversationDelete, fixture.Resource(id: $"c{index}"))).IsPermitted);
        Assert.HasCount(3, fixture.Audit.Snapshot());
        // Supervisor holds conversation:reply, so the break-glass grant is not needed; a user without it relies on break-glass.
        var responder = fixture.User("kim");
        await fixture.SeedAsync(new AccessGrant(new GrantId("bg-2"), fixture.Partition, responder.UserId, BuiltInRoles.Agent.Id,
            new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("ops")), justification: "incident 43 response", kind: GrantKind.BreakGlass,
            validFrom: fixture.Clock.GetUtcNow(), validUntil: fixture.Clock.GetUtcNow().AddHours(1)));
        var glass = await fixture.Evaluator.LoadDecisionContextAsync(responder, fixture.Scope, [AccessPermissions.ConversationReply]);
        for (var index = 0; index < 5; index++)
            Assert.AreEqual("allowed:break-glass", AccessFixture.Describe(await glass.DecideAsync(AccessPermissions.ConversationReply, fixture.Resource(team: "ops", id: $"g{index}"))));
        Assert.AreEqual("denied:NoMatchingGrant", AccessFixture.Describe(await glass.DecideAsync(AccessPermissions.ConversationReply, fixture.Resource(team: "sales", id: "s0"))));
        var breakGlass = fixture.Audit.Snapshot().Skip(3).ToList();
        CollectionAssert.AreEqual(new[] { "g0", "g1", "g2", "g3", "g4" }, breakGlass.Select(item => item.ResourceId).ToArray(),
            "every row relying on break-glass is audited with its own resource id");
        Assert.IsTrue(breakGlass.All(item => item.UsedBreakGlass && item.Kind == AccessAuditKind.Decision));
    }

    [TestMethod]
    public async Task FallbackContextDelegatesToPointChecks()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User("ada");
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent);
        var fallback = new PointAccessDecisionContext(fixture.Evaluator, user, fixture.Scope, [AccessPermissions.ConversationRead]);
        Assert.AreEqual("allowed", AccessFixture.Describe(await fallback.DecideAsync(AccessPermissions.ConversationRead, fixture.Resource())));
        Assert.ThrowsExactly<ArgumentException>(() => new PointAccessDecisionContext(fixture.Evaluator, user, fixture.Scope, []));
    }
}
