using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Core;

namespace TicketFlow.Access.AspNetCore.Tests;

[TestClass]
public sealed class ExternalIdentityAndHybridAuthTests
{
    private static readonly TicketScope DefaultScope = new("tenant-a", "proj-a", "prod", "support", new WorkspaceId("ws-main"));

    [TestMethod]
    public async Task ExternalIdentityFailsClosedWhenNoTrustGateIsConfigured()
    {
        await using var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowExternalIdentityAuthentication(_ => { });
        });

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Add("X-Auth-Subject", "alice");

        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task ExternalIdentityEnforcesSharedSecretTrustGateAndRejectsAmbiguousHeaders()
    {
        const string secret = "super-secret-proxy-token-123";
        await using var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowExternalIdentityAuthentication(options =>
            {
                options.UseIdentityFlowDefaults(sharedSecret: secret);
            });
        });

        using var client = app.GetTestClient();

        // Missing secret -> 401
        using (var missing = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            missing.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(missing);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Wrong secret -> 401
        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            wrong.Headers.Add("X-IdentityFlow-Proxy-Secret", "wrong-secret");
            wrong.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(wrong);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Duplicate conflicting secret headers -> 401
        using (var dupSecret = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            dupSecret.Headers.Add("X-IdentityFlow-Proxy-Secret", secret);
            dupSecret.Headers.Add("X-IdentityFlow-Proxy-Secret", "other-secret");
            dupSecret.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(dupSecret);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Duplicate conflicting subject headers -> 401
        using (var dupSubject = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            dupSubject.Headers.Add("X-IdentityFlow-Proxy-Secret", secret);
            dupSubject.Headers.Add("X-Auth-Subject", "alice");
            dupSubject.Headers.Add("X-Auth-Subject", "bob");
            using var resp = await client.SendAsync(dupSubject);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Valid secret + subject -> 200
        using (var valid = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            valid.Headers.Add("X-IdentityFlow-Proxy-Secret", secret);
            valid.Headers.Add("X-Auth-Subject", "alice");
            valid.Headers.Add("X-Auth-Name", "Alice Example");
            valid.Headers.Add("X-Auth-Email", "alice@example.com");
            using var resp = await client.SendAsync(valid);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("user:alice", payload.PrincipalId);
            Assert.AreEqual("identityflow", payload.Idp);
        }
    }

    [TestMethod]
    public async Task ExternalIdentityEnforcesTrustedProxyIpAndCidrIncludingIpv4MappedIpv6()
    {
        await using var app = await CreateTestAppAsync(
            services =>
            {
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseIdentityFlowDefaults();
                    options.TrustedProxies.Add(IPAddress.Parse("192.0.2.10"));
                    options.TrustedProxyNetworks.Add(new IPNetwork(IPAddress.Parse("10.20.0.0"), 16));
                });
            },
            remoteIpHeader: "X-Test-Remote-Ip");

        using var client = app.GetTestClient();

        using (var trustedDirect = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            trustedDirect.Headers.Add("X-Test-Remote-Ip", "192.0.2.10");
            trustedDirect.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(trustedDirect);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }

        using (var mappedCidr = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            mappedCidr.Headers.Add("X-Test-Remote-Ip", "::ffff:10.20.30.40");
            mappedCidr.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(mappedCidr);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }

        using (var mappedExact = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            mappedExact.Headers.Add("X-Test-Remote-Ip", "::ffff:192.0.2.10");
            mappedExact.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(mappedExact);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }

        using (var untrusted = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            untrusted.Headers.Add("X-Test-Remote-Ip", "203.0.113.99");
            untrusted.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(untrusted);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    [TestMethod]
    public async Task ExternalIdentityEnforcesCustomUpstreamValidator()
    {
        await using var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowExternalIdentityAuthentication(options =>
            {
                options.ValidateUpstreamRequest = ctx =>
                    ValueTask.FromResult(ctx.Request.Headers["X-Custom-Attestation"] == "ok");
            });
        });

        using var client = app.GetTestClient();

        using (var rejected = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            rejected.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(rejected);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        using (var accepted = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            accepted.Headers.Add("X-Custom-Attestation", "ok");
            accepted.Headers.Add("X-Auth-Subject", "alice");
            using var resp = await client.SendAsync(accepted);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [TestMethod]
    public async Task IdentityFlowPresetSupportsLocalOnlyFederatedRolesServiceAccountsAndScopeHeaders()
    {
        const string secret = "idflow-secret";

        // Part A: AcceptRoleHeaders = false (local roles only)
        await using (var localOnlyApp = await CreateTestAppAsync(
            services =>
            {
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseIdentityFlowDefaults(sharedSecret: secret, acceptFederatedRoles: false);
                });
            },
            configureAccess: options =>
            {
                options.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
                options.RoleFederation.StaticMappings["idflow-agent"] = BuiltInRoles.Agent.Id;
            }))
        {
            var grantStore = localOnlyApp.Services.GetRequiredService<IAccessGrantStore>();
            var tenantBPartition = new AccessPartition("tenant-b", "proj-b", "staging", "mobile");
            await grantStore.CreateAsync(new AccessGrant(
                new GrantId("local-viewer"),
                tenantBPartition,
                new UserId("local-user"),
                BuiltInRoles.Viewer.Id,
                TenantGrantScope.Instance));

            using var client = localOnlyApp.GetTestClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
            req.Headers.Add("X-IdentityFlow-Proxy-Secret", secret);
            req.Headers.Add("X-Auth-Subject", "local-user");
            req.Headers.Add("X-Auth-Roles", "idflow-agent");
            req.Headers.Add("X-Auth-Tenant", "tenant-b");
            req.Headers.Add("X-Auth-Project", "proj-b");
            req.Headers.Add("X-Auth-Environment", "staging");
            req.Headers.Add("X-Auth-App", "mobile");
            req.Headers.Add("X-Auth-Workspace", "ws-eu");

            using var resp = await client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("user:local-user", payload.PrincipalId);
            Assert.AreEqual("tenant-b/proj-b/staging/mobile/ws-eu", payload.Scope);
            Assert.HasCount(0, payload.ExternalRoles);
            Assert.IsTrue(payload.CanReadConversation);
            Assert.IsFalse(payload.CanReplyConversation); // X-Auth-Roles ignored
        }

        // Part B: AcceptRoleHeaders = true (federated user + terminated service account)
        await using (var federatedApp = await CreateTestAppAsync(
            services =>
            {
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseIdentityFlowDefaults(sharedSecret: secret, acceptFederatedRoles: true);
                });
            },
            configureAccess: options =>
            {
                options.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
                options.RoleFederation.StaticMappings["idflow-agent"] = BuiltInRoles.Agent.Id;
            }))
        {
            using var client = federatedApp.GetTestClient();

            using (var userReq = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                userReq.Headers.Add("X-IdentityFlow-Proxy-Secret", secret);
                userReq.Headers.Add("X-Auth-Subject", "fed-user");
                userReq.Headers.Add("X-Auth-Roles", "idflow-agent, extra-role");

                using var resp = await client.SendAsync(userReq);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
                var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
                Assert.IsNotNull(payload);
                Assert.AreEqual("user:fed-user", payload.PrincipalId);
                Assert.HasCount(2, payload.ExternalRoles);
                Assert.IsTrue(payload.CanReadConversation);
                Assert.IsTrue(payload.CanReplyConversation);
            }

            using (var svcReq = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                svcReq.Headers.Add("X-IdentityFlow-Proxy-Secret", secret);
                svcReq.Headers.Add("X-Auth-Client-Id", "idflow-bot");
                svcReq.Headers.Add("X-Auth-Principal-Type", "service_account");
                svcReq.Headers.Add("X-Auth-Roles", "idflow-agent");

                using var resp = await client.SendAsync(svcReq);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
                var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
                Assert.IsNotNull(payload);
                Assert.AreEqual("client:idflow-bot", payload.PrincipalId);
                Assert.IsTrue(payload.CanReadConversation);
                Assert.IsTrue(payload.CanReplyConversation);
            }
        }
    }

    [TestMethod]
    public async Task OAuth2ProxyPresetMapsForwardedHeadersToClaimsAndTeams()
    {
        await using var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowExternalIdentityAuthentication(options =>
            {
                options.UseOAuth2ProxyDefaults(sharedSecret: "oauth2-proxy-secret");
            });
        });

        using var client = app.GetTestClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        req.Headers.Add("X-OAuth2-Proxy-Secret", "oauth2-proxy-secret");
        req.Headers.Add("X-Forwarded-User", "oauth-user-7");
        req.Headers.Add("X-Forwarded-Email", "dev@example.com");
        req.Headers.Add("X-Forwarded-Preferred-Username", "devuser");
        req.Headers.Add("X-Forwarded-Groups", "platform, support");

        using var resp = await client.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
        Assert.IsNotNull(payload);
        Assert.AreEqual("user:oauth-user-7", payload.PrincipalId);
        Assert.AreEqual("oauth2-proxy", payload.Idp);
        Assert.HasCount(2, payload.Teams);
        Assert.IsTrue(payload.Teams.Contains("platform"));
        Assert.IsTrue(payload.Teams.Contains("support"));
    }

    [TestMethod]
    public async Task HybridAuthenticationSupportsExternalIdentityAndTicketFlowApiKeysOnSameHost()
    {
        const string proxySecret = "hybrid-proxy-secret";

        await using var app = await CreateTestAppAsync(services =>
        {
            services
                .AddTicketFlowHybridAuthentication(
                    primaryScheme: ExternalIdentityAuthenticationOptions.DefaultScheme,
                    configureApiKey: apiKey => apiKey.DefaultScope = DefaultScope)
                .AddTicketFlowExternalIdentityAuthentication(
                    ExternalIdentityAuthenticationOptions.DefaultScheme,
                    options => options.UseIdentityFlowDefaults(sharedSecret: proxySecret, acceptFederatedRoles: true));
        });

        var grantStore = app.Services.GetRequiredService<IAccessGrantStore>();
        var partition = AccessPartition.From(DefaultScope);
        var bootstrapOwner = new UserPrincipal(DefaultScope, new UserId("bootstrap-owner"));
        await grantStore.CreateAsync(new AccessGrant(
            new GrantId("boot-owner"),
            partition,
            bootstrapOwner.UserId,
            BuiltInRoles.Owner.Id,
            TenantGrantScope.Instance));

        string plaintextKey;
        var clientId = new ApiClientId("ci-reporter");
        using (var scope = app.Services.CreateScope())
        {
            var serviceAccounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
            var createSa = await serviceAccounts.CreateServiceAccountAsync(
                bootstrapOwner,
                new ServiceAccount(clientId, partition, TenantGrantScope.Instance, "CI Reporter"));
            Assert.IsTrue(createSa.IsSuccess);

            var issueKey = await serviceAccounts.IssueApiKeyAsync(
                bootstrapOwner,
                new ApiKeyCreateRequest(clientId, "primary-key"));
            Assert.IsTrue(issueKey.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> issued));
            plaintextKey = issued.Value.Value.PlaintextKey;
            Assert.IsTrue(plaintextKey.StartsWith("tf_sa_", StringComparison.Ordinal));
        }

        await grantStore.CreateAsync(new AccessGrant(
            new GrantId("bot-viewer"),
            partition,
            clientId,
            BuiltInRoles.Viewer.Id,
            TenantGrantScope.Instance));

        using var client = app.GetTestClient();

        // 1. IdentityFlow terminated user
        using (var userReq = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            userReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            userReq.Headers.Add("X-Auth-Subject", "hybrid-alice");
            using var resp = await client.SendAsync(userReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("user:hybrid-alice", payload.PrincipalId);
        }

        // 2. Service-account API key via X-Api-Key
        using (var apiKeyReq = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            apiKeyReq.Headers.Add("X-Api-Key", plaintextKey);
            using var resp = await client.SendAsync(apiKeyReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("client:ci-reporter", payload.PrincipalId);
            Assert.IsTrue(payload.CanReadConversation);
        }

        // 3. Service-account API key via Authorization: Bearer tf_sa_...
        using (var bearerReq = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            bearerReq.Headers.Add("Authorization", $"Bearer {plaintextKey}");
            using var resp = await client.SendAsync(bearerReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("client:ci-reporter", payload.PrincipalId);
            Assert.IsTrue(payload.CanReadConversation);
        }
    }

    [TestMethod]
    public async Task RoleCatalogEndpointReturns401Unauthenticated403NonAdminAnd200CatalogForAdmin()
    {
        const string proxySecret = "catalog-secret";
        await using var app = await CreateTestAppAsync(
            services =>
            {
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseIdentityFlowDefaults(sharedSecret: proxySecret, acceptFederatedRoles: false);
                });
            },
            mapRoleCatalog: true);

        var grantStore = app.Services.GetRequiredService<IAccessGrantStore>();
        var roleStore = app.Services.GetRequiredService<IAccessRoleStore>();
        var mappingStore = app.Services.GetRequiredService<IRoleFederationMappingStore>();
        var partition = AccessPartition.From(DefaultScope);

        await grantStore.CreateAsync(new AccessGrant(
            new GrantId("g-viewer"),
            partition,
            new UserId("viewer-user"),
            BuiltInRoles.Viewer.Id,
            TenantGrantScope.Instance));
        await grantStore.CreateAsync(new AccessGrant(
            new GrantId("g-admin"),
            partition,
            new UserId("admin-user"),
            BuiltInRoles.Owner.Id,
            TenantGrantScope.Instance));
        await roleStore.CreateAsync(
            DefaultScope,
            new AccessRole(new RoleId("custom-reader"), "Custom Reader", [AccessPermissions.ConversationRead]));
        await mappingStore.UpsertAsync(
            new RoleFederationMappingId("map-catalog-1"),
            new RoleFederationMapping("identityflow", "custom-readers", new RoleId("custom-reader"), partition, new WorkspaceGrantScope(DefaultScope.WorkspaceId)),
            isEnabled: true,
            "system:seed");

        using var client = app.GetTestClient();

        // 1. Unauthenticated -> 401
        using (var unauthReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles"))
        {
            using var resp = await client.SendAsync(unauthReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 2. Non-admin -> 403
        using (var viewerReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles"))
        {
            viewerReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            viewerReq.Headers.Add("X-Auth-Subject", "viewer-user");
            using var resp = await client.SendAsync(viewerReq);
            Assert.AreEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 3. Admin -> 200 JSON catalog
        using (var adminReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles"))
        {
            adminReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            adminReq.Headers.Add("X-Auth-Subject", "admin-user");
            using var resp = await client.SendAsync(adminReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);

            using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.AreEqual("tenant-a", json.RootElement.GetProperty("tenantId").GetString());
            Assert.AreEqual("ws-main", json.RootElement.GetProperty("workspaceId").GetProperty("value").GetString());
            var roles = json.RootElement.GetProperty("roles");
            Assert.AreEqual(BuiltInRoles.All.Count + 1, roles.GetArrayLength());

            var foundCustom = false;
            foreach (var role in roles.EnumerateArray())
                if (string.Equals(role.GetProperty("roleId").GetProperty("value").GetString(), "custom-reader", StringComparison.Ordinal))
                {
                    foundCustom = true;
                    Assert.IsFalse(role.GetProperty("isBuiltIn").GetBoolean());
                    Assert.AreEqual(1, role.GetProperty("mappings").GetArrayLength());
                }

            Assert.IsTrue(foundCustom);
        }
    }

    private static async Task<WebApplication> CreateTestAppAsync(
        Action<IServiceCollection> configureAuth,
        Action<AccessControlOptions>? configureAccess = null,
        string? remoteIpHeader = null,
        bool mapRoleCatalog = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddTicketFlow().AddAccessControl(configureAccess).AddInMemoryAccessStores();
        configureAuth(builder.Services);

        var app = builder.Build();
        if (!string.IsNullOrWhiteSpace(remoteIpHeader))
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.TryGetValue(remoteIpHeader, out var values) && IPAddress.TryParse(values.ToString(), out var ip))
                    context.Connection.RemoteIpAddress = ip;
                await next().ConfigureAwait(false);
            });

        app.UseAuthentication();

        app.MapGet("/whoami", async (HttpContext http, IAccessPrincipalResolver resolver, IAccessEvaluator evaluator) =>
        {
            if (http.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            var scope = TicketFlowClaimsScopeResolver.FromClaims(http, DefaultScope);
            if (scope is null)
                return Results.BadRequest();

            var principal = await resolver.ResolveAsync(http.User, scope, http.RequestAborted).ConfigureAwait(false);
            if (principal is null)
                return Results.Unauthorized();

            var resource = new AccessResource(scope);
            var canRead = (await evaluator.AuthorizeAsync(new AccessRequest(principal, AccessPermissions.ConversationRead, resource), http.RequestAborted).ConfigureAwait(false)).IsPermitted;
            var canReply = (await evaluator.AuthorizeAsync(new AccessRequest(principal, AccessPermissions.ConversationReply, resource), http.RequestAborted).ConfigureAwait(false)).IsPermitted;

            var principalId = principal switch
            {
                UserPrincipal u => $"user:{u.UserId.Value}",
                ApiClientPrincipal c => $"client:{c.ClientId.Value}",
                AutomationPrincipal a => $"automation:{a.AutomationId}",
                SystemPrincipal s => $"system:{s.Component}",
            };
            var teams = principal is UserPrincipal user ? user.Teams.Select(static t => t.Value).ToArray() : [];

            return Results.Ok(new WhoAmIResponse(
                principalId,
                $"{scope.TenantId}/{scope.ProjectId}/{scope.EnvironmentId}/{scope.AppId}/{scope.WorkspaceId.Value}",
                http.User.FindFirst("idp")?.Value,
                teams,
                principal.ExternalRoles.ToArray(),
                canRead,
                canReply));
        });

        if (mapRoleCatalog)
            app.MapTicketFlowRoleCatalogEndpoint(defaultScope: DefaultScope);

        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    private sealed record WhoAmIResponse(
        string PrincipalId,
        string Scope,
        string? Idp,
        string[] Teams,
        string[] ExternalRoles,
        bool CanReadConversation,
        bool CanReplyConversation);
}
