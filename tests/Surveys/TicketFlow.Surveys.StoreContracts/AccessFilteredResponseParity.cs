using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Access.StoreContracts;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Surveys.StoreContracts;

/// <summary>
/// Store response lists and aggregates driven by real access plans equal point checks: for seeded random grants, every principal sees
/// exactly the responses whose point check over <see cref="SurveyResources.Response"/> is permitted.
/// </summary>
public static class AccessFilteredResponseParity
{
    public static async Task VerifyAsync(ISurveyStore store, int seed)
    {
        var scope = TestScopes.Create(workspaceId: $"survey-parity-{seed}-{Guid.NewGuid():N}");
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
        var (_, responses) = await SurveyStoreContract.SeedResponsesAsync(store, 40, seed, scope);
        var permission = AccessPermissions.ConversationRead;
        var from = SurveyStoreContract.Now.AddDays(-1);
        var to = SurveyStoreContract.Now.AddDays(60);

        int permitted = 0, denied = 0;
        foreach (var principal in RandomAccessFixture.Principals(random, scope))
        {
            var filter = await planner.PlanAsync(new AccessQuery(principal, permission, scope));
            var expected = new List<SurveyResponse>();
            foreach (var response in responses)
                if ((await evaluator.AuthorizeAsync(new AccessRequest(principal, permission, SurveyResources.Response(scope, response)))).IsPermitted)
                    expected.Add(response);
            var listed = await store.ListResponsesAsync(scope, new SurveyResponseQuery(from, to, limit: 200), filter);
            CollectionAssert.AreEquivalent(expected.Select(item => item.Invitation).ToArray(), listed.Items.Select(item => item.Response.Invitation).ToArray(),
                $"seed {seed} for {principal.GetType().Name}");
            var aggregate = await store.AggregateAsync(scope, new SurveyAggregateQuery(SurveyKind.Csat, from, to), filter);
            Assert.AreEqual(expected.Count(item => item.Kind == SurveyKind.Csat), aggregate.Rows.Sum(row => row.Responses), $"seed {seed} aggregate");
            permitted += expected.Count;
            denied += responses.Count - expected.Count;
        }
        Assert.IsGreaterThan(0, permitted, $"seed {seed} should permit some responses.");
        Assert.IsGreaterThan(0, denied, $"seed {seed} should deny some responses.");
    }
}
