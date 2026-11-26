using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Access.AspNetCore;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.SampleHost;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Tests;

[TestClass]
public sealed partial class ServiceAccountsAndRoleFederationTests
{
    [TestMethod]
    public async Task ServiceAccountsFullLifecycleAndApiKeyAuthEndToEnd()
    {
        await using var app = await StartSampleHostAsync();
        using var admin = CreateDevClient(app, "admin");
        using var rawClient = app.GetTestServer().CreateClient();

        // 1. Seeded demo service accounts are rendered and demo API key authenticates
        var initialHtml = await GetStringAsync(admin, "/support/access/service-accounts");
        StringAssert.Contains(initialHtml, "Ticket Sync Worker", StringComparison.Ordinal);
        StringAssert.Contains(initialHtml, "Reporting Exporter", StringComparison.Ordinal);
        StringAssert.Contains(initialHtml, "tf_sa_demo0001…", StringComparison.Ordinal);
        StringAssert.Contains(initialHtml, "tf_sa_demo0002…", StringComparison.Ordinal);

        using (var demoReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            demoReq.Headers.Add("X-Api-Key", SampleSeeder.DemoReportingExportApiKey);
            using var demoResp = await rawClient.SendAsync(demoReq);
            Assert.AreEqual(HttpStatusCode.OK, demoResp.StatusCode);
            StringAssert.Contains(await demoResp.Content.ReadAsStringAsync(), "Reporting Exporter", StringComparison.Ordinal);
        }

        // Seeded tf_sa_* API key also works seamlessly on REST API endpoints (/ticketflow/api/...)
        using (var restReq = new HttpRequestMessage(HttpMethod.Get, "/ticketflow/api/workspaces/acme/conversations"))
        {
            restReq.Headers.Add("X-Api-Key", SampleSeeder.DemoReportingExportApiKey);
            using var restResp = await rawClient.SendAsync(restReq);
            Assert.AreEqual(HttpStatusCode.OK, restResp.StatusCode);
        }

        // 2. Create service account with initial role (Viewer)
        var (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        using (var createResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.create",
            ["_return"] = "/support/access/service-accounts",
            ["name"] = "E2E Lifecycle Bot",
            ["clientId"] = "sa-e2e-lifecycle",
            ["scope"] = "workspace",
            ["role"] = "viewer",
            ["description"] = "End-to-end test service account",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, createResp.StatusCode);
            Assert.AreEqual("/support/access/service-accounts", createResp.Headers.Location?.OriginalString);
        }

        var afterCreateHtml = await GetStringAsync(admin, "/support/access/service-accounts");
        StringAssert.Contains(afterCreateHtml, "data-service-account=\"sa-e2e-lifecycle\"", StringComparison.Ordinal);
        StringAssert.Contains(afterCreateHtml, "E2E Lifecycle Bot", StringComparison.Ordinal);
        StringAssert.Contains(afterCreateHtml, "Viewer", StringComparison.Ordinal);

        // 3. Update display name & description
        var saCard = ExtractServiceAccountCard(afterCreateHtml, "sa-e2e-lifecycle");
        var saVersion = ExtractRevision(saCard);
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        using (var updateResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.update",
            ["_revision"] = saVersion,
            ["clientId"] = "sa-e2e-lifecycle",
            ["name"] = "E2E Lifecycle Bot v2",
            ["description"] = "Updated description",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, updateResp.StatusCode);
        }

        var afterUpdateHtml = await GetStringAsync(admin, "/support/access/service-accounts");
        StringAssert.Contains(afterUpdateHtml, "E2E Lifecycle Bot v2", StringComparison.Ordinal);

        // 4. Issue API key (one-time plaintext key via PageState, Cache-Control: no-store, no flash cookie)
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        string firstKey;
        using (var issueResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.key.create",
            ["clientId"] = "sa-e2e-lifecycle",
            ["name"] = "primary-e2e-key",
            ["expiresDays"] = "90",
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, issueResp.StatusCode);
            Assert.IsTrue(issueResp.Headers.CacheControl?.NoStore, "Issued API key response must carry Cache-Control: no-store.");
            Assert.IsFalse(
                issueResp.Headers.TryGetValues("Set-Cookie", out var setCookies)
                && setCookies.Any(c => c.StartsWith("tf-flash=", StringComparison.Ordinal)),
                "Issued API key must never be stored in a cookie.");

