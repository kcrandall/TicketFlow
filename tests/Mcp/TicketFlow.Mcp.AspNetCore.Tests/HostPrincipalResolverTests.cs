using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Testing;

namespace TicketFlow.Mcp.AspNetCore.Tests;

[TestClass]
public sealed class HostPrincipalResolverTests
{
    [TestMethod]
    public async Task TheMcpCallersUserIsUsedWithoutMutatingTheSharedRequest()
    {
        var scope = TestScopes.Create();
        var requestUser = new ClaimsPrincipal(new ClaimsIdentity());
        var http = new DefaultHttpContext { User = requestUser };
        http.Request.Host = new HostString("support.example.test");
        var accessor = new HttpContextAccessor { HttpContext = http };
        var seenUsers = new List<ClaimsPrincipal>();
        var resolver = new HostMcpPrincipalResolver(accessor, new FixedPartition(AccessPartition.From(scope), seenUsers), new ClientPrincipals(seenUsers));
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client", "assistant")], "test"));

        var principal = await resolver.ResolveAsync(new TicketFlowMcpCaller(caller, new ServiceCollection().BuildServiceProvider()), scope.WorkspaceId);

        Assert.IsInstanceOfType<ApiClientPrincipal>(principal);
        Assert.AreSame(requestUser, http.User, "The shared HttpContext.User is never replaced.");
        Assert.IsTrue(seenUsers.All(user => ReferenceEquals(user, caller)), "Resolvers see exactly the MCP caller's user.");
        Assert.IsNull(await resolver.ResolveAsync(new TicketFlowMcpCaller(null, new ServiceCollection().BuildServiceProvider()), scope.WorkspaceId),
            "Without an authenticated caller nothing is resolved, even if the request carried a user.");
    }

