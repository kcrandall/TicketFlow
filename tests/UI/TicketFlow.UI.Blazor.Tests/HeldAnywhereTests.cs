using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.UI.Blazor.Tests;

/// <summary>The additive Access API the navigation snapshot uses: permissions held on any resource (team- and inbox-scoped grants).</summary>
[TestClass]
public sealed class HeldAnywhereTests
{
    [TestMethod]
    public async Task TeamAndInboxScopedGrantsCountAsHeld()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTicketFlow().AddAccessControl().AddInMemoryAccessStores();
        await using var provider = services.BuildServiceProvider();
        var scope = UiTestSupport.Scope;
        var partition = AccessPartition.From(scope);
        var grants = provider.GetRequiredService<IAccessGrantStore>();
        await grants.CreateAsync(new AccessGrant(new GrantId("team"), partition, new UserId("teamed"), BuiltInRoles.Agent.Id, new TeamGrantScope(scope.WorkspaceId, new TeamId("tier1"))));
        await grants.CreateAsync(new AccessGrant(new GrantId("deny"), partition, new UserId("teamed"), BuiltInRoles.Viewer.Id, new WorkspaceGrantScope(scope.WorkspaceId), GrantEffect.Deny));
        await using var request = provider.CreateAsyncScope();
        var planner = request.ServiceProvider.GetRequiredService<IAccessQueryPlanner>();

        var teamed = new UserPrincipal(scope, new UserId("teamed"), [new TeamId("tier1")]);
        var held = await planner.HeldAnywhereAsync(teamed, scope, [AccessPermissions.ConversationReply, AccessPermissions.ConversationRead, AccessPermissions.AccessManage]);
        Assert.Contains(AccessPermissions.ConversationReply, held, "A team-scoped Agent grant provides conversation:reply somewhere.");
        Assert.DoesNotContain(AccessPermissions.ConversationRead, held, "A workspace deny of a role containing the permission removes it everywhere.");
        Assert.DoesNotContain(AccessPermissions.AccessManage, held);

        var stranger = new UserPrincipal(scope, new UserId("stranger"));
        Assert.IsEmpty(await planner.HeldAnywhereAsync(stranger, scope, [AccessPermissions.ConversationRead]));
    }
}