            var issueHtml = await issueResp.Content.ReadAsStringAsync();
            StringAssert.Contains(issueHtml, "data-issued-api-key", StringComparison.Ordinal);
            StringAssert.Contains(issueHtml, "X-Api-Key", StringComparison.Ordinal);
            StringAssert.Contains(issueHtml, "Authorization: Bearer", StringComparison.Ordinal);
            firstKey = ExtractIssuedApiKey(issueHtml);
            StringAssert.StartsWith(firstKey, "tf_sa_", StringComparison.Ordinal);
        }

        // 5. Subsequent GET must NOT contain the plaintext key (only the non-secret prefix)
        var afterIssueHtml = await GetStringAsync(admin, "/support/access/service-accounts");
        Assert.IsFalse(afterIssueHtml.Contains(firstKey, StringComparison.Ordinal), "Plaintext key must never appear on subsequent GET.");
        var firstPrefix = firstKey[..firstKey.IndexOf('.')];
        StringAssert.Contains(afterIssueHtml, $"{firstPrefix}…", StringComparison.Ordinal);

        // 6. Call UI with X-Api-Key: 200 on allowed page, 403 on admin page
        using (var allowedReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            allowedReq.Headers.Add("X-Api-Key", firstKey);
            using var resp = await rawClient.SendAsync(allowedReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "E2E Lifecycle Bot v2", StringComparison.Ordinal);
        }
        using (var adminReq = new HttpRequestMessage(HttpMethod.Get, "/support/access"))
        {
            adminReq.Headers.Add("X-Api-Key", firstKey);
            using var resp = await rawClient.SendAsync(adminReq);
            Assert.AreEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 7. Call UI with Authorization: Bearer: 200 on allowed page, 403 on admin page
        using (var bearerAllowed = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            bearerAllowed.Headers.Add("Authorization", $"Bearer {firstKey}");
            using var resp = await rawClient.SendAsync(bearerAllowed);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }
        using (var bearerAdmin = new HttpRequestMessage(HttpMethod.Get, "/support/access/service-accounts"))
        {
            bearerAdmin.Headers.Add("Authorization", $"Bearer {firstKey}");
            using var resp = await rawClient.SendAsync(bearerAdmin);
            Assert.AreEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 8. Invalid API key returns 401 with Cache-Control: no-store (never redirects to LoginPath)
        using (var invalidHeaderReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            invalidHeaderReq.Headers.Add("X-Api-Key", "tf_sa_00000000.invalid_secret");
            using var resp = await rawClient.SendAsync(invalidHeaderReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.IsTrue(resp.Headers.CacheControl?.NoStore);
        }
        using (var invalidBearerReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            invalidBearerReq.Headers.Add("Authorization", "Bearer tf_sa_00000000.invalid_secret");
            using var resp = await rawClient.SendAsync(invalidBearerReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.IsTrue(resp.Headers.CacheControl?.NoStore);
        }

        // 9. Revoke first key -> calling UI with revoked key returns 401
        saCard = ExtractServiceAccountCard(afterIssueHtml, "sa-e2e-lifecycle");
        var firstKeyId = ExtractFirstKeyId(saCard);
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        using (var revokeResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.key.revoke",
            ["keyId"] = firstKeyId,
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, revokeResp.StatusCode);
        }

        using (var revokedReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            revokedReq.Headers.Add("X-Api-Key", firstKey);
            using var resp = await rawClient.SendAsync(revokedReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 10. Issue a second key, disable SA -> 401, re-enable SA -> 200
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        string secondKey;
        using (var issueSecond = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.key.create",
            ["clientId"] = "sa-e2e-lifecycle",
            ["name"] = "secondary-e2e-key",
        }))
        {
            Assert.AreEqual(HttpStatusCode.OK, issueSecond.StatusCode);
            secondKey = ExtractIssuedApiKey(await issueSecond.Content.ReadAsStringAsync());
        }

        // Verify second key works while enabled
        using (var okReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            okReq.Headers.Add("X-Api-Key", secondKey);
            using var resp = await rawClient.SendAsync(okReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }

        // Disable SA
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        using (var disableResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.disable",
            ["clientId"] = "sa-e2e-lifecycle",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, disableResp.StatusCode);
        }

        using (var disabledReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            disabledReq.Headers.Add("X-Api-Key", secondKey);
            using var resp = await rawClient.SendAsync(disabledReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // Re-enable SA
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        using (var enableResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.enable",
            ["clientId"] = "sa-e2e-lifecycle",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, enableResp.StatusCode);
        }

        using (var reenabledReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            reenabledReq.Headers.Add("X-Api-Key", secondKey);
            using var resp = await rawClient.SendAsync(reenabledReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        }

        // Grant additional role via inline grant action
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        using (var grantResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.grant",
            ["clientId"] = "sa-e2e-lifecycle",
            ["role"] = "agent",
            ["scope"] = "workspace",
            ["effect"] = "Allow",
            ["justification"] = "Upgrade to Agent",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, grantResp.StatusCode);
        }

        var afterGrantHtml = await GetStringAsync(admin, "/support/access/service-accounts");
        StringAssert.Contains(ExtractServiceAccountCard(afterGrantHtml, "sa-e2e-lifecycle"), "Upgrade to Agent", StringComparison.Ordinal);

        // 11. Delete SA -> calling UI with second key returns 401
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/service-accounts");
        using (var deleteResp = await PostAsync(admin, "/support/access/service-accounts", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.serviceaccount.delete",
            ["clientId"] = "sa-e2e-lifecycle",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, deleteResp.StatusCode);
        }

        using (var deletedReq = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            deletedReq.Headers.Add("X-Api-Key", secondKey);
            using var resp = await rawClient.SendAsync(deletedReq);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        var afterDeleteHtml = await GetStringAsync(admin, "/support/access/service-accounts");
        Assert.IsFalse(afterDeleteHtml.Contains("data-service-account=\"sa-e2e-lifecycle\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RoleFederationUiAndWellKnownRoleCatalogEndpoint()
    {
        await using var app = await StartSampleHostAsync();
        using var admin = CreateDevClient(app, "admin");
        using var viewer = CreateDevClient(app, "viewer");
        using var anonymous = app.GetTestServer().CreateClient();

        // 1. Role Federation UI renders mode, seeded mappings, provider datalist, and role catalog
        var html = await GetStringAsync(admin, "/support/access/federation");
        StringAssert.Contains(html, "data-federation-status", StringComparison.Ordinal);
        StringAssert.Contains(html, "MergeWithLocalGrants", StringComparison.Ordinal);
        StringAssert.Contains(html, "idf:support-agents", StringComparison.Ordinal);
        StringAssert.Contains(html, "idf:support-viewers", StringComparison.Ordinal);
        StringAssert.Contains(html, "oauth2-proxy", StringComparison.Ordinal);
        StringAssert.Contains(html, "/.well-known/ticketflow-roles", StringComparison.Ordinal);
        StringAssert.Contains(html, "IRoleFederationService", StringComparison.Ordinal);
        StringAssert.Contains(html, "tf-role-catalog-json", StringComparison.Ordinal);

        // 2. Non-admin gets 403 on Role Federation UI
        using (var viewerUiResp = await viewer.GetAsync("/support/access/federation"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, viewerUiResp.StatusCode);
        }

        // 3. Create, toggle, and delete a mapping via UI forms
        var (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/federation");
        using (var createResp = await PostAsync(admin, "/support/access/federation", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.federation.create",
            ["provider"] = "okta",
            ["externalRole"] = "okta-support-supervisors",
            ["role"] = "supervisor",
            ["scope"] = "workspace",
            ["description"] = "Okta supervisor sync",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, createResp.StatusCode);
        }

        var afterCreate = await GetStringAsync(admin, "/support/access/federation");
        StringAssert.Contains(afterCreate, "okta-support-supervisors", StringComparison.Ordinal);
        var mappingRow = ExtractFederationMappingRow(afterCreate, "okta-support-supervisors");
        var mappingId = ExtractFederationMappingId(mappingRow);
        var mappingVersion = ExtractRevision(mappingRow);

        // Toggle disable
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/federation");
        using (var toggleResp = await PostAsync(admin, "/support/access/federation", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.federation.toggle",
            ["_revision"] = mappingVersion,
            ["id"] = mappingId,
            ["provider"] = "okta",
            ["externalRole"] = "okta-support-supervisors",
            ["role"] = "supervisor",
            ["scope"] = "workspace",
            ["description"] = "Okta supervisor sync",
            ["enabled"] = "false",
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, toggleResp.StatusCode);
        }

        var afterDisable = await GetStringAsync(admin, "/support/access/federation");
        var disabledRow = ExtractFederationMappingRow(afterDisable, "okta-support-supervisors");
        StringAssert.Contains(disabledRow, "Disabled", StringComparison.Ordinal);

        // Delete mapping
        (token, cookie) = await GetAntiforgeryAsync(admin, "/support/access/federation");
        using (var deleteResp = await PostAsync(admin, "/support/access/federation", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.federation.delete",
            ["_revision"] = ExtractRevision(disabledRow),
            ["id"] = mappingId,
        }))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, deleteResp.StatusCode);
        }

        var afterDelete = await GetStringAsync(admin, "/support/access/federation");
        Assert.IsFalse(afterDelete.Contains("okta-support-supervisors", StringComparison.Ordinal));

        // 4. /.well-known/ticketflow-roles endpoint: 401 unauthenticated, 403 non-admin, 200 admin
        using (var unauthCatalog = await anonymous.GetAsync("/.well-known/ticketflow-roles"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthCatalog.StatusCode);
        }
        using (var viewerCatalog = await viewer.GetAsync("/.well-known/ticketflow-roles"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, viewerCatalog.StatusCode);
        }
        using (var adminCatalog = await admin.GetAsync("/.well-known/ticketflow-roles"))
        {
            Assert.AreEqual(HttpStatusCode.OK, adminCatalog.StatusCode);
            using var doc = JsonDocument.Parse(await adminCatalog.Content.ReadAsStringAsync());
            var roles = doc.RootElement.GetProperty("roles");
            Assert.IsGreaterThanOrEqualTo(BuiltInRoles.All.Count, roles.GetArrayLength());

            var agentRole = roles.EnumerateArray().First(r => string.Equals(r.GetProperty("roleId").GetProperty("value").GetString(), "agent", StringComparison.Ordinal));
            var agentMappings = agentRole.GetProperty("mappings").EnumerateArray().Select(m => m.GetProperty("externalRole").GetString()).ToList();
            CollectionAssert.Contains(agentMappings, "idf:support-agents");
        }
    }

    [TestMethod]
    public async Task ExternalIdentityFlowProxyTerminationAndRolesOnlyMode()
    {
        const string proxySecret = "e2e-identityflow-proxy-secret-999";
        var partition = new AccessPartition("ext-tenant", "ext-proj", "prod", "support");
        var workspace = new WorkspaceId("ext-ws");
        var scope = partition.InWorkspace(workspace);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddTicketFlow()
            .AddAccessControl(options =>
            {
                options.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
                options.RoleFederation.MapTeamsAsExternalRoles = true;
            })
            .AddInMemoryAccessStores()
            .AddWorkspaces()
            .AddInMemoryWorkspaceStores()
            .AddContacts()
            .AddInMemoryContactStores()
            .AddConversations()
            .AddInMemoryConversationStores()
            .AddTicketFlowEndpoints(options => options.Partition = partition)
            .AddTicketFlowBlazorUI(options =>
            {
                options.BasePath = "/support";
                options.ProductName = "Federated Support";
                options.Workspace = workspace;
                options.ManageApiKeysInUI = false;
                options.LoginPath = null;
            });

        builder.Services.AddTicketFlowExternalIdentityAuthentication(options =>
        {
            options.UseIdentityFlowDefaults(sharedSecret: proxySecret, acceptFederatedRoles: true);
        });
        builder.Services.AddAuthorization();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapTicketFlowBlazorUI();
        app.MapTicketFlowRoleCatalogEndpoint(defaultScope: scope);

        // Seed workspace settings, local Owner grant for local-admin, and a federated mapping for idf:support-agents -> Agent
        await app.Services.GetRequiredService<IWorkspaceSettingsStore>()
            .CreateAsync(scope, new WorkspaceSettings("Federated Support", "en", "UTC"));
        await app.Services.GetRequiredService<IAccessGrantStore>()
            .CreateAsync(new AccessGrant(
                new GrantId("ext-owner"),
                partition,
                new UserId("local-admin"),
                BuiltInRoles.Owner.Id,
                new WorkspaceGrantScope(workspace)));
        await app.Services.GetRequiredService<IRoleFederationMappingStore>()
            .UpsertAsync(
                new RoleFederationMappingId("ext-fed-agents"),
                new RoleFederationMapping("identityflow", "idf:support-agents", BuiltInRoles.Agent.Id, partition, new WorkspaceGrantScope(workspace)),
                isEnabled: true,
                "system:seed");

        await app.StartAsync();
        using var client = app.GetTestServer().CreateClient();

        // 1. Spoofed header (missing secret) -> 401 Unauthorized, no redirect
        using (var spoofedMissing = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            spoofedMissing.Headers.Add("X-Auth-Subject", "local-admin");
            using var resp = await client.SendAsync(spoofedMissing);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.IsTrue(resp.Headers.CacheControl?.NoStore);
        }

        // Spoofed header (wrong secret) -> 401 Unauthorized
        using (var spoofedWrong = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            spoofedWrong.Headers.Add("X-IdentityFlow-Proxy-Secret", "wrong-secret");
            spoofedWrong.Headers.Add("X-Auth-Subject", "local-admin");
            using var resp = await client.SendAsync(spoofedWrong);
            Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 2. Terminated user with federated role renders display name, gets 200 on allowed route & 403 on admin route
        using (var userAllowed = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            userAllowed.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            userAllowed.Headers.Add("X-Auth-Subject", "ext-elena");
            userAllowed.Headers.Add("X-Auth-Name", "Elena Vance");
            userAllowed.Headers.Add("X-Auth-Email", "elena@acme.test");
            userAllowed.Headers.Add("X-Auth-Roles", "idf:support-agents");
            using var resp = await client.SendAsync(userAllowed);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "Elena Vance", StringComparison.Ordinal);
        }
        using (var userAdmin = new HttpRequestMessage(HttpMethod.Get, "/support/access"))
        {
            userAdmin.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            userAdmin.Headers.Add("X-Auth-Subject", "ext-elena");
            userAdmin.Headers.Add("X-Auth-Name", "Elena Vance");
            userAdmin.Headers.Add("X-Auth-Roles", "idf:support-agents");
            using var resp = await client.SendAsync(userAdmin);
            Assert.AreEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 3. Terminated service account (X-Auth-Client-Id + X-Auth-Principal-Type: service_account) gets federated role
        using (var saAllowed = new HttpRequestMessage(HttpMethod.Get, "/support/conversations"))
        {
            saAllowed.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            saAllowed.Headers.Add("X-Auth-Client-Id", "idf-triage-bot");
            saAllowed.Headers.Add("X-Auth-Principal-Type", "service_account");
            saAllowed.Headers.Add("X-Auth-Name", "Triage Bot");
            saAllowed.Headers.Add("X-Auth-Roles", "idf:support-agents");
            using var resp = await client.SendAsync(saAllowed);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var body = await resp.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "Triage Bot", StringComparison.Ordinal);
        }
        using (var saAdmin = new HttpRequestMessage(HttpMethod.Get, "/support/access"))
        {
            saAdmin.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            saAdmin.Headers.Add("X-Auth-Client-Id", "idf-triage-bot");
            saAdmin.Headers.Add("X-Auth-Principal-Type", "service_account");
            saAdmin.Headers.Add("X-Auth-Roles", "idf:support-agents");
            using var resp = await client.SendAsync(saAdmin);
            Assert.AreEqual(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 4. Service Accounts tab is hidden in roles-only mode (ManageApiKeysInUI = false), Role Federation tab is shown
        using (var adminAccessReq = new HttpRequestMessage(HttpMethod.Get, "/support/access"))
        {
            adminAccessReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            adminAccessReq.Headers.Add("X-Auth-Subject", "local-admin");
            adminAccessReq.Headers.Add("X-Auth-Name", "Local Admin");
            using var resp = await client.SendAsync(adminAccessReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var accessHtml = await resp.Content.ReadAsStringAsync();
            Assert.IsFalse(accessHtml.Contains("/support/access/service-accounts", StringComparison.Ordinal), "Service Accounts tab must be hidden when ManageApiKeysInUI is false.");
            StringAssert.Contains(accessHtml, "/support/access/federation", StringComparison.Ordinal);
        }

        // Direct GET to /support/access/service-accounts shows external-IdP notice and no create/issue forms
        using (var directSaReq = new HttpRequestMessage(HttpMethod.Get, "/support/access/service-accounts"))
        {
            directSaReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            directSaReq.Headers.Add("X-Auth-Subject", "local-admin");
            using var resp = await client.SendAsync(directSaReq);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            var saHtml = await resp.Content.ReadAsStringAsync();
            StringAssert.Contains(saHtml, "Service accounts managed externally", StringComparison.Ordinal);
            Assert.IsFalse(saHtml.Contains("access.serviceaccount.create", StringComparison.Ordinal));
            Assert.IsFalse(saHtml.Contains("access.serviceaccount.key.create", StringComparison.Ordinal));

            // Defense-in-depth: posting access.serviceaccount.create is forbidden when ManageApiKeysInUI is false
            var token = TokenPattern().Match(saHtml).Groups[1].Value;
            var cookie = resp.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)).Split(';')[0];
            using var postReq = new HttpRequestMessage(HttpMethod.Post, "/support/access/service-accounts")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["_action"] = "access.serviceaccount.create",
                    ["name"] = "Blocked SA",
                }),
            };
            postReq.Headers.Add("X-IdentityFlow-Proxy-Secret", proxySecret);
            postReq.Headers.Add("X-Auth-Subject", "local-admin");
            postReq.Headers.Add("Cookie", cookie);
            using var postResp = await client.SendAsync(postReq);
            Assert.AreEqual(HttpStatusCode.Forbidden, postResp.StatusCode);
        }

        await app.StopAsync();
    }

    [TestMethod]
    public async Task UiSessionRespectsHostPrincipalResolverEnrichesMappedStaffAndRefreshesTenantAdminCheck()
    {
        var partition = new AccessPartition("t1", "p1", "prod", "app1");
        var workspace = new WorkspaceId("ws1");
        var scope = partition.InWorkspace(workspace);

        // 1. Host ITicketFlowPrincipalResolver returning null MUST NOT fall back to IAccessPrincipalResolver,
        //    and spoofed amr=external / principal_type MUST NOT trigger the API key shortcut.
        var services = new ServiceCollection();
        services.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddTicketFlowEndpoints(o => o.Partition = partition)
            .AddTicketFlowBlazorUI(o => o.Workspace = workspace);
        services.AddScoped<ITicketFlowPrincipalResolver, NullReturningResolver>();

        await using (var provider = services.BuildServiceProvider())
        using (var serviceScope = provider.CreateScope())
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("sub", "spoofed-admin"),
                    new System.Security.Claims.Claim("amr", "external"),
                    new System.Security.Claims.Claim("principal_type", "user"),
                ], "ExternalIdentity")),
            };
            var session = await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(http);
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.NoPrincipal, session.State);
        }

        // 2. Staff-mapped user (sub != UserId) still gets ExternalRoles and IdentityProvider enriched cleanly!
        var mappedServices = new ServiceCollection();
        mappedServices.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddTicketFlowEndpoints(o => o.Partition = partition)
            .AddTicketFlowBlazorUI(o => o.Workspace = workspace);
        mappedServices.AddScoped<ITicketFlowPrincipalResolver, StaffMappedResolver>();

        await using (var provider = mappedServices.BuildServiceProvider())
        using (var serviceScope = provider.CreateScope())
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("sub", "6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
                    new System.Security.Claims.Claim("role", "idf:support-agents"),
                    new System.Security.Claims.Claim("idp", "identityflow"),
                ], "IdentityFlow.Cookie")),
            };
            var session = await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(http);
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.Ready, session.State);
            var user = Assert.IsInstanceOfType<UserPrincipal>(session.Principal);
            Assert.AreEqual(new UserId("staff-ada"), user.UserId);
            Assert.IsTrue(user.ExternalRoles.Contains("idf:support-agents"));
            Assert.AreEqual("identityflow", user.IdentityProvider);
        }

        // 3. Revoked tenant-admin immediately stops ServiceAccountsPage from reading tenant grants despite stale cache
        await using (var app = await StartSampleHostAsync())
        {
            var settings = app.Services.GetRequiredService<SampleSettings>();
            var grants = app.Services.GetRequiredService<IAccessGrantStore>();
            var saStore = app.Services.GetRequiredService<IServiceAccountStore>();
            var cache = app.Services.GetRequiredService<TicketFlow.UI.Blazor.Infrastructure.UiSensitivePermissionCache>();

            // Seed a tenant-admin user + a tenant grant on a SA
            var tempAdminId = new UserId("temp-tenant-admin");
            var tenantAdminGrant = new AccessGrant(new GrantId("g-temp-tenant-admin"), settings.Partition, tempAdminId, BuiltInRoles.Owner.Id, TenantGrantScope.Instance);
            await grants.CreateAsync(tenantAdminGrant);
            var wsAdminGrant = new AccessGrant(new GrantId("g-temp-ws-admin"), settings.Partition, tempAdminId, BuiltInRoles.Admin.Id, new WorkspaceGrantScope(settings.Workspace));
            await grants.CreateAsync(wsAdminGrant);

            var secretTenantSa = new ApiClientId("sa-tenant-secret");
            await saStore.CreateAsync(new ServiceAccount(secretTenantSa, settings.Partition, TenantGrantScope.Instance, "Secret Tenant SA"), "system:test");
            await grants.CreateAsync(new AccessGrant(new GrantId("g-secret-tenant-sa"), settings.Partition, secretTenantSa, BuiltInRoles.Owner.Id, TenantGrantScope.Instance, justification: "TOP-SECRET-TENANT-GRANT"));

            using var tempClient = CreateDevClient(app, "temp-tenant-admin");
            // Warm up UiSensitivePermissionCache while user holds AccessManageTenant
            var beforeRevokeHtml = await GetStringAsync(tempClient, "/support/access/service-accounts");
            StringAssert.Contains(beforeRevokeHtml, "TOP-SECRET-TENANT-GRANT", StringComparison.Ordinal);

            // Now revoke the tenant-admin grant (UiSensitivePermissionCache STILL has stale AccessManageTenant!)
            await grants.DeleteAsync(settings.Partition, tenantAdminGrant.Id, expectedRevision: 1);

            var afterRevokeHtml = await GetStringAsync(tempClient, "/support/access/service-accounts");
            Assert.IsFalse(afterRevokeHtml.Contains("TOP-SECRET-TENANT-GRANT", StringComparison.Ordinal),
                "Revoked tenant-admin must immediately stop seeing tenant grants despite cached sensitive permissions.");

            // Workspace-scoped Owner (NOT tenant-scoped) MUST NOT have CanManageTenant on ServiceAccountsPage or RoleFederationPage
            var wsOwnerId = new UserId("ws-only-owner");
            await grants.CreateAsync(new AccessGrant(new GrantId("g-ws-only-owner"), settings.Partition, wsOwnerId, BuiltInRoles.Owner.Id, new WorkspaceGrantScope(settings.Workspace)));
            using var wsOwnerClient = CreateDevClient(app, "ws-only-owner");
            var wsSaHtml = await GetStringAsync(wsOwnerClient, "/support/access/service-accounts");
            Assert.IsFalse(wsSaHtml.Contains("TOP-SECRET-TENANT-GRANT", StringComparison.Ordinal), "Workspace-scoped Owner must not see tenant grants.");
            Assert.IsFalse(wsSaHtml.Contains("<option value=\"tenant\"", StringComparison.Ordinal), "Workspace-scoped Owner must not see Tenant scope option on ServiceAccountsPage.");
            var wsFedHtml = await GetStringAsync(wsOwnerClient, "/support/access/federation");
            Assert.IsFalse(wsFedHtml.Contains("<option value=\"tenant\"", StringComparison.Ordinal), "Workspace-scoped Owner must not see Tenant scope option on RoleFederationPage.");

            // SampleHost /dev/login rejects open-redirect returnUrl payloads containing backslashes or control characters
            using var anonClient = app.GetTestServer().CreateClient();
            foreach (var evilReturn in new[] { "/\\evil.com", "/support\\evil", "/support\r\nX-Injected: 1", "/support\tfoo", "/support\x7f" })
            {
                using var loginResp = await anonClient.GetAsync($"/dev/login?user=admin&returnUrl={Uri.EscapeDataString(evilReturn)}");
                Assert.AreEqual(HttpStatusCode.Redirect, loginResp.StatusCode);
                Assert.AreEqual("/support", loginResp.Headers.Location?.OriginalString, $"Open redirect must fall back to /support for '{evilReturn}'.");
            }
        }

        // 3b. Host resolver returning wrong-scope principal MUST be rejected BEFORE external claims enrichment
        var wrongScopeServices = new ServiceCollection();
        wrongScopeServices.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddTicketFlowEndpoints(o => o.Partition = partition)
            .AddTicketFlowBlazorUI(o => o.Workspace = workspace);
        wrongScopeServices.AddScoped<ITicketFlowPrincipalResolver, WrongScopeResolver>();

        await using (var provider = wrongScopeServices.BuildServiceProvider())
        using (var serviceScope = provider.CreateScope())
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("sub", "wrong-scope-user"),
                    new System.Security.Claims.Claim("role", "idf:support-agents"),
                    new System.Security.Claims.Claim("idp", "identityflow"),
                ], "IdentityFlow.Cookie")),
            };
            var session = await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(http);
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.NoPrincipal, session.State);
        }

        // 3c. Disabled or out-of-scope service account fails closed to NoPrincipal, custom host resolver enforces VerifyScopeClaims, and duplicate conflicting scope claims fail closed.
        var saSessionServices = new ServiceCollection();
        saSessionServices.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddTicketFlowEndpoints(o => o.Partition = partition)
            .AddTicketFlowBlazorUI(o => o.Workspace = workspace);
        saSessionServices.AddScoped<ITicketFlowPrincipalResolver, FlexibleHostResolver>();

        await using (var provider = saSessionServices.BuildServiceProvider())
        {
            var saStore = provider.GetRequiredService<IServiceAccountStore>();
            var disabledClient = new ApiClientId("sa-disabled-ui");
            await saStore.CreateAsync(new ServiceAccount(disabledClient, partition, new WorkspaceGrantScope(workspace), "Disabled UI SA"), "system:test");
            await saStore.SetEnabledAsync(partition, disabledClient, isEnabled: false, "system:test", "revoked");

            var otherWsClient = new ApiClientId("sa-other-ws-ui");
            await saStore.CreateAsync(new ServiceAccount(otherWsClient, partition, new WorkspaceGrantScope(new WorkspaceId("ws-other")), "Other WS UI SA"), "system:test");

            var enabledClient = new ApiClientId("sa-enabled-ui");
            await saStore.CreateAsync(new ServiceAccount(enabledClient, partition, new WorkspaceGrantScope(workspace), "Enabled UI SA"), "system:test");

            using var serviceScope = provider.CreateScope();

            // Disabled service account -> NoPrincipal
            var disabledHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("client_id", disabledClient.Value),
                ], "ExternalIdentity")),
            };
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.NoPrincipal,
                (await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(disabledHttp)).State);

            // Out-of-scope service account -> NoPrincipal
            var otherWsHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("client_id", otherWsClient.Value),
                ], "ExternalIdentity")),
            };
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.NoPrincipal,
                (await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(otherWsHttp)).State);

            // Enabled in-scope service account -> Ready
            var enabledHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("client_id", enabledClient.Value),
                ], "ExternalIdentity")),
            };
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.Ready,
                (await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(enabledHttp)).State);

            // Custom host resolver MUST still fail closed when scope claims conflict with resolvedScope!
            var mismatchedClaimHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("sub", "staff-ada"),
                    new System.Security.Claims.Claim("workspace_id", "ws-other"),
                ], "ExternalIdentity")),
            };
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.NoPrincipal,
                (await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(mismatchedClaimHttp)).State);
        }

        // Duplicate conflicting scope claims in PartitionFromClaims (no ITicketFlowPartitionResolver) -> NoPrincipal
        var claimPartitionServices = new ServiceCollection();
        claimPartitionServices.AddTicketFlow()
            .AddAccessControl()
            .AddInMemoryAccessStores()
            .AddTicketFlowBlazorUI(o => o.Workspace = workspace);
        await using (var provider = claimPartitionServices.BuildServiceProvider())
        using (var serviceScope = provider.CreateScope())
        {
            var conflictingHttp = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = serviceScope.ServiceProvider,
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("sub", "alice"),
                    new System.Security.Claims.Claim("tenant_id", "t1"),
                    new System.Security.Claims.Claim("tenant_id", "t2"),
                    new System.Security.Claims.Claim("project_id", "p1"),
                    new System.Security.Claims.Claim("environment_id", "prod"),
                    new System.Security.Claims.Claim("app_id", "app1"),
                ], "ExternalIdentity")),
            };
            Assert.AreEqual(TicketFlow.UI.Blazor.Infrastructure.UiSessionState.NoPrincipal,
                (await TicketFlow.UI.Blazor.Infrastructure.TicketFlowUISession.ResolvePrincipalAsync(conflictingHttp)).State);
        }

        // 4. SampleSeeder does NOT seed hardcoded demo API keys outside Development
        await using (var prodApp = SampleHostApp.Build(["--environment", "Production"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TicketFlow:Storage"] = "InMemory",
                ["TicketFlow:DevelopmentAuthentication"] = "false",
            });
        }))
        {
            await SampleHostApp.InitializeAsync(prodApp, seed: true);
            var settings = prodApp.Services.GetRequiredService<SampleSettings>();
            var apiKeys = prodApp.Services.GetRequiredService<IApiKeyStore>();
            Assert.IsEmpty(await apiKeys.ListForPartitionAsync(settings.Partition, includeInactive: true),
                "Hardcoded demo API keys must never be seeded outside Development.");
        }
    }

    private sealed class NullReturningResolver : ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PrincipalRef?>(null);
    }

    private sealed class StaffMappedResolver : ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PrincipalRef?>(new UserPrincipal(scope, new UserId("staff-ada"), [new TeamId("tier1")]));
    }

    private sealed class WrongScopeResolver : ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PrincipalRef?>(new UserPrincipal(new TicketScope("other-t", "other-p", "prod", "other-a", new WorkspaceId("other-ws")), new UserId("wrong-scope-user")));
    }

    private sealed class FlexibleHostResolver : ITicketFlowPrincipalResolver
    {
        public ValueTask<PrincipalRef?> ResolveAsync(Microsoft.AspNetCore.Http.HttpContext httpContext, TicketScope scope, CancellationToken cancellationToken = default)
        {
            if (httpContext.User.FindFirst("client_id")?.Value is { Length: > 0 } clientId)
                return ValueTask.FromResult<PrincipalRef?>(new ApiClientPrincipal(scope, new ApiClientId(clientId)));
            if (httpContext.User.FindFirst("sub")?.Value is { Length: > 0 } sub)
                return ValueTask.FromResult<PrincipalRef?>(new UserPrincipal(scope, new UserId(sub)));
            return ValueTask.FromResult<PrincipalRef?>(null);
        }
    }

    private static async Task<WebApplication> StartSampleHostAsync()
    {
        var app = SampleHostApp.Build(["--environment", "Development"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TicketFlow:Storage"] = "InMemory",
                ["TicketFlow:DevelopmentAuthentication"] = "true",
            });
        });
        await SampleHostApp.InitializeAsync(app, seed: true);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateDevClient(WebApplication app, string user)
    {
        var client = app.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", user);
        return client;
    }

    private static async Task<string> GetStringAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<(string Token, string Cookie)> GetAntiforgeryAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        var html = await response.Content.ReadAsStringAsync();
        var token = TokenPattern().Match(html).Groups[1].Value;
        var cookie = response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)).Split(';')[0];
        Assert.IsFalse(string.IsNullOrEmpty(token));
        return (token, cookie);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string cookie, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    private static string ExtractIssuedApiKey(string html)
    {
        var match = IssuedKeyPattern().Match(html);
        Assert.IsTrue(match.Success, "Expected data-issued-api-key-value in response HTML.");
        return WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
    }

    private static string ExtractServiceAccountCard(string html, string clientId)
    {
        var marker = $"data-service-account=\"{clientId}\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"Missing service account card '{clientId}'.");
        var next = html.IndexOf("data-service-account=\"", start + marker.Length, StringComparison.Ordinal);
        return next > start ? html[start..next] : html[start..];
    }

    private static string ExtractFederationMappingRow(string html, string externalRole)
    {
        var idx = html.IndexOf(externalRole, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, idx, $"Missing federation mapping '{externalRole}'.");
        var start = html.LastIndexOf("<tr ", idx, StringComparison.Ordinal);
        var end = html.IndexOf("</tr>", idx, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start);
        Assert.IsGreaterThan(start, end);
        return html[start..(end + 5)];
    }

    private static string ExtractFederationMappingId(string rowHtml)
    {
        var match = FederationMappingIdPattern().Match(rowHtml);
        Assert.IsTrue(match.Success, "Missing data-federation-mapping attribute.");
        return match.Groups[1].Value;
    }

    private static string ExtractRevision(string fragment)
    {
        var match = RevisionPattern().Match(fragment);
        Assert.IsTrue(match.Success, "Missing _revision hidden input.");
        return match.Groups[1].Value;
    }

    private static string ExtractFirstKeyId(string cardHtml)
    {
        var match = KeyIdPattern().Match(cardHtml);
        Assert.IsTrue(match.Success, "Missing keyId hidden input in service account card.");
        return match.Groups[1].Value;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("name=\"_revision\" value=\"(?<v>\\d+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RevisionPattern();

    [GeneratedRegex("data-issued-api-key-value[^>]*>(?<v>[^<]+)<", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IssuedKeyPattern();

    [GeneratedRegex("name=\"keyId\" value=\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("data-federation-mapping=\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FederationMappingIdPattern();
}
