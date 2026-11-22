using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class BreakGlassAndAuditTests
{
    private const string Reason = "Incident 4521: customer data loss investigation";

    private static async Task<(AccessFixture Fixture, UserPrincipal Responder)> CreateAsync()
    {
        var fixture = AccessFixture.Create(options: options => options.MaximumBreakGlassDuration = TimeSpan.FromHours(1));
        var onCall = await fixture.CustomRoleAsync("on-call", AccessPermissions.AccessBreakGlass);
        var responder = fixture.User("responder");
        await fixture.GrantAsync(responder.UserId, onCall);
        await fixture.GrantAsync(responder.UserId, BuiltInRoles.Viewer);
        return (fixture, responder);
    }

    private static BreakGlassRequest Request(AccessFixture fixture, PrincipalRef requester, string id = "bg-1", AccessRole? role = null, GrantScope? scope = null, TimeSpan? duration = null, string reason = Reason) =>
        new(requester, new GrantId(id), (role ?? BuiltInRoles.Supervisor).Id, scope ?? fixture.Workspace, duration ?? TimeSpan.FromMinutes(30), reason);

    [TestMethod]
    public async Task BreakGlassRequiresJustificationPermissionAndBounds()
    {
        var (fixture, responder) = await CreateAsync();
        using var _ = fixture;
        var agent = fixture.User("agent");
        await fixture.GrantAsync(agent.UserId, BuiltInRoles.Agent);
        string Refusal(AccessChangeResult<StoredAccessGrant> result) => AccessFixture.Describe(result);

        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, reason: "because"))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, reason: "          x"))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, reason: "bad\u0007justification text"))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, duration: TimeSpan.FromHours(2)))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, duration: TimeSpan.Zero))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, scope: TenantGrantScope.Instance))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, scope: new WorkspaceGrantScope(new WorkspaceId("elsewhere"))))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, role: BuiltInRoles.Owner))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, role: BuiltInRoles.Admin))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, role: new AccessRole(new RoleId("missing"), "x", [])))));
        Assert.AreEqual("refused:Invalid", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, TestPrincipals.Automation(fixture.Scope)))));
        Assert.AreEqual("refused:Forbidden", Refusal(await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, agent))));
        Assert.HasCount(12, fixture.Audit.Snapshot());
        Assert.IsTrue(fixture.Audit.Snapshot().All(e => e.Kind == AccessAuditKind.BreakGlassActivated && e.Outcome == AccessAuditOutcome.Denied));
        Assert.IsNull(fixture.Audit.Snapshot()[2].Justification);
    }

    [TestMethod]
    public async Task BreakGlassGrantsAreAuditedOnActivationAndUseAndExpireAutomatically()
    {
        var (fixture, responder) = await CreateAsync();
        using var _ = fixture;
        var contact = fixture.Resource(id: "conversation-9", team: "billing");
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(responder, AccessPermissions.ConversationReply, contact));
        Assert.IsEmpty(fixture.Audit.Snapshot());

        var activation = await fixture.Admin.ActivateBreakGlassAsync(Request(fixture, responder, scope: new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing"))));
        var stored = ((AccessChangeApplied<StoredAccessGrant>)activation.Value!).Value.Grant;
        Assert.AreEqual(GrantKind.BreakGlass, stored.Kind);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), stored.ValidFrom);
        Assert.AreEqual(fixture.Clock.GetUtcNow().AddMinutes(30), stored.ValidUntil);
        Assert.AreEqual(Reason, stored.Justification);
        var activated = fixture.Audit.Snapshot().Single();
        Assert.AreEqual(AccessAuditKind.BreakGlassActivated, activated.Kind);
        Assert.AreEqual(Reason, activated.Justification);
        Assert.AreEqual(new TeamId("billing"), activated.TeamId);

        Assert.AreEqual("allowed:break-glass", await fixture.DescribeAsync(responder, AccessPermissions.ConversationReply, contact));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(responder, AccessPermissions.ConversationReply, fixture.Resource(team: "sales")));
        // A standard grant covering the permission means the break-glass grant was not needed for that decision.
        Assert.AreEqual("allowed", await fixture.DescribeAsync(responder, AccessPermissions.ConversationRead, contact));
        var used = fixture.Audit.Snapshot();
        Assert.HasCount(2, used);
        Assert.AreEqual(AccessAuditKind.Decision, used[1].Kind);
        Assert.IsTrue(used[1].UsedBreakGlass);
        Assert.AreEqual("conversation-9", used[1].ResourceId);

        var filter = await fixture.Planner.PlanAsync(new AccessQuery(responder, AccessPermissions.ConversationReply, fixture.Scope));
        Assert.IsTrue(AccessFilterEvaluator.Matches(filter, contact));
        Assert.AreEqual(AccessAuditKind.FilterPlanned, fixture.Audit.Snapshot()[^1].Kind);
        Assert.IsTrue(fixture.Audit.Snapshot()[^1].UsedBreakGlass);

        // Break-glass grants never authorize further break-glass or management, and cannot be modified.
        Assert.AreEqual("refused:Invalid", AccessFixture.Describe(await fixture.Admin.UpdateGrantAsync(responder, stored, 1)));

        fixture.Clock.Advance(TimeSpan.FromMinutes(30));
        var before = fixture.Audit.Snapshot().Count;
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(responder, AccessPermissions.ConversationReply, contact));
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(responder, AccessPermissions.ConversationReply, fixture.Scope)));
        Assert.HasCount(before, fixture.Audit.Snapshot());
    }

    [TestMethod]
    public async Task EverySensitiveDecisionIsRecordedExactlyOnce()
    {
        using var fixture = AccessFixture.Create();
        var supervisor = fixture.User("supervisor");
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        var resource = fixture.Resource(id: "contact-7", team: "billing", inbox: "email");

        Assert.AreEqual("allowed", await fixture.DescribeAsync(supervisor, AccessPermissions.ConversationRead, resource));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(supervisor, AccessPermissions.InboxManage, resource));
        Assert.IsEmpty(fixture.Audit.Snapshot());

        Assert.AreEqual("allowed", await fixture.DescribeAsync(supervisor, AccessPermissions.ContactExport, resource));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(supervisor, AccessPermissions.ContactReveal, resource));
        Assert.AreEqual("denied:UnsupportedPrincipal", await fixture.DescribeAsync(TestPrincipals.System(fixture.Scope, "exporter"), AccessPermissions.ContactExport, resource));
        var events = fixture.Audit.Snapshot();
        Assert.HasCount(3, events);
        Assert.AreEqual((AccessAuditKind.Decision, AccessAuditOutcome.Allowed, AccessPermissions.ContactExport), (events[0].Kind, events[0].Outcome, events[0].Permission!.Value));
        Assert.AreEqual(fixture.Scope, events[0].Scope);
        Assert.AreEqual(new TeamId("billing"), events[0].TeamId);
        Assert.AreEqual(new InboxId("email"), events[0].InboxId);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), events[0].OccurredAt);
        Assert.AreEqual(AccessDenialReason.NoMatchingGrant, events[1].DenialReason);
        Assert.AreEqual(new AccessActor(AccessActorKind.System, "exporter"), events[2].Actor);
        Assert.AreEqual(3, events.Select(e => e.Id).Distinct().Count());
    }

    [TestMethod]
    public async Task AuditFailuresFailTheDecisionClosed()
    {
        using var fixture = AccessFixture.Create(auditCapacity: 1);
        var supervisor = fixture.User("supervisor");
        await fixture.GrantAsync(supervisor.UserId, BuiltInRoles.Supervisor);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(supervisor, AccessPermissions.ContactExport));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.AuthorizeAsync(supervisor, AccessPermissions.ContactExport));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(supervisor, AccessPermissions.ConversationRead));
    }
}
