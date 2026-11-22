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

        Assert.IsInstanceOfType<ApiClientPrincipal>(principal!.Value.Value);
        Assert.AreSame(requestUser, http.User, "The shared HttpContext.User is never replaced.");
        Assert.IsTrue(seenUsers.All(user => ReferenceEquals(user, caller)), "Resolvers see exactly the MCP caller's user.");
        Assert.IsNull(await resolver.ResolveAsync(new TicketFlowMcpCaller(null, new ServiceCollection().BuildServiceProvider()), scope.WorkspaceId),
            "Without an authenticated caller nothing is resolved, even if the request carried a user.");
    }

    private sealed class FixedPartition(AccessPartition partition, List<ClaimsPrincipal> seen) : ITicketFlowPartitionResolver
    {
        public ValueTask<AccessPartition?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
        {
            seen.Add(httpContext.User);
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
