using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing.Tests;

[TestClass]
public sealed class ModelAndCodecTests
{
    private static readonly RoutingPolicyId Id = new("p");

    public static IEnumerable<object[]> Policies =>
    [
        [new RoutingPolicy(Id, "Round robin", RoutingStrategy.RoundRobin)],
        [new RoutingPolicy(Id, "Least busy", RoutingStrategy.LeastBusy, new TeamId("billing"), maximumWait: TimeSpan.FromMinutes(20))],
        [new RoutingPolicy(Id, "Manual", RoutingStrategy.Manual, onAgentOffline: OfflineReassignment.ReturnToQueue)],
        [new RoutingPolicy(Id, "Skills", new SkillsBasedStrategy([new SkillRule(new LabelSkillMatch(new LabelId("vip")), "billing", 3),
            new SkillRule(new AttributeSkillMatch("language", "de"), "german", 1)]),
            overflow: [new OverflowRule(TimeSpan.FromMinutes(1), new OverflowToTeam(new TeamId("tier2"))), new OverflowRule(TimeSpan.FromMinutes(2), new OverflowToInbox(new InboxId("x")))])],
        [new RoutingPolicy(Id, "Sticky", new StickyOwnerStrategy(TimeSpan.FromDays(3), RoutingStrategy.Manual))],
    ];

    [TestMethod]
    [DynamicData(nameof(Policies))]
    public void PoliciesRoundTrip(RoutingPolicy policy)
    {
        var json = RoutingPolicyCodec.Serialize(policy);
        StringAssert.StartsWith(json, "{\"v\":1,", StringComparison.Ordinal);
        Assert.AreEqual(policy, RoutingPolicyCodec.Deserialize(Id, json));
    }

    [TestMethod]
    [DataRow("""{"v":2,"name":"x","strategy":{"k":"manual"},"overflow":[],"offline":1}""")]
    [DataRow("""{"v":1,"name":"x","strategy":{"k":"random"},"overflow":[],"offline":1}""")]
    [DataRow("""{"v":1,"name":"x","strategy":{"k":"manual","extra":1},"overflow":[],"offline":1}""")]
    [DataRow("""{"v":1,"name":"x","strategy":{"k":"manual"},"overflow":[],"offline":1,"other":true}""")]
    [DataRow("""{"v":1,"name":"x","strategy":{"k":"manual"},"overflow":[],"offline":9}""")]
    [DataRow("""{"v":1,"name":"x","strategy":{"k":"sticky","window":60,"fallback":{"k":"sticky","window":60,"fallback":{"k":"manual"}}},"overflow":[],"offline":1}""")]
    [DataRow("""{"v":1,"name":"x","strategy":{"k":"manual"},"overflow":[{"after":120,"target":{"k":"team","team":"a"}},{"after":60,"target":{"k":"team","team":"b"}}],"offline":1}""")]
    [DataRow("""{"v":1,"name":"x","strategy":{"k":"manual"},"overflow":[{"after":60,"target":{"k":"queue","team":"a"}}],"offline":1}""")]
    [DataRow("""{"v":1,"name":"","strategy":{"k":"manual"},"overflow":[],"offline":1}""")]
    public void InvalidPolicyDocumentsAreRejected(string json) =>
        Assert.ThrowsExactly<FormatException>(() => RoutingPolicyCodec.Deserialize(Id, json));

    [TestMethod]
    public void AgentSettingsRoundTripAndValidate()
    {
        var settings = new AgentRoutingSettings(new UserId("ann"), 3, [new(new ChannelKind("live-chat"), 5), new(new ChannelKind("email"), 0)],
            [new AgentSkill("german", 2), new AgentSkill("billing", 7)]);
        Assert.AreEqual(settings, AgentSettingsCodec.Deserialize(settings.Agent, 3, AgentSettingsCodec.Serialize(settings)));
        Assert.AreEqual(0, settings.CapacityFor(new ChannelKind("email")));
        Assert.AreEqual(3, settings.CapacityFor(new ChannelKind("sms")));
        CollectionAssert.AreEqual(new[] { "billing", "german" }, settings.Skills.Select(skill => skill.Name).ToArray());
        Assert.IsTrue(settings.HasSkills(new Dictionary<string, int> { ["billing"] = 7 }));
        Assert.IsFalse(settings.HasSkills(new Dictionary<string, int> { ["billing"] = 8 }));
        Assert.ThrowsExactly<FormatException>(() => AgentSettingsCodec.Deserialize(settings.Agent, 3, """{"v":1,"capacities":[],"skills":[{"name":"Bad","level":1}]}"""));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AgentSkill("x", 11));
        Assert.ThrowsExactly<ArgumentException>(() => new AgentSkill("Has Space"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new AgentRoutingSettings(new UserId("a"), 1001));
        Assert.ThrowsExactly<ArgumentException>(() => new AgentRoutingSettings(new UserId("a"), 1, skills: Enumerable.Range(0, 33).Select(i => new AgentSkill($"s{i}"))));
    }

