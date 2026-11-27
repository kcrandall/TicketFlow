using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using static TicketFlow.Hosting.AspNetCore.Tests.EndpointHost;

namespace TicketFlow.Hosting.AspNetCore.Tests;

[TestClass]
public sealed class FoundationTests
{
    [TestMethod]
    public async Task RequestsWithoutAPrincipalAreUnauthorizedProblems()
    {
        await using var host = await CreateAsync();
        var response = await host.Client(user: null).GetAsync($"{Api}/inboxes");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await JsonAsync(response);
        Assert.AreEqual("unauthenticated", problem.GetProperty("code").GetString());
        Assert.AreEqual("urn:ticketflow:problem:unauthenticated", problem.GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task InvalidWorkspaceRouteValuesAreBadRequests()
    {
        await using var host = await CreateAsync();
        var response = await host.Client().GetAsync("/ticketflow/api/workspaces/%01bad/inboxes");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalid-workspace", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task PrincipalsWithoutGrantsAreForbidden()
    {
        await using var host = await CreateAsync();
        var response = await host.Client("stranger").GetAsync($"{Api}/inboxes/support");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual("forbidden", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task RevisionFencingUsesETagsAndIfMatch()
    {
        await using var host = await CreateAsync();
        var client = host.Client();
        var get = await client.GetAsync($"{Api}/inboxes/support");
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.AreEqual("\"1\"", get.Headers.ETag?.Tag);

        var body = new { name = "Support desk", channel = new { kind = "email", configurationId = "cfg-support" } };
        var missing = await client.SendAsync(Request(HttpMethod.Put, $"{Api}/inboxes/support", body));
        Assert.AreEqual(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        var updated = await client.SendAsync(Request(HttpMethod.Put, $"{Api}/inboxes/support", body, ifMatch: 1));
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        Assert.AreEqual(2, Revision(updated));
        Assert.AreEqual("Support desk", (await JsonAsync(updated)).GetProperty("name").GetString());

        var stale = await client.SendAsync(Request(HttpMethod.Put, $"{Api}/inboxes/support", body, ifMatch: 1));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = await JsonAsync(stale);
        Assert.AreEqual("revision-mismatch", problem.GetProperty("code").GetString());
        Assert.AreEqual(2, problem.GetProperty("currentRevision").GetInt64());
        Assert.AreEqual("\"2\"", stale.Headers.ETag?.Tag);

        var malformed = new HttpRequestMessage(HttpMethod.Delete, $"{Api}/inboxes/support");
        malformed.Headers.TryAddWithoutValidation("If-Match", "\"two\"");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.SendAsync(malformed)).StatusCode);

        var weak = new HttpRequestMessage(HttpMethod.Put, $"{Api}/inboxes/support") { Content = JsonContent.Create(body) };
        weak.Headers.TryAddWithoutValidation("If-Match", "W/\"2\"");
        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(weak)).StatusCode, "Weak tags naming the revision are accepted.");
    }

    [TestMethod]
    public async Task CreatingAnExistingRecordIsAConflictAndMissingRecordsAreNotFound()
    {
        await using var host = await CreateAsync();
        var client = host.Client();
        var duplicate = await client.SendAsync(Request(HttpMethod.Post, $"{Api}/inboxes", new { id = "support", name = "Again", channel = new { kind = "sms", configurationId = "cfg-sms" } }));
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.AreEqual(1, (await JsonAsync(duplicate)).GetProperty("currentRevision").GetInt64());
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{Api}/inboxes/missing")).StatusCode);
    }

    [TestMethod]
    public async Task DomainValidationFailuresAreUnprocessableWithoutEchoingInput()
    {
        await using var host = await CreateAsync();
        var response = await host.Client().SendAsync(Request(HttpMethod.Post, $"{Api}/inboxes",
            new { name = "Bad", channel = new { kind = "NOT A KIND secret@example.com", configurationId = "cfg" } }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret@example.com", text, StringComparison.Ordinal);
        Assert.AreEqual("invalid", (await JsonAsync(response)).GetProperty("code").GetString());

        var badEnum = await host.Client().GetAsync($"{Api}/conversations?status=1");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, badEnum.StatusCode);
    }

    [TestMethod]
    public async Task OversizedBodiesAreRejected()
    {
        await using var host = await CreateAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/inboxes")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { name = new string('x', 70_000), channel = new { kind = "sms", configurationId = "c" } }))
            {
                Headers = { ContentType = new("application/json") },
            },
        };
        var response = await host.Client().SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [TestMethod]
    public async Task OpenApiDocumentDescribesTicketFlowEndpoints()
    {
        await using var host = await CreateAsync();
        var response = await host.Client(user: null).GetAsync("/ticketflow/openapi/ticketflow.json");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = await JsonAsync(response);
        var paths = document.GetProperty("paths");
        Assert.IsTrue(paths.TryGetProperty("/ticketflow/api/workspaces/{workspaceId}/conversations", out _));
        Assert.IsTrue(paths.TryGetProperty("/ticketflow/public/workspaces/{workspaceId}/surveys/respond", out _));
        Assert.IsTrue(document.GetProperty("components").GetProperty("schemas").TryGetProperty("ConversationResponse", out _));
        var listConversations = paths.GetProperty("/ticketflow/api/workspaces/{workspaceId}/conversations").GetProperty("get");
        Assert.AreEqual("ListConversations", listConversations.GetProperty("operationId").GetString());
        Assert.IsFalse(listConversations.GetRawText().Contains("TicketFlowActor", StringComparison.Ordinal), "Bound actors are not API parameters.");
    }

    [TestMethod]
    public async Task MeReportsWorkspacePermissions()
    {
        await using var host = await CreateAsync();
        var owner = await JsonAsync(await host.Client().GetAsync($"{Api}/me"));
        Assert.AreEqual("user", owner.GetProperty("kind").GetString());
        var permissions = owner.GetProperty("workspacePermissions").EnumerateArray().Select(item => item.GetString()).ToList();
        CollectionAssert.Contains(permissions, "conversation:read");
        CollectionAssert.DoesNotContain(permissions, "access:manage", "Sensitive permissions are probed only on request (audited).");
        var sensitive = await JsonAsync(await host.Client().GetAsync($"{Api}/me?includeSensitive=true"));
        var sensitivePerms = sensitive.GetProperty("workspacePermissions").EnumerateArray().Select(item => item.GetString()).ToList();
        CollectionAssert.Contains(sensitivePerms, "access:manage");
        CollectionAssert.DoesNotContain(sensitivePerms, "access:manage-tenant", "Workspace-only Owner must not report access:manage-tenant.");

        using (var scope = host.App.Services.CreateScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<TicketFlow.Access.IAccessGrantStore>();
            await grants.CreateAsync(new TicketFlow.Access.AccessGrant(
                new TicketFlow.Access.GrantId("seed-tenant-owner"),
                TicketFlow.Access.AccessPartition.From(host.Scope),
                new TicketFlow.Core.UserId("tenant-owner"),
                TicketFlow.Access.BuiltInRoles.Owner.Id,
                TicketFlow.Access.TenantGrantScope.Instance));
        }
        var tenantOwner = await JsonAsync(await host.Client("tenant-owner").GetAsync($"{Api}/me?includeSensitive=true"));
        CollectionAssert.Contains(tenantOwner.GetProperty("workspacePermissions").EnumerateArray().Select(item => item.GetString()).ToList(), "access:manage-tenant");

        var agent = await JsonAsync(await host.Client("agent").GetAsync($"{Api}/me?includeSensitive=true"));
        CollectionAssert.DoesNotContain(agent.GetProperty("workspacePermissions").EnumerateArray().Select(item => item.GetString()).ToList(), "access:manage");
        var stranger = await JsonAsync(await host.Client("stranger").GetAsync($"{Api}/me"));
        Assert.AreEqual(0, stranger.GetProperty("workspacePermissions").GetArrayLength());
    }

    [TestMethod]
    public async Task PostCsrfGuardInspectsOnlyMediaTypeEssence()
    {
        await using var host = await CreateAsync();
        var client = host.Client();
        var endpoint = $"{Api}/routing/agents/me/heartbeat";

        foreach (var spoofed in new[]
        {
            "text/plain; foo=+json",
            "application/x-www-form-urlencoded; x=+json",
            "multipart/form-data; boundary=+json",
            "application/jsonfoo",
            "application/+json",
            "text/vnd.custom+json",
        })
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent("{}"),
            };
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", spoofed);
            var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"Expected 400 for spoofed Content-Type '{spoofed}'.");
            Assert.AreEqual("json-or-header-required", (await JsonAsync(response)).GetProperty("code").GetString(), $"Expected CSRF rejection for '{spoofed}'.");
        }

        // Valid application/json with parameters passes the CSRF guard
        var validJson = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        Assert.AreNotEqual(HttpStatusCode.BadRequest, (await client.SendAsync(validJson)).StatusCode);

        // Valid application/*+json essence passes the CSRF guard (not rejected as json-or-header-required)
        var validStructured = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/vnd.api+json"),
        };
        Assert.AreNotEqual(HttpStatusCode.BadRequest, (await client.SendAsync(validStructured)).StatusCode);
    }

    [TestMethod]
    public async Task AuthenticatedEndpointsStampNoStoreWhilePublicPortalRemainsCacheable()
    {
        await using var host = await CreateAsync();

        var me = await host.Client().GetAsync($"{Api}/me");
        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
        Assert.IsTrue(me.Headers.CacheControl?.NoStore == true, "Authenticated /me must set Cache-Control: no-store.");
        Assert.IsTrue(me.Headers.Pragma.Any(h => string.Equals(h.Name, "no-cache", StringComparison.OrdinalIgnoreCase)), "Authenticated /me must set Pragma: no-cache.");

        var inbox = await host.Client().GetAsync($"{Api}/inboxes/support");
        Assert.AreEqual(HttpStatusCode.OK, inbox.StatusCode);
        Assert.IsTrue(inbox.Headers.CacheControl?.NoStore == true);
        Assert.IsTrue(inbox.Headers.Pragma.Any(h => string.Equals(h.Name, "no-cache", StringComparison.OrdinalIgnoreCase)));

        var unauth = await host.Client(user: null).GetAsync($"{Api}/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);
        Assert.IsTrue(unauth.Headers.CacheControl?.NoStore == true);
        Assert.IsTrue(unauth.Headers.Pragma.Any(h => string.Equals(h.Name, "no-cache", StringComparison.OrdinalIgnoreCase)));

        var publicPortal = await host.Client(user: null).GetAsync($"{Public}/portals/missing");
        Assert.AreEqual(HttpStatusCode.NotFound, publicPortal.StatusCode);
        Assert.IsFalse(publicPortal.Headers.CacheControl?.NoStore == true, "Public portal group must not force Cache-Control: no-store.");
    }

    [TestMethod]
    public async Task ActorBindingEnforcesScopeClaimsAndServiceAccountStatus()
    {
        await using var host = await CreateAsync(
            configure: options =>
            {
                options.ResolvePrincipal = (http, target) =>
                {
                    if (http.Request.Headers["X-Test-Client"].ToString() is { Length: > 0 } clientId)
                        return ValueTask.FromResult<TicketFlow.Core.PrincipalRef?>(new TicketFlow.Core.ApiClientPrincipal(target, new TicketFlow.Core.ApiClientId(clientId)));
                    return ValueTask.FromResult(http.Request.Headers["X-Test-User"].ToString() is { Length: > 0 } user
                        ? (TicketFlow.Core.PrincipalRef?)new TicketFlow.Core.UserPrincipal(target, new TicketFlow.Core.UserId(user))
                        : null);
                };
            },
            onRequest: http =>
            {
                if (http.Request.Headers["X-Test-Claim-Workspace"].ToString() is { Length: > 0 } claimedWs)
                    http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim("workspace_id", claimedWs)], "test"));
            });

        using (var scope = host.App.Services.CreateScope())
        {
            var saStore = scope.ServiceProvider.GetRequiredService<TicketFlow.Access.IServiceAccountStore>();
            var grants = scope.ServiceProvider.GetRequiredService<TicketFlow.Access.IAccessGrantStore>();
            var partition = TicketFlow.Access.AccessPartition.From(host.Scope);

            var activeId = new TicketFlow.Core.ApiClientId("sa-active");
            await saStore.CreateAsync(new TicketFlow.Access.ServiceAccount(activeId, partition, new TicketFlow.Access.WorkspaceGrantScope(host.Scope.WorkspaceId), "Active"), "seed");
            await grants.CreateAsync(new TicketFlow.Access.AccessGrant(new TicketFlow.Access.GrantId("g-sa-active"), partition, activeId, TicketFlow.Access.BuiltInRoles.Viewer.Id, new TicketFlow.Access.WorkspaceGrantScope(host.Scope.WorkspaceId)));

            var disabledId = new TicketFlow.Core.ApiClientId("sa-disabled");
            await saStore.CreateAsync(new TicketFlow.Access.ServiceAccount(disabledId, partition, new TicketFlow.Access.WorkspaceGrantScope(host.Scope.WorkspaceId), "Disabled"), "seed");
            await saStore.SetEnabledAsync(partition, disabledId, isEnabled: false, actorPrincipalId: "seed", expectedVersion: 1);
            await grants.CreateAsync(new TicketFlow.Access.AccessGrant(new TicketFlow.Access.GrantId("g-sa-disabled"), partition, disabledId, TicketFlow.Access.BuiltInRoles.Owner.Id, new TicketFlow.Access.WorkspaceGrantScope(host.Scope.WorkspaceId)));

            var otherWsId = new TicketFlow.Core.ApiClientId("sa-other-ws");
            await saStore.CreateAsync(new TicketFlow.Access.ServiceAccount(otherWsId, partition, new TicketFlow.Access.WorkspaceGrantScope(new TicketFlow.Core.WorkspaceId("other-ws")), "Other WS"), "seed");
            await grants.CreateAsync(new TicketFlow.Access.AccessGrant(new TicketFlow.Access.GrantId("g-sa-other-ws"), partition, otherWsId, TicketFlow.Access.BuiltInRoles.Owner.Id, TicketFlow.Access.TenantGrantScope.Instance));
        }

        // Mismatched workspace_id claim is rejected even with custom ResolvePrincipal
        var mismatchedReq = new HttpRequestMessage(HttpMethod.Get, $"{Api}/me");
        mismatchedReq.Headers.Add("X-Test-User", "owner");
        mismatchedReq.Headers.Add("X-Test-Claim-Workspace", "other-ws");
        var mismatchedResp = await host.Server.CreateClient().SendAsync(mismatchedReq);
        Assert.AreEqual(HttpStatusCode.Unauthorized, mismatchedResp.StatusCode);
        Assert.AreEqual("scope-mismatch", (await JsonAsync(mismatchedResp)).GetProperty("code").GetString());

        // Matching workspace_id claim succeeds
        var matchingReq = new HttpRequestMessage(HttpMethod.Get, $"{Api}/me");
        matchingReq.Headers.Add("X-Test-User", "owner");
        matchingReq.Headers.Add("X-Test-Claim-Workspace", host.Scope.WorkspaceId.Value);
        Assert.AreEqual(HttpStatusCode.OK, (await host.Server.CreateClient().SendAsync(matchingReq)).StatusCode);

        // Active in-scope service account succeeds on /me
        var activeReq = new HttpRequestMessage(HttpMethod.Get, $"{Api}/me");
        activeReq.Headers.Add("X-Test-Client", "sa-active");
        Assert.AreEqual(HttpStatusCode.OK, (await host.Server.CreateClient().SendAsync(activeReq)).StatusCode);

        // Disabled service account is rejected with 401 on /me and /inboxes
        foreach (var path in new[] { $"{Api}/me", $"{Api}/inboxes" })
        {
            var disabledReq = new HttpRequestMessage(HttpMethod.Get, path);
            disabledReq.Headers.Add("X-Test-Client", "sa-disabled");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Server.CreateClient().SendAsync(disabledReq)).StatusCode);

            var otherWsReq = new HttpRequestMessage(HttpMethod.Get, path);
            otherWsReq.Headers.Add("X-Test-Client", "sa-other-ws");
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Server.CreateClient().SendAsync(otherWsReq)).StatusCode);
        }
    }
}
