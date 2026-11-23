using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Access.StoreContracts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Reporting.StoreContracts;

/// <summary>
/// Store aggregates, fact lists and backlog counts driven by real access plans equal point checks: for seeded random grants, every
/// principal sees exactly the rows whose point check over <see cref="ReportResources"/> is permitted.
/// </summary>
public static class AccessFilteredReportParity
{
    public static async Task VerifyAsync(IReportingStore store, int seed)
    {
        var scope = TestScopes.Create(workspaceId: $"report-parity-{seed}-{Guid.NewGuid():N}");
        var clock = TestClock.Create();
        var random = new Random(seed);
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

        var facts = new List<ReportFact>();
        var states = new List<ReportConversationState>();
        for (var index = 0; index < 40; index++)
        {
            string? Pick(string[] values) => random.Next(5) == 0 ? null : values[random.Next(values.Length)];
            var labels = RandomAccessFixture.Labels.Where(_ => random.Next(3) == 0).ToArray();
            var inbox = RandomAccessFixture.Inboxes[random.Next(RandomAccessFixture.Inboxes.Length)];
            var team = Pick(RandomAccessFixture.Teams);
            var agent = Pick(RandomAccessFixture.Users);
            var conversation = $"c{index}";
            facts.Add(ReportingStoreContract.Count(ReportMetric.ConversationCreated, ReportingStoreContract.Day.AddMinutes(index * 7), conversation,
                ReportingStoreContract.Dimensions(inbox, team, agent, labels: labels)));
            var state = ReportingStoreContract.State(conversation, inbox: inbox, team: team, assignee: agent, labels: labels);
            states.Add(state);
            Assert.AreEqual("applied:1:1", ReportingStoreContract.W(await store.ApplyAsync(scope, new ReportProjection(state, [facts[^1]]), null)));
        }
        var permission = AccessPermissions.ConversationRead;
        var whole = ReportingStoreContract.Whole(ReportingStoreContract.Day, ReportingStoreContract.Day.AddDays(1), metrics: ReportMetric.ConversationCreated);

        int permitted = 0, denied = 0;
        foreach (var principal in RandomAccessFixture.Principals(random, scope))
        {
            var filter = await planner.PlanAsync(new AccessQuery(principal, permission, scope));
            async Task<bool> AllowedAsync(AccessResource resource) => (await evaluator.AuthorizeAsync(new AccessRequest(principal, permission, resource))).IsPermitted;
            var label = $"seed {seed} for {principal.GetType().Name}";

            long expectedRollups = 0;
            var expectedFacts = new List<Guid>();
            foreach (var fact in facts)
            {
                if (await AllowedAsync(ReportResources.Rollup(scope, fact.Dimensions.Inbox, fact.Dimensions.Team, fact.Dimensions.Agent)))
                    expectedRollups++;
                if (await AllowedAsync(ReportResources.Fact(scope, fact)))
                    expectedFacts.Add(fact.SourceId);
            }
            long expectedBacklog = 0;
            foreach (var state in states)
                if (await AllowedAsync(ReportResources.Conversation(scope, state)))
                    expectedBacklog++;

            Assert.AreEqual(expectedRollups, ReportingStoreContract.Total(await store.AggregateAsync(scope, whole, filter), ReportMetric.ConversationCreated), label + " aggregate");
            var listed = await store.ListFactsAsync(scope, new ReportFactQuery(ReportingStoreContract.Day, ReportingStoreContract.Day.AddDays(1), limit: 1000), filter);
            CollectionAssert.AreEquivalent(expectedFacts, listed.Items.Select(fact => fact.SourceId).ToList(), label + " facts");
            Assert.AreEqual(expectedBacklog, await store.CountBacklogAsync(scope, ReportFilter.None, filter), label + " backlog");
            permitted += expectedFacts.Count;
            denied += facts.Count - expectedFacts.Count;
        }
        Assert.IsGreaterThan(0, permitted, $"seed {seed} should permit some rows.");
        Assert.IsGreaterThan(0, denied, $"seed {seed} should deny some rows.");
    }
}
