using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Identity;
using TicketFlow.Integrations.IdentityFlow.SignalR;
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
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.User, Subject, Subject), IdentityFlowClaimsMapper.Map(session, options));
        var bearer = Authenticated(("sub", Subject.ToUpperInvariant()), ("identityflow:client_id", "console"), ("scope", "openid ticketflow"));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.User, Subject, Subject), IdentityFlowClaimsMapper.Map(bearer, options), "GUID subjects are normalized.");
    }

    [TestMethod]
    public void BearerTokensCarryingASessionIdStillNeedTheRequiredScope()
    {
        var options = new IdentityFlowPrincipalOptions();
        options.RequiredScopes.Add("ticketflow");
        // OpenIddict access tokens issued from a browser session carry identityflow:session_id as well as the client id.
        var bearer = Authenticated(("sub", Subject), ("identityflow:session_id", "s1"), ("identityflow:client_id", "spa"), ("scope", "profile"));
        Assert.IsNull(IdentityFlowClaimsMapper.Map(bearer, options), "Session-derived bearer tokens are not browser sessions.");
        var pat = Authenticated((ClaimTypes.NameIdentifier, Subject), ("identityflow:session_id", "s1"), ("identityflow_auth_method", "PersonalAccessToken"));
        Assert.IsNull(IdentityFlowClaimsMapper.Map(pat, options));
        var cookie = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject), new Claim("client_id", "x")], "IdentityFlow.Cookie"));
        Assert.IsNotNull(IdentityFlowClaimsMapper.Map(cookie, options), "The cookie authentication type is a browser session.");
        Assert.IsNotNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("identityflow:session_id", "s1")), options));
    }

    [TestMethod]
    public void ApiKeysPersonalAccessTokensAndClientTokensFollowTheirConventions()
    {
        var options = new IdentityFlowPrincipalOptions { UserIdPrefix = "idf-user:" };
        var compact = Guid.Parse(Subject).ToString("N");
        var apiKey = Authenticated((ClaimTypes.NameIdentifier, compact), ("identityflow_auth_method", "ApiKey"), ("identityflow_method_id", "key-1"), ("scope", "ticketflow"));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.ApiClient, "idf:" + Subject, Subject), IdentityFlowClaimsMapper.Map(apiKey, options),
            "API clients are prefixed by default.");
        options.ApiKeyClients = IdentityFlowApiKeyClientSource.Method;
        Assert.AreEqual("idf:key-1", IdentityFlowClaimsMapper.Map(apiKey, options)?.Id, "Each key can be its own API client.");
        options.ApiKeys = IdentityFlowApiKeyMapping.User;
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.User, "idf-user:" + Subject, Subject), IdentityFlowClaimsMapper.Map(apiKey, options));
        var pat = Authenticated((ClaimTypes.NameIdentifier, compact), ("identityflow_auth_method", "PersonalAccessToken"));
        Assert.AreEqual(IdentityFlowPrincipalKind.User, IdentityFlowClaimsMapper.Map(pat, options)?.Kind);
        var client = Authenticated(("identityflow:client_id", "billing-sync"));
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.ApiClient, "idf:billing-sync", "billing-sync"), IdentityFlowClaimsMapper.Map(client, options));
    }

    [TestMethod]
    public void MappedCallersNeverReceiveReservedWorkerIdentities()
    {
        var options = new IdentityFlowPrincipalOptions { ApiClientIdPrefix = string.Empty };
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("identityflow:client_id", "sla-worker")), options), "Built-in worker ids are reserved.");
        options.ReservedApiClientIds.Add("custom-engine");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("identityflow:client_id", "custom-engine")), options));
        Assert.IsNotNull(IdentityFlowClaimsMapper.Map(Authenticated(("identityflow:client_id", "integration")), options));
    }

    [TestMethod]
    public void UnauthenticatedAmbiguousAndOutOfScopeUsersMapToNothing()
    {
        var options = new IdentityFlowPrincipalOptions();
        Assert.IsNull(IdentityFlowClaimsMapper.Map(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)])), options), "Unauthenticated identities are ignored.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(null, options));
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("email", "someone@example.com")), options));
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("sub", Guid.NewGuid().ToString())), options), "Conflicting subjects are ambiguous.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("sub", Guid.NewGuid().ToString()), (ClaimTypes.NameIdentifier, Subject)), options), "Ambiguous sub must not fall back to NameIdentifier.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), (ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())), options), "Distinct sub and NameIdentifier must be rejected.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("identityflow:client_id", "client-a"), ("client_id", "client-b")), options), "Distinct identityflow:client_id and client_id must be rejected.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("identityflow_auth_method", "ApiKey"), ("identityflow_auth_method", "PersonalAccessToken")), options), "Ambiguous auth method must be rejected.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("identityflow:session_id", "s1"), ("identityflow:session_id", "s2")), options), "Ambiguous session id must be rejected.");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("identityflow_auth_method", "ApiKey")), options));
        options.RequiredScopes.Add("ticketflow");
        Assert.IsNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("scope", "openid profile")), options));
        Assert.IsNotNull(IdentityFlowClaimsMapper.Map(Authenticated(("sub", Subject), ("scope", "openid ticketflow")), options));
    }

    [TestMethod]
    public async Task ResolverBuildsUsersWithTeamSnapshotsAndApiClients()
    {
        var scope = TestScopes.Create();
        var resolver = new IdentityFlowPrincipalResolver(new FakeTeams(), Options.Create(new IdentityFlowPrincipalOptions()));
        var http = new DefaultHttpContext { User = Authenticated(("sub", Subject)) };
        var resolved = Assert.IsInstanceOfType<UserPrincipal>(await resolver.ResolveAsync(http, scope));
        Assert.AreEqual(Subject, resolved.UserId.Value);
        Assert.IsTrue(resolved.Teams.Contains(new TeamId("tier1")));
        http.User = Authenticated((ClaimTypes.NameIdentifier, Subject), ("identityflow_auth_method", "ApiKey"));
        Assert.IsInstanceOfType<ApiClientPrincipal>(await resolver.ResolveAsync(http, scope));
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.IsNull(await resolver.ResolveAsync(http, scope));
    }

    [TestMethod]
    public async Task StaffIdentityLinksWinAndUnmappedUsersFollowTheOption()
    {
        var scope = TestScopes.Create();
        var staff = new FakeStaff();
        var options = new IdentityFlowPrincipalOptions();
        var resolver = new IdentityFlowPrincipalResolver(new FakeTeams(), Options.Create(options), staff);
        var http = new DefaultHttpContext { User = Authenticated(("sub", Subject)) };
        var linked = Assert.IsInstanceOfType<UserPrincipal>(await resolver.ResolveAsync(http, scope));
        Assert.AreEqual("agent-ada", linked.UserId.Value);
        Assert.AreEqual(new StaffIdentityRef("identityflow", Subject), staff.Last);

        http.User = Authenticated(("sub", "unlinked"));
        Assert.AreEqual("unlinked", Assert.IsInstanceOfType<UserPrincipal>(await resolver.ResolveAsync(http, scope)).UserId.Value);
        options.UnmappedStaff = IdentityFlowUnmappedStaff.Refuse;
        Assert.IsNull(await resolver.ResolveAsync(http, scope), "Unlinked identities can be refused.");
    }

    [TestMethod]
    public async Task RealtimeConnectionsTakeTheWorkspaceFromTheQueryString()
    {
        var scope = TestScopes.Create();
        IRealtimePrincipalResolver resolver = new IdentityFlowRealtimePrincipalResolver(new IdentityFlowPrincipalResolver(new FakeTeams(), Options.Create(new IdentityFlowPrincipalOptions())),
            new FixedPartition(AccessPartition.From(scope)), Options.Create(new IdentityFlowPrincipalOptions()));
        var user = Authenticated(("sub", Subject));
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?workspace=workspace");
        Assert.AreEqual(scope, (await resolver.ResolveAsync(user, http))?.Scope);
        http.Request.QueryString = QueryString.Empty;
        Assert.IsNull(await resolver.ResolveAsync(user, http), "Connections must name a workspace.");
        Assert.IsNull(await resolver.ResolveAsync(user, null));
    }

    [TestMethod]
    public void RegistrationReplacesOnlyTheOptionFallbacks()
    {
        var services = new ServiceCollection();
        services.AddTicketFlow().AddTicketFlowEndpoints().AddTicketFlowSignalR().AddIdentityFlowRealtimePrincipals();
        services.AddScoped<ITeamMembershipResolver, FakeTeams>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsInstanceOfType<IdentityFlowPrincipalResolver>(scope.ServiceProvider.GetRequiredService<ITicketFlowPrincipalResolver>());
        Assert.IsInstanceOfType<IdentityFlowRealtimePrincipalResolver>(scope.ServiceProvider.GetRequiredService<IRealtimePrincipalResolver>());

        var custom = new ServiceCollection();
        custom.AddScoped<ITicketFlowPrincipalResolver, CustomResolver>();
        custom.AddTicketFlow().AddTicketFlowEndpoints().AddIdentityFlowPrincipals();
        Assert.AreEqual(typeof(CustomResolver), custom.Single(descriptor => descriptor.ServiceType == typeof(ITicketFlowPrincipalResolver)).ImplementationType, "Application resolvers win.");
    }

    [TestMethod]
    public async Task TicketFlowServiceAccountApiKeysMapUnprefixedAndVerifyScopeClaims()
    {
        var scope = TestScopes.Create();
        var options = new IdentityFlowPrincipalOptions();
        options.RequiredScopes.Add("ticketflow"); // TicketFlow API keys do not carry OAuth scopes

        var validApiKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "sa-reporting-export"),
            new Claim("api_key_id", "key-123"),
            new Claim("amr", "api_key"),
            new Claim("tenant_id", scope.TenantId),
            new Claim("project_id", scope.ProjectId),
            new Claim("environment_id", scope.EnvironmentId),
            new Claim("app_id", scope.AppId),
            new Claim("workspace_id", scope.WorkspaceId.Value),
        ], authenticationType: "ApiKey"));

        var mapped = IdentityFlowClaimsMapper.Map(validApiKey, options);
        Assert.AreEqual(new IdentityFlowIdentity(IdentityFlowPrincipalKind.ApiClient, "sa-reporting-export", "sa-reporting-export"), mapped);

        var resolver = new IdentityFlowPrincipalResolver(new FakeTeams(), Options.Create(options));
        var resolved = Assert.IsInstanceOfType<ApiClientPrincipal>(await resolver.ResolveAsync(validApiKey, scope));
        Assert.AreEqual(new ApiClientId("sa-reporting-export"), resolved.ClientId);

        // Wrong workspace claim fails closed
        var wrongWorkspace = TestScopes.Create(workspaceId: "other-ws");
        Assert.IsNull(await resolver.ResolveAsync(validApiKey, wrongWorkspace));

        // Reserved worker ID is still rejected even on ApiKey scheme
        var reservedKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "sla-worker"),
            new Claim("api_key_id", "key-456"),
        ], authenticationType: "ApiKey"));
        Assert.IsNull(IdentityFlowClaimsMapper.Map(reservedKey, options));
    }

    [TestMethod]
    public async Task ResolverPropagatesExternalRolesAndIdentityProviderForUsersClientsAndRealtime()
    {
        var scope = TestScopes.Create();
        var staff = new FakeStaff();
        var resolver = new IdentityFlowPrincipalResolver(
            new FakeTeams(),
            Options.Create(new IdentityFlowPrincipalOptions()),
            staff);

        var userClaims = Authenticated(
            ("sub", Subject),
            ("role", "idf:support-agents, idf:billing"),
            ("idp", "identityflow"));
        var resolvedUser = Assert.IsInstanceOfType<UserPrincipal>(await resolver.ResolveAsync(userClaims, scope));
        Assert.AreEqual(new UserId("agent-ada"), resolvedUser.UserId);
        Assert.IsTrue(resolvedUser.Teams.Contains(new TeamId("tier1")));
        Assert.IsTrue(resolvedUser.ExternalRoles.Contains("idf:support-agents"));
        Assert.IsTrue(resolvedUser.ExternalRoles.Contains("idf:billing"));
        Assert.AreEqual("identityflow", resolvedUser.IdentityProvider);

        var clientClaims = Authenticated(
            ("identityflow:client_id", "sync-bot"),
            ("roles", "idf:automation"),
            ("identityflow:provider", "okta"));
        var resolvedClient = Assert.IsInstanceOfType<ApiClientPrincipal>(await resolver.ResolveAsync(clientClaims, scope));
        Assert.AreEqual(new ApiClientId("idf:sync-bot"), resolvedClient.ClientId);
        Assert.IsTrue(resolvedClient.ExternalRoles.Contains("idf:automation"));
        Assert.AreEqual("okta", resolvedClient.IdentityProvider);

        // SignalR realtime resolver inherits ExternalRoles and IdentityProvider via IdentityFlowPrincipalResolver
        IRealtimePrincipalResolver realtime = new IdentityFlowRealtimePrincipalResolver(
            resolver,
            new FixedPartition(AccessPartition.From(scope)),
            Options.Create(new IdentityFlowPrincipalOptions()));
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?workspace=workspace");
        var realtimePrincipal = Assert.IsInstanceOfType<UserPrincipal>(await realtime.ResolveAsync(userClaims, http));
        Assert.IsTrue(realtimePrincipal.ExternalRoles.Contains("idf:support-agents"));
        Assert.AreEqual("identityflow", realtimePrincipal.IdentityProvider);
    }

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), "IdentityFlow"));

    private sealed class FakeTeams : ITeamMembershipResolver
    {
        public Task<IReadOnlyList<TeamId>> GetTeamsAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TeamId>>([new TeamId("tier1")]);
    }

    private sealed class FakeStaff : IStaffIdentityResolver
    {
        public StaffIdentityRef? Last { get; private set; }

        public Task<UserId?> ResolveUserAsync(TicketScope scope, StaffIdentityRef identity, CancellationToken cancellationToken = default)
        {
            Last = identity;
            return Task.FromResult<UserId?>(identity.Subject == Subject ? new UserId("agent-ada") : null);
        }
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
