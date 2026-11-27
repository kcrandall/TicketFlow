using TicketFlow.Core;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class ConditionTests
{
    private static AccessCondition Nest(int depth)
    {
        AccessCondition condition = new AssigneeIsSelfCondition();
        for (var level = 1; level < depth; level++)
            condition = new NotCondition(condition);
        return condition;
    }

    [TestMethod]
    public void DepthAndSizeAreBoundedByConstruction()
    {
        Assert.AreEqual(AccessCondition.MaximumDepth, Nest(AccessCondition.MaximumDepth).Depth);
        Assert.ThrowsExactly<ArgumentException>(() => new NotCondition(Nest(AccessCondition.MaximumDepth)));
        Assert.ThrowsExactly<ArgumentException>(() => new AndCondition(new AssigneeIsSelfCondition(), Nest(AccessCondition.MaximumDepth)));

        var leaves = Enumerable.Range(0, AccessCondition.MaximumNodes - 1).Select(index => (AccessCondition)new AttributeEqualsCondition("k", $"v{index}")).ToArray();
        var widest = new OrCondition(leaves);
        Assert.AreEqual(AccessCondition.MaximumNodes, widest.NodeCount);
        Assert.AreEqual(2, widest.Depth);
        Assert.ThrowsExactly<ArgumentException>(() => new OrCondition([.. leaves, new TeamInPrincipalTeamsCondition()]));
        Assert.ThrowsExactly<ArgumentException>(() => new AndCondition(widest, new TeamInPrincipalTeamsCondition()));
        Assert.ThrowsExactly<ArgumentException>(() => new AndCondition());
    }

    [TestMethod]
    public void LeavesValidateTheirOperands()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AttributeEqualsCondition("Tier", "vip"));
        Assert.ThrowsExactly<ArgumentException>(() => new AttributeEqualsCondition("tier", new string('v', 257)));
        Assert.ThrowsExactly<ArgumentException>(() => new AttributeInCondition("tier"));
        Assert.ThrowsExactly<ArgumentException>(() => new AttributeInCondition("tier", Enumerable.Range(0, 65).Select(index => $"v{index}")));
        Assert.ThrowsExactly<ArgumentException>(() => new TimeWindowCondition(new(9, 0), new(9, 0)));
        Assert.ThrowsExactly<ArgumentException>(() => new HasLabelCondition(default));
        CollectionAssert.AreEqual(new[] { "a", "b" }, new AttributeInCondition("tier", "b", "a", "b").Values.ToArray());
        Assert.AreEqual(new AndCondition(new AttributeInCondition("tier", "b", "a")), new AndCondition(new AttributeInCondition("tier", "a", "b")));
    }

    [TestMethod]
    public async Task MissingAttributesFailClosedForAllowAndDeny()
    {
        using var fixture = AccessFixture.Create();
        var allowed = fixture.User("allowed");
        var denied = fixture.User("denied");
        await fixture.GrantAsync(allowed.UserId, BuiltInRoles.Agent, condition: new AttributeEqualsCondition("tier", "standard"));
        await fixture.GrantAsync(denied.UserId, BuiltInRoles.Agent);
        await fixture.GrantAsync(denied.UserId, BuiltInRoles.Agent, effect: GrantEffect.Deny, condition: new AttributeEqualsCondition("tier", "vip"));
        var standard = fixture.Resource(attributes: new() { ["tier"] = "standard" });
        var vip = fixture.Resource(attributes: new() { ["tier"] = "vip" });
        var unknown = fixture.Resource(attributes: new() { ["region"] = "eu" });

        Assert.AreEqual("allowed", await fixture.DescribeAsync(allowed, AccessPermissions.ConversationReply, standard));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(allowed, AccessPermissions.ConversationReply, vip));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(allowed, AccessPermissions.ConversationReply, unknown));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(denied, AccessPermissions.ConversationReply, standard));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(denied, AccessPermissions.ConversationReply, vip));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(denied, AccessPermissions.ConversationReply, unknown));
    }

    [TestMethod]
    public async Task NegationKeepsUnknownUnknown()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, condition: new NotCondition(new HasLabelCondition(new LabelId("vip"))));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(labels: ["billing"])));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource(labels: ["vip"])));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply, fixture.Resource()));
    }

    [TestMethod]
    public async Task PrincipalReferencesBindToTheActingUser()
    {
        using var fixture = AccessFixture.Create();
        var ada = fixture.User("ada", "billing");
        var client = fixture.Client();
        await fixture.GrantAsync(ada.UserId, BuiltInRoles.Agent, condition: new OrCondition(new AssigneeIsSelfCondition(), new TeamInPrincipalTeamsCondition()));
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Agent, condition: new AssigneeIsSelfCondition());
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Viewer);
        await fixture.GrantAsync(client.ClientId, BuiltInRoles.Viewer, effect: GrantEffect.Deny, condition: new TeamInPrincipalTeamsCondition());

        Assert.AreEqual("allowed", await fixture.DescribeAsync(ada, AccessPermissions.ConversationReply, fixture.Resource(assignee: "ada")));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(ada, AccessPermissions.ConversationReply, fixture.Resource(team: "billing", assignee: "grace")));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(ada, AccessPermissions.ConversationReply, fixture.Resource(team: "sales", assignee: "grace")));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(ada, AccessPermissions.ConversationReply, fixture.Resource()));
        // A self reference is unknown for API clients: their allow never applies and their deny always does.
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(client, AccessPermissions.ConversationReply, fixture.Resource(assignee: "crm")));
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(client, AccessPermissions.ConversationRead, fixture.Resource()));
    }

    [TestMethod]
    public async Task TimeWindowsUseTheClockAndWrapMidnight()
    {
        using var fixture = AccessFixture.Create();
        var user = fixture.User();
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, condition: new TimeWindowCondition(new(22, 0), new(6, 0)));
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 1, 1, 21, 59, 59, TimeSpan.Zero));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        fixture.Clock.Advance(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        Assert.IsTrue(new TimeWindowCondition(new(9, 0), new(17, 0)).Contains(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(5))) is false);
    }

    [TestMethod]
    public void CodecRoundTripsWithExplicitDiscriminators()
    {
        var condition = new AndCondition(
            new OrCondition(new AssigneeIsSelfCondition(), new TeamInPrincipalTeamsCondition()),
            new NotCondition(new AttributeInCondition("tier", "vip", "enterprise")),
            new AttributeEqualsCondition("region", "eu \"west\""),
            new HasLabelCondition(new LabelId("escalated")),
            new TimeWindowCondition(new(22, 0, 30), new(6, 15)));
        var json = AccessConditionCodec.Serialize(condition);
        StringAssert.StartsWith(json, "{\"v\":1,\"c\":{\"op\":\"and\"", StringComparison.Ordinal);
        foreach (var op in new[] { "\"or\"", "\"not\"", "\"assignee-self\"", "\"team-in-principal\"", "\"attr-in\"", "\"attr-eq\"", "\"label\"", "\"time\"" })
            StringAssert.Contains(json, op, StringComparison.Ordinal);
        Assert.AreEqual(condition, AccessConditionCodec.Deserialize(json));
    }

    [TestMethod]
    [DataRow("{\"v\":2,\"c\":{\"op\":\"assignee-self\"}}")]
    [DataRow("{\"v\":1,\"c\":{\"op\":\"wildcard\"}}")]
    [DataRow("{\"v\":1,\"c\":{\"op\":\"assignee-self\",\"extra\":true}}")]
    [DataRow("{\"v\":1,\"c\":{\"op\":\"attr-eq\",\"key\":\"tier\"}}")]
    [DataRow("{\"v\":1,\"c\":{\"op\":\"attr-eq\",\"key\":\"tier\",\"value\":1}}")]
    [DataRow("{\"v\":1,\"c\":{\"op\":\"and\",\"items\":[]}}")]
    [DataRow("{\"v\":1,\"c\":{\"op\":\"attr-in\",\"key\":\"tier\",\"values\":[]}}")]
    [DataRow("{\"v\":1,\"c\":{\"op\":\"time\",\"start\":\"25:00:00\",\"end\":\"01:00:00\"}}")]
    [DataRow("{\"v\":1}")]
    [DataRow("[]")]
    [DataRow("not json")]
    public void CodecRejectsMalformedPayloads(string json) =>
        Assert.ThrowsExactly<FormatException>(() => AccessConditionCodec.Deserialize(json));

    [TestMethod]
    public void CodecEnforcesDepthAndSizeBounds()
    {
        var deep = "{\"op\":\"assignee-self\"}";
        for (var level = 0; level < AccessCondition.MaximumDepth; level++)
            deep = "{\"op\":\"not\",\"item\":" + deep + "}";
        Assert.ThrowsExactly<FormatException>(() => AccessConditionCodec.Deserialize("{\"v\":1,\"c\":" + deep + "}"));
        var wide = string.Join(",", Enumerable.Repeat("{\"op\":\"assignee-self\"}", AccessCondition.MaximumNodes));
        Assert.ThrowsExactly<FormatException>(() => AccessConditionCodec.Deserialize("{\"v\":1,\"c\":{\"op\":\"or\",\"items\":[" + wide + "]}}"));
        Assert.ThrowsExactly<FormatException>(() => AccessConditionCodec.Deserialize(new string(' ', AccessConditionCodec.MaximumEncodedBytes + 1)));
        Assert.AreEqual(Nest(AccessCondition.MaximumDepth), AccessConditionCodec.Deserialize(AccessConditionCodec.Serialize(Nest(AccessCondition.MaximumDepth))));
    }

    [TestMethod]
    public async Task AccessEvaluatorEvaluatesAdministrativePermissionsConservatively()
    {
        var staffIdentityManage = new Permission("staff-identity:manage");
        using var fixture = AccessFixture.Create(services => services.AddTicketFlow().AddAccessPermissions(new PermissionDefinition(staffIdentityManage)));
        var adminPerms = new[]
        {
            AccessPermissions.AccessManage,
            AccessPermissions.AccessManageTenant,
            AccessPermissions.AccessBreakGlass,
            staffIdentityManage,
        };
        var allAdminRole = await fixture.CustomRoleAsync("all-admin-ws", AccessPermissions.AccessManage, AccessPermissions.AccessBreakGlass, staffIdentityManage);
        var target = fixture.Resource();

        // Add a field policy on staff-identity:manage and on ContactRead with AccessManage reveal permission to exercise both MaskAsync/BatchContext.Evaluate and RevealAsync paths.
        Assert.IsInstanceOfType<StoreApplied<StoredAccessFieldPolicy>>((await fixture.Policies.CreateAsync(fixture.Scope,
            new AccessFieldPolicy(new FieldPath("staff.email"), [staffIdentityManage], [], AccessPermissions.AccessManage))).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessFieldPolicy>>((await fixture.Policies.CreateAsync(fixture.Scope,
            new AccessFieldPolicy(new FieldPath("contact.secret"), [AccessPermissions.ContactRead], [], AccessPermissions.AccessManage))).Value);

        // 1. Conditional Deny (AssigneeIsSelfCondition / TeamInPrincipalTeamsCondition) blocks administrative permissions in AuthorizeAsync, BatchContext.DecideAsync, and RevealAsync.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("eval-deny-assignee", new AssigneeIsSelfCondition()),
            ("eval-deny-team", new TeamInPrincipalTeamsCondition()),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Owner, effect: GrantEffect.Deny, condition: condition);

            var batch = await fixture.Evaluator.LoadDecisionContextAsync(actor, fixture.Scope, adminPerms);
            foreach (var perm in adminPerms)
            {
                Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(actor, perm, target), $"{name}:{perm}");
                Assert.AreEqual("denied:ExplicitDeny", AccessFixture.Describe(await batch.DecideAsync(perm, target)), $"{name}:batch:{perm}");
            }

            var revealAdminRead = await fixture.Evaluator.RevealAsync(new RevealRequest(actor, target, staffIdentityManage, new FieldPath("staff.email")));
            Assert.IsTrue(revealAdminRead is RevealRefused { Reason: RevealRefusal.ReadDenied }, name);

            // ContactRead is non-administrative and allowed (Owner), but RevealPermission is AccessManage (administrative), which must fail closed.
            var revealAdminPerm = await fixture.Evaluator.RevealAsync(new RevealRequest(actor, target, AccessPermissions.ContactRead, new FieldPath("contact.secret")));
            Assert.IsTrue(revealAdminPerm is RevealRefused { Reason: RevealRefusal.RevealDenied }, name);
        }

        // 2. Negated conditional Allow (Not(AssigneeIsSelfCondition) / Not(TeamInPrincipalTeamsCondition)) must NOT grant administrative permissions.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("eval-allow-not-assignee", new NotCondition(new AssigneeIsSelfCondition())),
            ("eval-allow-not-team", new NotCondition(new TeamInPrincipalTeamsCondition())),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance, condition: condition);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Viewer);

            var batch = await fixture.Evaluator.LoadDecisionContextAsync(actor, fixture.Scope, adminPerms);
            foreach (var perm in adminPerms)
            {
                Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(actor, perm, target), $"{name}:{perm}");
                Assert.AreEqual("denied:NoMatchingGrant", AccessFixture.Describe(await batch.DecideAsync(perm, target)), $"{name}:batch:{perm}");
            }

            var revealAdminRead = await fixture.Evaluator.RevealAsync(new RevealRequest(actor, target, staffIdentityManage, new FieldPath("staff.email")));
            Assert.IsTrue(revealAdminRead is RevealRefused { Reason: RevealRefusal.ReadDenied }, name);

            var revealAdminPerm = await fixture.Evaluator.RevealAsync(new RevealRequest(actor, target, AccessPermissions.ContactRead, new FieldPath("contact.secret")));
            Assert.IsTrue(revealAdminPerm is RevealRefused { Reason: RevealRefusal.RevealDenied }, name);
        }

        // 3. Sub-scope Deny (Inbox/Team) blocks workspace-level administrative permissions; cross-workspace Deny blocks AccessManageTenant.
        foreach (var (name, denyScope) in new (string, GrantScope)[]
        {
            ("eval-subscope-inbox", new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip"))),
            ("eval-subscope-team", new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing"))),
        })
        {
            var actor = fixture.User(name);
            await fixture.GrantAsync(actor.UserId, allAdminRole);
            await fixture.GrantAsync(actor.UserId, allAdminRole, denyScope, GrantEffect.Deny);

            var batch = await fixture.Evaluator.LoadDecisionContextAsync(actor, fixture.Scope, [AccessPermissions.AccessManage, AccessPermissions.AccessBreakGlass, staffIdentityManage]);
            foreach (var perm in new[] { AccessPermissions.AccessManage, AccessPermissions.AccessBreakGlass, staffIdentityManage })
            {
                Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(actor, perm, target), $"{name}:{perm}");
                Assert.AreEqual("denied:ExplicitDeny", AccessFixture.Describe(await batch.DecideAsync(perm, target)), $"{name}:batch:{perm}");
            }
        }

        var wsB = new WorkspaceId("ws-b");
        var tenantActor = fixture.User("eval-cross-ws-tenant");
        await fixture.GrantAsync(tenantActor.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(tenantActor.UserId, BuiltInRoles.Owner, new WorkspaceGrantScope(wsB), GrantEffect.Deny);
        var tenantBatch = await fixture.Evaluator.LoadDecisionContextAsync(tenantActor, fixture.Scope, [AccessPermissions.AccessManageTenant, AccessPermissions.ConversationRead]);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(tenantActor, AccessPermissions.AccessManageTenant, target));
        Assert.AreEqual("denied:ExplicitDeny", AccessFixture.Describe(await tenantBatch.DecideAsync(AccessPermissions.AccessManageTenant, target)));
        // Non-tenant workspace permission in wsA is unaffected by the wsB deny.
        Assert.AreEqual("allowed", AccessFixture.Describe(await tenantBatch.DecideAsync(AccessPermissions.ConversationRead, target)));

        // 4. Non-administrative permissions still evaluate Live.
        var agent = fixture.User("eval-live-agent", "ops");
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent, condition: new NotCondition(new AssigneeIsSelfCondition()));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(agent, AccessPermissions.ConversationReply, fixture.Resource(assignee: "someone-else")));
    }
}