    [TestMethod]
    public async Task PreservesExternalRolesIdentityProviderAndEnforcesScopePinning()
    {
        var scope = TestScopes.Create();
        var otherWorkspace = new WorkspaceId("other-ws");
        var partition = AccessPartition.From(scope);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var claimsResolver = new ClaimsPrincipalResolver();
        var resolver = new HostMcpPrincipalResolver(accessor, new FixedPartition(partition, []), principals: null, accessResolver: claimsResolver);

        // External identity with roles and provider
        var externalUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "alice"),
            new Claim("role", "support-agents"),
            new Claim("role", "triage"),
            new Claim("idp", "okta"),
            new Claim("tenant_id", scope.TenantId),
            new Claim("project_id", scope.ProjectId),
            new Claim("environment_id", scope.EnvironmentId),
            new Claim("app_id", scope.AppId),
        ], "ExternalIdentity"));

        var resolvedUser = await resolver.ResolveAsync(new TicketFlowMcpCaller(externalUser, new ServiceCollection().BuildServiceProvider()), scope.WorkspaceId);
        var userPrincipal = Assert.IsInstanceOfType<UserPrincipal>(resolvedUser);
        Assert.AreEqual("alice", userPrincipal.UserId.Value);
        Assert.AreEqual("okta", userPrincipal.IdentityProvider);
        CollectionAssert.AreEquivalent(new[] { "support-agents", "triage" }, userPrincipal.ExternalRoles.ToList());

        // Workspace-pinned service account API key: allowed in pinned workspace, rejected in another workspace
        var workspacePinnedKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "sa-ws-only"),
            new Claim("amr", "api_key"),
            new Claim("api_key_id", "key-1"),
            new Claim("tenant_id", scope.TenantId),
            new Claim("project_id", scope.ProjectId),
            new Claim("environment_id", scope.EnvironmentId),
            new Claim("app_id", scope.AppId),
            new Claim("workspace_id", scope.WorkspaceId.Value),
        ], "ApiKey"));

        var pinnedOk = await resolver.ResolveAsync(new TicketFlowMcpCaller(workspacePinnedKey, new ServiceCollection().BuildServiceProvider()), scope.WorkspaceId);
        Assert.IsInstanceOfType<ApiClientPrincipal>(pinnedOk);
        var pinnedRejected = await resolver.ResolveAsync(new TicketFlowMcpCaller(workspacePinnedKey, new ServiceCollection().BuildServiceProvider()), otherWorkspace);
        Assert.IsNull(pinnedRejected, "Workspace-pinned API key must be rejected for another workspace.");

        // Tenant-scoped service account API key (no workspace_id claim): allowed across workspaces in the same partition
        var tenantScopedKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "sa-tenant"),
            new Claim("amr", "api_key"),
            new Claim("api_key_id", "key-2"),
            new Claim("tenant_id", scope.TenantId),
            new Claim("project_id", scope.ProjectId),
            new Claim("environment_id", scope.EnvironmentId),
            new Claim("app_id", scope.AppId),
        ], "ApiKey"));

        var tenantInFirst = await resolver.ResolveAsync(new TicketFlowMcpCaller(tenantScopedKey, new ServiceCollection().BuildServiceProvider()), scope.WorkspaceId);
        var tenantInSecond = await resolver.ResolveAsync(new TicketFlowMcpCaller(tenantScopedKey, new ServiceCollection().BuildServiceProvider()), otherWorkspace);
        Assert.AreEqual(scope.WorkspaceId, Assert.IsInstanceOfType<ApiClientPrincipal>(tenantInFirst).Scope.WorkspaceId);
        Assert.AreEqual(otherWorkspace, Assert.IsInstanceOfType<ApiClientPrincipal>(tenantInSecond).Scope.WorkspaceId);
    }

    [TestMethod]
    public async Task RejectsDisabledAndOutOfScopeServiceAccountsAcrossBothResolutionPaths()
    {
        var scope = TestScopes.Create();
        var otherWorkspace = new WorkspaceId("other-ws");
        var partition = AccessPartition.From(scope);
        var saStore = new InMemoryServiceAccountStore(TestClock.Create());

        var activeId = new ApiClientId("mcp-sa-active");
        var disabledId = new ApiClientId("mcp-sa-disabled");
        var scopedId = new ApiClientId("mcp-sa-scoped");
        await saStore.CreateAsync(new ServiceAccount(activeId, partition, new WorkspaceGrantScope(scope.WorkspaceId), "Active"), "seed");
        await saStore.CreateAsync(new ServiceAccount(disabledId, partition, new WorkspaceGrantScope(scope.WorkspaceId), "Disabled"), "seed");
        await saStore.SetEnabledAsync(partition, disabledId, isEnabled: false, actorPrincipalId: "seed", expectedVersion: 1);
        await saStore.CreateAsync(new ServiceAccount(scopedId, partition, new WorkspaceGrantScope(otherWorkspace), "Other WS"), "seed");

        var services = new ServiceCollection();
        services.AddSingleton<IServiceAccountStore>(saStore);
        await using var provider = services.BuildServiceProvider();

        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var hostResolver = new HostMcpPrincipalResolver(accessor, new FixedPartition(partition, []), new ClientPrincipals([]), serviceAccounts: saStore);
        var claimsResolver = new HostMcpPrincipalResolver(accessor, new FixedPartition(partition, []), principals: null, accessResolver: new ClaimsPrincipalResolver(), serviceAccounts: saStore);

        static ClaimsPrincipal ClientUser(string id) => new(new ClaimsIdentity(
            [new Claim("client", id), new Claim("client_id", id), new Claim("principal_type", "service_account")], "test"));

        Assert.IsInstanceOfType<ApiClientPrincipal>(await hostResolver.ResolveAsync(new TicketFlowMcpCaller(ClientUser("mcp-sa-active"), provider), scope.WorkspaceId));
        Assert.IsNull(await hostResolver.ResolveAsync(new TicketFlowMcpCaller(ClientUser("mcp-sa-disabled"), provider), scope.WorkspaceId));
        Assert.IsNull(await hostResolver.ResolveAsync(new TicketFlowMcpCaller(ClientUser("mcp-sa-scoped"), provider), scope.WorkspaceId));

        Assert.IsInstanceOfType<ApiClientPrincipal>(await claimsResolver.ResolveAsync(new TicketFlowMcpCaller(ClientUser("mcp-sa-active"), provider), scope.WorkspaceId));
        Assert.IsNull(await claimsResolver.ResolveAsync(new TicketFlowMcpCaller(ClientUser("mcp-sa-disabled"), provider), scope.WorkspaceId));
        Assert.IsNull(await claimsResolver.ResolveAsync(new TicketFlowMcpCaller(ClientUser("mcp-sa-scoped"), provider), scope.WorkspaceId));
    }

    private sealed class FixedPartition(AccessPartition partition, List<ClaimsPrincipal> seen) : ITicketFlowPartitionResolver
    {
        public ValueTask<AccessPartition?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
        {
            seen.Add(httpContext.User);
            if (httpContext.Request.Host.HasValue)
                Assert.AreEqual("support.example.test", httpContext.Request.Host.Host, "Partition resolution still sees the request line.");
            return ValueTask.FromResult<AccessPartition?>(partition);
        }
    }

    private sealed class ClientPrincipals(List<ClaimsPrincipal> seen) : ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default)
        {
            seen.Add(httpContext.User);
            return ValueTask.FromResult(httpContext.User.FindFirstValue("client") is { } client ? (PrincipalRef?)new ApiClientPrincipal(scope, new ApiClientId(client)) : null);
        }
    }
}
