using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Access.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Workspaces.StoreContracts;

/// <summary>
/// Store lists driven by real access plans equal point checks: for seeded random grants (team/inbox/workspace/tenant scopes, deny,
/// conditions, break-glass, deleted roles), every principal's inbox and team list contains exactly the records whose
/// <c>inbox:read</c> / <c>team:read</c> point check is permitted.
/// </summary>
public static class AccessFilteredListParity
{
    public static async Task VerifyAsync(IInboxStore inboxes, ITeamStore teams, int seed)
    {
        var scope = TestScopes.Create(workspaceId: $"parity-{seed}");
        var clock = TestClock.Create();
        var random = new Random(seed);
        clock.Advance(TimeSpan.FromMinutes(random.Next(24 * 60)));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(new InMemoryAccessAuditSink(1_000_000));
        services.AddAccessPermissions(new PermissionDefinition("triage:run"));
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores();
        await using var provider = services.BuildServiceProvider();
        await using var request = provider.CreateAsyncScope();
        var roles = provider.GetRequiredService<IAccessRoleStore>();
        var grants = provider.GetRequiredService<IAccessGrantStore>();
        await roles.CreateAsync(scope, RandomAccessFixture.CustomRole);
        await roles.CreateAsync(scope, RandomAccessFixture.DeletedRole);
        await roles.DeleteAsync(scope, RandomAccessFixture.DeletedRole.Id, 1);
        foreach (var grant in RandomAccessFixture.Grants(random, scope, clock.GetUtcNow(), 40))
            Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await grants.CreateAsync(grant)).Value);
        var planner = request.ServiceProvider.GetRequiredService<IAccessQueryPlanner>();
        var evaluator = request.ServiceProvider.GetRequiredService<IAccessEvaluator>();

        string[] inboxIds = [.. RandomAccessFixture.Inboxes, "i-extra"];
        string[] teamIds = [.. RandomAccessFixture.Teams, "t-extra"];
        foreach (var id in inboxIds)
            Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await inboxes.CreateAsync(scope, WorkspaceStoreContract.SampleInbox(id))).Value);
        foreach (var id in teamIds)
            Assert.IsInstanceOfType<StoreApplied<StoredTeam>>((await teams.CreateAsync(scope, new Team(new TeamId(id), "Team"))).Value);

        int permitted = 0, denied = 0;
        foreach (var principal in RandomAccessFixture.Principals(random, scope))
        {
            var inboxFilter = await planner.PlanAsync(new AccessQuery(principal, WorkspacePermissions.InboxRead, scope));
            var listedInboxes = (await inboxes.ListAsync(scope, inboxFilter, new(200))).Items.Select(item => item.Inbox.Id.Value).ToArray();
            var expectedInboxes = new List<string>();
            foreach (var id in inboxIds.Order(StringComparer.Ordinal))
                if ((await evaluator.AuthorizeAsync(new AccessRequest(principal, WorkspacePermissions.InboxRead, WorkspaceResources.Inbox(scope, new InboxId(id))))).IsPermitted)
                    expectedInboxes.Add(id);
            CollectionAssert.AreEqual(expectedInboxes, listedInboxes, $"seed {seed} inboxes for {principal.GetType().Name}");

            var teamFilter = await planner.PlanAsync(new AccessQuery(principal, WorkspacePermissions.TeamRead, scope));
            var listedTeams = (await teams.ListAsync(scope, teamFilter, new(200))).Items.Select(item => item.Id.Value).ToArray();
            var expectedTeams = new List<string>();
            foreach (var id in teamIds.Order(StringComparer.Ordinal))
                if ((await evaluator.AuthorizeAsync(new AccessRequest(principal, WorkspacePermissions.TeamRead, WorkspaceResources.Team(scope, new TeamId(id))))).IsPermitted)
                    expectedTeams.Add(id);
            CollectionAssert.AreEqual(expectedTeams, listedTeams, $"seed {seed} teams for {principal.GetType().Name}");
            permitted += expectedInboxes.Count + expectedTeams.Count;
            denied += inboxIds.Length + teamIds.Length - expectedInboxes.Count - expectedTeams.Count;
        }
        Assert.IsGreaterThan(0, permitted, $"seed {seed} should permit some records.");
        Assert.IsGreaterThan(0, denied, $"seed {seed} should deny some records.");
    }
}
