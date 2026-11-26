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
}