    [TestMethod]
    public void PolicyValidationIsBounded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new StickyOwnerStrategy(TimeSpan.FromDays(1), new StickyOwnerStrategy(TimeSpan.FromDays(1), RoutingStrategy.Manual)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new StickyOwnerStrategy(TimeSpan.FromMilliseconds(1500), RoutingStrategy.Manual));
        Assert.ThrowsExactly<ArgumentException>(() => new SkillsBasedStrategy([]));
        Assert.ThrowsExactly<ArgumentException>(() => new RoutingPolicy(Id, "x", RoutingStrategy.Manual,
            overflow: Enumerable.Range(1, 9).Select(i => new OverflowRule(TimeSpan.FromMinutes(i), new OverflowToTeam(new TeamId("t"))))));
        Assert.ThrowsExactly<ArgumentException>(() => new RoutingPolicy(Id, "bad\u0001name", RoutingStrategy.Manual));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new RoutingPolicy(Id, "x", RoutingStrategy.Manual, maximumWait: TimeSpan.FromDays(31)));
    }

    [TestMethod]
    public void ReferenceSelectionRotatesAndRespectsCapacity()
    {
        var request = new RoutingReservationRequest(new ConversationId("c"), 1, new InboxId("i"), new ContactId("k"), new ChannelKind("email"), "i",
            RoutingSelection.RoundRobin, [new RoutingCandidate(new UserId("b"), 1), new RoutingCandidate(new UserId("a"), 2), new RoutingCandidate(new UserId("c"), 1)], null,
            TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, request.Candidates.Select(c => c.Agent.Value).ToArray());
        var loads = new Dictionary<UserId, int> { [new UserId("b")] = 1 };
        Assert.AreEqual("a", request.Select(loads, null)!.Agent.Value);
        Assert.AreEqual("c", request.Select(loads, "a")!.Agent.Value);
        Assert.AreEqual("a", request.Select(loads, "c")!.Agent.Value);
        Assert.IsNull(request.Select(new Dictionary<UserId, int> { [new UserId("a")] = 2, [new UserId("b")] = 1, [new UserId("c")] = 1 }, null));
        Assert.ThrowsExactly<ArgumentException>(() => new RoutingReservationRequest(new ConversationId("c"), 1, new InboxId("i"), new ContactId("k"), new ChannelKind("email"), "i",
            RoutingSelection.RoundRobin, [], null, TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void PermissionsAreBuiltInAndHeldByTheExpectedRoles()
    {
        CollectionAssert.AreEquivalent(new[] { "agent-status:manage", "agent-status:update", "routing:manage" },
            RoutingPermissions.Definitions.Select(definition => definition.Permission.Value).ToArray());
        Assert.IsTrue(BuiltInRoles.Agent.Includes(RoutingPermissions.StatusUpdate));
        Assert.IsFalse(BuiltInRoles.Agent.Includes(RoutingPermissions.StatusManage));
        Assert.IsTrue(BuiltInRoles.Supervisor.Includes(RoutingPermissions.StatusManage));
        Assert.IsTrue(BuiltInRoles.Supervisor.Includes(RoutingPermissions.Manage));
        Assert.IsTrue(BuiltInRoles.Admin.Includes(RoutingPermissions.Manage));
        Assert.IsFalse(BuiltInRoles.LightAgent.Includes(RoutingPermissions.StatusUpdate));
        // Registering the identical definitions again is idempotent.
        Assert.AreEqual(AccessPermissions.Definitions.Count, PermissionCatalog.Create(RoutingPermissions.Definitions).Definitions.Count);
    }
}
