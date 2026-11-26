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
            Assert.IsTrue(resp.Headers.CacheControl?.NoStore, "/.well-known/ticketflow-roles must return Cache-Control: no-store.");
            Assert.IsTrue(resp.Headers.Pragma.Any(p => string.Equals(p.Name, "no-cache", StringComparison.OrdinalIgnoreCase)),
                "/.well-known/ticketflow-roles must return Pragma: no-cache.");
        }
    }

    [TestMethod]
    public async Task OAuth2ProxyIgnoresSpoofedAuthHeadersAndRejectsCommaCoalescedSingleHeaders()
    {
        const string secret = "oauth2-hardened-secret";
        await using var app = await CreateTestAppAsync(
            services =>
            {
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseOAuth2ProxyDefaults(sharedSecret: secret, acceptFederatedRoles: true);
                });
            });

        using var client = app.GetTestClient();

        // Spoofed X-Auth-Client-Id, X-Auth-Principal-Type, X-Auth-Tenant, X-Auth-Workspace, X-Auth-Roles are ignored;
        // X-Forwarded-Groups is mapped to roles when acceptFederatedRoles: true, and comma is allowed in DisplayNameHeader
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            req.Headers.Add("X-OAuth2-Proxy-Secret", secret);
            req.Headers.Add("X-Forwarded-User", "oauth-alice");
            req.Headers.Add("X-Forwarded-Preferred-Username", "Lovelace, Ada");
            req.Headers.Add("X-Forwarded-Groups", "support-agents");
            req.Headers.Add("X-Auth-Client-Id", "spoofed-bot");
            req.Headers.Add("X-Auth-Principal-Type", "service_account");
            req.Headers.Add("X-Auth-Tenant", "evil-tenant");
            req.Headers.Add("X-Auth-Workspace", "evil-ws");
            req.Headers.Add("X-Auth-Roles", "spoofed-owner");

            using var resp = await client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var payload = await resp.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.IsNotNull(payload);
            Assert.AreEqual("user:oauth-alice", payload.PrincipalId);
            Assert.AreEqual("tenant-a/proj-a/prod/support/ws-main", payload.Scope);
            CollectionAssert.Contains(payload.ExternalRoles, "support-agents");
            CollectionAssert.DoesNotContain(payload.ExternalRoles, "spoofed-owner");
        }

        // Comma-coalesced multiple distinct values on X-Forwarded-User -> 401
        using (var coalescedSub = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            coalescedSub.Headers.Add("X-OAuth2-Proxy-Secret", secret);
            coalescedSub.Headers.Add("X-Forwarded-User", "alice, bob");
            using var resp = await client.SendAsync(coalescedSub);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Comma-coalesced multiple distinct values on shared secret -> 401
        using (var coalescedSecret = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
        {
            coalescedSecret.Headers.Add("X-OAuth2-Proxy-Secret", $"{secret}, other");
            coalescedSecret.Headers.Add("X-Forwarded-User", "oauth-alice");
            using var resp = await client.SendAsync(coalescedSecret);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    [TestMethod]
    public async Task ApiKeyScopeResolutionFailsClosedOnAmbiguousQueryOrNullResolveScopeAndRoleCatalogRespectsHostResolver()
    {
        // Part 1: Ambiguous ?workspace=a&workspace=b fails closed instead of falling back to DefaultScope
        await using (var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowApiKeyAuthentication(apiKey => apiKey.DefaultScope = DefaultScope, setAsDefault: true);
        }))
        {
            var partition = AccessPartition.From(DefaultScope);
            var owner = new UserPrincipal(DefaultScope, new UserId("owner"));
            await app.Services.GetRequiredService<IAccessGrantStore>().CreateAsync(
                new AccessGrant(new GrantId("g-o"), partition, owner.UserId, BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

            string plaintextKey;
            using (var scope = app.Services.CreateScope())
            {
                var sa = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
                var clientId = new ApiClientId("scope-bot");
                Assert.IsTrue((await sa.CreateServiceAccountAsync(owner, new ServiceAccount(clientId, partition, TenantGrantScope.Instance, "Scope Bot"))).IsSuccess);
                var issued = await sa.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(clientId, "k1"));
                Assert.IsTrue(issued.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> ok));
                plaintextKey = ok.Value.Value.PlaintextKey;
            }

            using var client = app.GetTestClient();
            using (var ambiguous = new HttpRequestMessage(HttpMethod.Get, "/whoami?workspace=ws-main&workspace=ws-other"))
            {
                ambiguous.Headers.Add("X-Api-Key", plaintextKey);
                using var resp = await client.SendAsync(ambiguous);
                Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
        }

        // Part 2: Configured ResolveScope returning null fails closed even when DefaultScope is set
        await using (var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowApiKeyAuthentication(apiKey =>
            {
                apiKey.DefaultScope = DefaultScope;
                apiKey.ResolveScope = _ => ValueTask.FromResult<TicketScope?>(null);
            }, setAsDefault: true);
        }))
        {
            var partition = AccessPartition.From(DefaultScope);
            var owner = new UserPrincipal(DefaultScope, new UserId("owner"));
            await app.Services.GetRequiredService<IAccessGrantStore>().CreateAsync(
                new AccessGrant(new GrantId("g-o2"), partition, owner.UserId, BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

            string plaintextKey;
            using (var scope = app.Services.CreateScope())
            {
                var sa = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
                var clientId = new ApiClientId("null-scope-bot");
                Assert.IsTrue((await sa.CreateServiceAccountAsync(owner, new ServiceAccount(clientId, partition, TenantGrantScope.Instance, "Null Scope Bot"))).IsSuccess);
                var issued = await sa.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(clientId, "k1"));
                Assert.IsTrue(issued.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> ok));
                plaintextKey = ok.Value.Value.PlaintextKey;
            }

            using var client = app.GetTestClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
            req.Headers.Add("X-Api-Key", plaintextKey);
            using var resp = await client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Part 3: /.well-known/ticketflow-roles respects host ITicketFlowPrincipalResolver returning null (no fallback to IAccessPrincipalResolver)
        const string proxySecret = "host-resolver-secret";
        await using (var app = await CreateTestAppAsync(
            services =>
            {
                services.AddScoped<TicketFlow.Hosting.AspNetCore.ITicketFlowPrincipalResolver, RefusingHostPrincipalResolver>();
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseIdentityFlowDefaults(sharedSecret: proxySecret);
                });
            },
            mapRoleCatalog: true))
        {
            await app.Services.GetRequiredService<IAccessGrantStore>().CreateAsync(
                new AccessGrant(new GrantId("g-adm"), AccessPartition.From(DefaultScope), new UserId("admin-user"), BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

            using var client = app.GetTestClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles");
            req.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            req.Headers.Add("X-Auth-Subject", "admin-user");
            using var resp = await client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.IsTrue(resp.Headers.CacheControl?.NoStore);
        }
    }

    private sealed class RefusingHostPrincipalResolver : TicketFlow.Hosting.AspNetCore.ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PrincipalRef?>(null);
    }

    private sealed class WrongScopeHostPrincipalResolver(TicketScope wrongScope) : TicketFlow.Hosting.AspNetCore.ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PrincipalRef?>(new UserPrincipal(wrongScope, new UserId("admin-user")));
    }

    [TestMethod]
    public async Task Wave2SecurityHardeningsForApiKeyScopeClaimsDualCredentialsSharedSecretAndScopeResolver()
    {
        // 1. TenantGrantScope API key omits workspace_id claim; WorkspaceGrantScope emits it; conflicting X-Api-Key + Bearer fails closed
        await using (var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowApiKeyAuthentication(apiKey => apiKey.DefaultScope = DefaultScope, setAsDefault: true);
        }))
        {
            var partition = AccessPartition.From(DefaultScope);
            var owner = new UserPrincipal(DefaultScope, new UserId("owner"));
            await app.Services.GetRequiredService<IAccessGrantStore>().CreateAsync(
                new AccessGrant(new GrantId("g-o"), partition, owner.UserId, BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

            string tenantKey;
            string wsKey;
            using (var scope = app.Services.CreateScope())
            {
                var sa = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
                var tenantClient = new ApiClientId("tenant-wide-bot");
                var wsClient = new ApiClientId("ws-pinned-bot");
                Assert.IsTrue((await sa.CreateServiceAccountAsync(owner, new ServiceAccount(tenantClient, partition, TenantGrantScope.Instance, "Tenant Bot"))).IsSuccess);
                Assert.IsTrue((await sa.CreateServiceAccountAsync(owner, new ServiceAccount(wsClient, partition, new WorkspaceGrantScope(DefaultScope.WorkspaceId), "WS Bot"))).IsSuccess);

                Assert.IsTrue((await sa.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(tenantClient, "k-t"))).TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> tk));
                Assert.IsTrue((await sa.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(wsClient, "k-w"))).TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> wk));
                tenantKey = tk.Value.Value.PlaintextKey;
                wsKey = wk.Value.Value.PlaintextKey;
            }

            using var client = app.GetTestClient();

            // Tenant-scoped API key must NOT emit workspace_id claim (retains 4 partition claims)
            using (var req = new HttpRequestMessage(HttpMethod.Get, "/claims"))
            {
                req.Headers.Add("X-Api-Key", tenantKey);
                using var resp = await client.SendAsync(req);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
                var claims = await resp.Content.ReadFromJsonAsync<Dictionary<string, string[]>>();
                Assert.IsNotNull(claims);
                Assert.IsTrue(claims.ContainsKey(TicketFlowClaimsScopeResolver.TenantClaimType));
                Assert.IsTrue(claims.ContainsKey(TicketFlowClaimsScopeResolver.ProjectClaimType));
                Assert.IsTrue(claims.ContainsKey(TicketFlowClaimsScopeResolver.EnvironmentClaimType));
                Assert.IsTrue(claims.ContainsKey(TicketFlowClaimsScopeResolver.AppClaimType));
                Assert.IsFalse(claims.ContainsKey(TicketFlowClaimsScopeResolver.WorkspaceClaimType), "TenantGrantScope API keys must omit workspace_id.");
            }

            // Workspace-scoped API key emits workspace_id
            using (var req = new HttpRequestMessage(HttpMethod.Get, "/claims"))
            {
                req.Headers.Add("X-Api-Key", wsKey);
                using var resp = await client.SendAsync(req);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
                var claims = await resp.Content.ReadFromJsonAsync<Dictionary<string, string[]>>();
                Assert.IsNotNull(claims);
                Assert.AreEqual(DefaultScope.WorkspaceId.Value, claims[TicketFlowClaimsScopeResolver.WorkspaceClaimType][0]);
            }

            // Conflicting X-Api-Key and Authorization: Bearer tokens fail closed -> 401
            using (var conflict = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                conflict.Headers.Add("X-Api-Key", tenantKey);
                conflict.Headers.Add("Authorization", $"Bearer {wsKey}");
                using var resp = await client.SendAsync(conflict);
                Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            }

            // Matching X-Api-Key and Authorization: Bearer succeeds -> 200
            using (var matching = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                matching.Headers.Add("X-Api-Key", tenantKey);
                matching.Headers.Add("Authorization", $"Bearer {tenantKey}");
                using var resp = await client.SendAsync(matching);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            }
        }

        // 2. RoleCatalog rejects mismatched scope from host resolver BEFORE external claims enrichment
        const string proxySecret = "scope-order-secret";
        var wrongScope = new TicketScope("tenant-b", "proj-b", "prod", "support", new WorkspaceId("ws-other"));
        await using (var app = await CreateTestAppAsync(
            services =>
            {
                services.AddScoped<TicketFlow.Hosting.AspNetCore.ITicketFlowPrincipalResolver>(_ => new WrongScopeHostPrincipalResolver(wrongScope));
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseIdentityFlowDefaults(sharedSecret: proxySecret, acceptFederatedRoles: true);
                });
            },
            mapRoleCatalog: true))
        {
            await app.Services.GetRequiredService<IAccessGrantStore>().CreateAsync(
                new AccessGrant(new GrantId("g-adm"), AccessPartition.From(DefaultScope), new UserId("admin-user"), BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

            using var client = app.GetTestClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles");
            req.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            req.Headers.Add("X-Auth-Subject", "admin-user");
            req.Headers.Add("X-Auth-Roles", "idf:owner");
            using var resp = await client.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 3. SharedSecretHeader accepts secret containing comma verbatim and rejects whitespace/duplicated/comma-repeated values
        const string commaSecret = "sec,ret-with-comma";
        await using (var app = await CreateTestAppAsync(services =>
        {
            services.AddTicketFlowExternalIdentityAuthentication(options =>
            {
                options.UseIdentityFlowDefaults(sharedSecret: commaSecret);
            });
        }))
        {
            using var client = app.GetTestClient();

            using (var valid = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                valid.Headers.TryAddWithoutValidation("X-IdentityFlow-Proxy-Secret", commaSecret);
                valid.Headers.Add("X-Auth-Subject", "alice");
                using var resp = await client.SendAsync(valid);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            }

            using (var padded = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                padded.Headers.TryAddWithoutValidation("X-IdentityFlow-Proxy-Secret", $" {commaSecret} ");
                padded.Headers.Add("X-Auth-Subject", "alice");
                using var resp = await client.SendAsync(padded);
                Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            }

            using (var repeated = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                repeated.Headers.TryAddWithoutValidation("X-IdentityFlow-Proxy-Secret", $"{commaSecret}, {commaSecret}");
                repeated.Headers.Add("X-Auth-Subject", "alice");
                using var resp = await client.SendAsync(repeated);
                Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            }

            using (var dupLines = new HttpRequestMessage(HttpMethod.Get, "/whoami"))
            {
                dupLines.Headers.TryAddWithoutValidation("X-IdentityFlow-Proxy-Secret", commaSecret);
                dupLines.Headers.TryAddWithoutValidation("X-IdentityFlow-Proxy-Secret", commaSecret);
                dupLines.Headers.Add("X-Auth-Subject", "alice");
                using var resp = await client.SendAsync(dupLines);
                Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
        }

        // 4. TicketFlowClaimsScopeResolver.FromPrincipal fails closed on invalid/conflicting claims and blocks cross-ancestor fallback mixing
        static System.Security.Claims.ClaimsPrincipal MakePrincipal(params (string Type, string Value)[] claims) =>
            new(new System.Security.Claims.ClaimsIdentity(claims.Select(c => new System.Security.Claims.Claim(c.Type, c.Value)), "Test"));

        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("tenant_id", "tenant-a"), ("tenant_id", "tenant-b")), DefaultScope));
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("environment_id", "prod"), ("environment", "staging")), DefaultScope));
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("app_id", "support"), ("app_name", "billing")), DefaultScope));
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("workspace_id", "ws-main"), ("workspace", "ws-other")), DefaultScope));
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("tenant_id", "tenant-a"), ("tenant_id", "bad\0value")), DefaultScope));
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("tenant_id", "")), DefaultScope));

        // Cross-ancestor mixing blocked: different tenant_id must NOT inherit project/env/app/workspace from DefaultScope
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("tenant_id", "tenant-b")), DefaultScope));
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("tenant_id", "tenant-a"), ("project_id", "proj-b")), DefaultScope));
        Assert.IsNull(TicketFlowClaimsScopeResolver.FromPrincipal(MakePrincipal(("tenant_id", "tenant-a"), ("project_id", "proj-a"), ("environment_id", "staging"), ("app_id", "support")), DefaultScope));

        // Matching partition claims cleanly inherit fallback workspace
        var inherited = TicketFlowClaimsScopeResolver.FromPrincipal(
            MakePrincipal(("tenant_id", "tenant-a"), ("project_id", "proj-a"), ("environment_id", "prod"), ("app_id", "support")),
            DefaultScope);
        Assert.AreEqual(DefaultScope, inherited);
    }

    [TestMethod]
    public async Task RoleCatalogEndpointEnforcesScopeClaimsAndServiceAccountStateForCustomHostResolvers()
    {
        const string proxySecret = "catalog-host-resolver-secret";
        var partition = AccessPartition.From(DefaultScope);
        await using var app = await CreateTestAppAsync(
            services =>
            {
                services.AddScoped<TicketFlow.Hosting.AspNetCore.ITicketFlowPrincipalResolver, ClaimEchoHostPrincipalResolver>();
                services.AddTicketFlowExternalIdentityAuthentication(options =>
                {
                    options.UseIdentityFlowDefaults(sharedSecret: proxySecret);
                });
            },
            mapRoleCatalog: true,
            resolveRoleCatalogScope: _ => ValueTask.FromResult<TicketScope?>(DefaultScope));

        var grants = app.Services.GetRequiredService<IAccessGrantStore>();
        var saStore = app.Services.GetRequiredService<IServiceAccountStore>();
        await grants.CreateAsync(new AccessGrant(new GrantId("g-admin-u"), partition, new UserId("admin-user"), BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

        var disabledClient = new ApiClientId("sa-disabled-cat");
        await saStore.CreateAsync(new ServiceAccount(disabledClient, partition, TenantGrantScope.Instance, "Disabled Cat SA"), "system:test");
        await saStore.SetEnabledAsync(partition, disabledClient, isEnabled: false, "system:test", "disabled");
        await grants.CreateAsync(new AccessGrant(new GrantId("g-disabled-sa"), partition, disabledClient, BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

        var otherWsClient = new ApiClientId("sa-other-ws-cat");
        await saStore.CreateAsync(new ServiceAccount(otherWsClient, partition, new WorkspaceGrantScope(new WorkspaceId("ws-other")), "Other WS Cat SA"), "system:test");
        await grants.CreateAsync(new AccessGrant(new GrantId("g-other-ws-sa"), partition, otherWsClient, BuiltInRoles.Owner.Id, TenantGrantScope.Instance));

        var enabledClient = new ApiClientId("sa-enabled-cat");
        await saStore.CreateAsync(new ServiceAccount(enabledClient, partition, new WorkspaceGrantScope(DefaultScope.WorkspaceId), "Enabled Cat SA"), "system:test");
        await grants.CreateAsync(new AccessGrant(new GrantId("g-enabled-sa"), partition, enabledClient, BuiltInRoles.Owner.Id, new WorkspaceGrantScope(DefaultScope.WorkspaceId)));

        using var client = app.GetTestClient();

        // 1. Mismatched workspace claim is rejected BEFORE delegating to custom host resolver -> 401
        using (var mismatched = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles"))
        {
            mismatched.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            mismatched.Headers.Add("X-Auth-Subject", "admin-user");
            mismatched.Headers.Add("X-Auth-Workspace", "ws-other");
            using var resp = await client.SendAsync(mismatched);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 2. Disabled service account resolved by host resolver -> 401
        using (var disabledReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles"))
        {
            disabledReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            disabledReq.Headers.Add("X-Auth-Client-Id", disabledClient.Value);
            disabledReq.Headers.Add("X-Auth-Principal-Type", "service_account");
            using var resp = await client.SendAsync(disabledReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 3. Out-of-scope service account resolved by host resolver -> 401
        using (var otherWsReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles"))
        {
            otherWsReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            otherWsReq.Headers.Add("X-Auth-Client-Id", otherWsClient.Value);
            otherWsReq.Headers.Add("X-Auth-Principal-Type", "service_account");
            using var resp = await client.SendAsync(otherWsReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 4. Enabled in-scope service account with matching scope -> 200
        using (var okReq = new HttpRequestMessage(HttpMethod.Get, "/.well-known/ticketflow-roles"))
        {
            okReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            okReq.Headers.Add("X-Auth-Client-Id", enabledClient.Value);
            okReq.Headers.Add("X-Auth-Principal-Type", "service_account");
            using var resp = await client.SendAsync(okReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    private sealed class ClaimEchoHostPrincipalResolver : TicketFlow.Hosting.AspNetCore.ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default)
        {
            if (httpContext.User.FindFirst("client_id")?.Value is { Length: > 0 } clientId)
                return ValueTask.FromResult<PrincipalRef?>(new ApiClientPrincipal(scope, new ApiClientId(clientId)));
            if (httpContext.User.FindFirst("sub")?.Value is { Length: > 0 } sub)
                return ValueTask.FromResult<PrincipalRef?>(new UserPrincipal(scope, new UserId(sub)));
            return ValueTask.FromResult<PrincipalRef?>(null);
        }
    }

    private static async Task<WebApplication> CreateTestAppAsync(
        Action<IServiceCollection> configureAuth,
        Action<AccessControlOptions>? configureAccess = null,
        string? remoteIpHeader = null,
        bool mapRoleCatalog = false,
        Func<HttpContext, ValueTask<TicketScope?>>? resolveRoleCatalogScope = null)
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

        app.MapGet("/claims", (HttpContext http) =>
        {
            if (http.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            var grouped = http.User.Claims
                .GroupBy(c => c.Type, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray(), StringComparer.Ordinal);
            return Results.Ok(grouped);
        });

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
            app.MapTicketFlowRoleCatalogEndpoint(resolveScope: resolveRoleCatalogScope, defaultScope: DefaultScope);

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
