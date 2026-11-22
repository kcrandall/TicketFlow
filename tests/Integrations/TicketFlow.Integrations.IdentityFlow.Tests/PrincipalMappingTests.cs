using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Realtime.SignalR;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.IdentityFlow.Tests;

[TestClass]
public sealed class PrincipalMappingTests
{
    private const string Subject = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

    [TestMethod]
    public void BrowserSessionsAndBearerTokensMapToUsers()
    {
        var options = new IdentityFlowPrincipalOptions();
        var session = Authenticated(("sub", Subject), (ClaimTypes.NameIdentifier, Subject), ("identityflow:session_id", Guid.NewGuid().ToString()));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.User, Subject), IdentityFlowClaimsMapper.Map(session, options));
        var bearer = Authenticated(("sub", Subject.ToUpperInvariant()), ("identityflow:client_id", "console"), ("scope", "openid ticketflow"));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.User, Subject), IdentityFlowClaimsMapper.Map(bearer, options), "GUID subjects are normalized.");
    }

    [TestMethod]
    public void ApiKeysPersonalAccessTokensAndClientTokensFollowTheirConventions()
    {
        var options = new IdentityFlowPrincipalOptions { ApiClientIdPrefix = "key:", UserIdPrefix = "idf:" };
        var compact = Guid.Parse(Subject).ToString("N");
        var apiKey = Authenticated((ClaimTypes.NameIdentifier, compact), ("identityflow_auth_method", "ApiKey"), ("scope", "ticketflow"));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.ApiClient, "key:" + Subject), IdentityFlowClaimsMapper.Map(apiKey, options));
        options.ApiKeys = IdentityFlowApiKeyMapping.User;
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.User, "idf:" + Subject), IdentityFlowClaimsMapper.Map(apiKey, options));
        var pat = Authenticated((ClaimTypes.NameIdentifier, compact), ("identityflow_auth_method", "PersonalAccessToken"));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.User, "idf:" + Subject), IdentityFlowClaimsMapper.Map(pat, options));
        var client = Authenticated(("identityflow:client_id", "billing-sync"));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.ApiClient, "key:billing-sync"), IdentityFlowClaimsMapper.Map(client, options));
    }

    [TestMethod]
    public void UnauthenticatedAmbiguousAndOutOfScopeUsersMapToNothing()
    {
        var options = new IdentityFlowPrincipalOptions();
        Assert.IsNull(IdentityFlowClaimsMapper.Map(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)])), options), "Unauthenticated identities are ignored.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(null, options));
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("email", "someone@example.com")), options));
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("sub", Guid.NewGuid().ToString())), options), "Conflicting subjects are ambiguous.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("identityflow_auth_method", "ApiKey")), options));

        options.RequiredScopes.Add("ticketflow");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("scope", "openid profile")), options));
        Assert.IsNotNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("scope", "openid ticketflow")), options));
        Assert.IsNotNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("identityflow:session_id", "s")), options), "Browser sessions carry no scopes.");
    }

    [TestMethod]
    public async Task ResolverBuildsUsersWithTeamSnapshotsAndApiClients()
    {
        var scope = TestScopes.Create();
        var resolver = new IdentityFlowPrincipalResolver(new FakeTeams(), new FixedPartition(AccessPartition.From(scope)), Options.Create(new IdentityFlowPrincipalOptions()));
        var http = new DefaultHttpContext { User = Authenticated(("sub", Subject)) };
        var user = await resolver.ResolveAsync(http, scope);
        Assert.IsInstanceOfType<UserPrincipal>(user?.Value);
        var resolved = (UserPrincipal)user!.Value.Value!;
        Assert.AreEqual(Subject, resolved.UserId.Value);
        Assert.IsTrue(resolved.Teams.Contains(new TeamId("tier1")));

        http.User = Authenticated((ClaimTypes.NameIdentifier, Subject), ("identityflow_auth_method", "ApiKey"));
        Assert.IsInstanceOfType<ApiClientPrincipal>((await resolver.ResolveAsync(http, scope))?.Value);
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.IsNull(await resolver.ResolveAsync(http, scope));
    }

    [TestMethod]
    public async Task RealtimeConnectionsTakeTheWorkspaceFromTheQueryString()
    {
        var scope = TestScopes.Create();
        IRealtimePrincipalResolver resolver = new IdentityFlowPrincipalResolver(new FakeTeams(), new FixedPartition(AccessPartition.From(scope)), Options.Create(new IdentityFlowPrincipalOptions()));
        var user = Authenticated(("sub", Subject));
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?workspace=workspace");
        var principal = await resolver.ResolveAsync(user, http);
        Assert.AreEqual(scope, principal?.Scope);
        http.Request.QueryString = QueryString.Empty;
        Assert.IsNull(await resolver.ResolveAsync(user, http), "Connections must name a workspace.");
        Assert.IsNull(await resolver.ResolveAsync(user, null));
    }

    [TestMethod]
    public void RegistrationReplacesOnlyTheOptionFallbacks()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow().AddTicketFlowEndpoints().AddTicketFlowSignalR().AddIdentityFlowPrincipals();
        services.AddScoped<ITeamMembershipResolver, FakeTeams>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsInstanceOfType<IdentityFlowPrincipalResolver>(scope.ServiceProvider.GetRequiredService<ITicketFlowPrincipalResolver>());
        Assert.IsInstanceOfType<IdentityFlowPrincipalResolver>(scope.ServiceProvider.GetRequiredService<IRealtimePrincipalResolver>());

        var custom = new ServiceCollection();
        custom.AddScoped<ITicketFlowPrincipalResolver, CustomResolver>();
        custom.AddTicketFlow().AddTicketFlowEndpoints().AddIdentityFlowPrincipals();
        Assert.AreEqual(typeof(CustomResolver), custom.Single(descriptor => descriptor.ServiceType == typeof(ITicketFlowPrincipalResolver)).ImplementationType, "Application resolvers win.");
    }

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), "IdentityFlow"));

    private sealed class FakeTeams : ITeamMembershipResolver
    {
        public Task<IReadOnlyList<TeamId>> GetTeamsAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TeamId>>([new TeamId("tier1")]);
    }

    private sealed class FixedPartition(AccessPartition partition) : ITicketFlowPartitionResolver
    {
        public ValueTask<AccessPartition?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken = default) => ValueTask.FromResult<AccessPartition?>(partition);
    }

    private sealed class CustomResolver : ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default) => ValueTask.FromResult<PrincipalRef?>(null);
    }
}
