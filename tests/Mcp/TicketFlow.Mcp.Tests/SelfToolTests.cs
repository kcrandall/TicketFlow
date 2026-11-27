using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Mcp.Tests;

[TestClass]
public sealed class SelfToolTests
{
    [TestMethod]
    public async Task WhoAmIDescribesTheCallerWithoutProbingSensitivePermissionsByDefault()
    {
        await using var host = await McpTestHost.CreateAsync();
        var auditBefore = await AuditCountAsync(host);
        var (_, me) = await host.CallAsync("whoami", user: null, apiClient: "assistant");
        Assert.AreEqual("apiClient", me.GetProperty("principal").GetProperty("kind").GetString());
        Assert.AreEqual("assistant", me.GetProperty("principal").GetProperty("id").GetString());
        Assert.AreEqual("workspace", me.GetProperty("scope").GetProperty("workspace").GetString());
        Assert.AreEqual("agent", me.GetProperty("roles")[0].GetString());
        var permissions = Strings(me.GetProperty("workspacePermissions"));
        CollectionAssert.Contains(permissions, "conversation:reply");
        CollectionAssert.DoesNotContain(permissions, "conversation:delete");
        CollectionAssert.Contains(Strings(me.GetProperty("notProbed")), "contact:reveal", "Sensitive permissions are not probed by default.");
        Assert.AreEqual(auditBefore, await AuditCountAsync(host), "whoami records no audit events unless sensitive probing is requested.");

        var (_, sensitive) = await host.CallAsync("whoami", new() { ["includeSensitive"] = true }, user: "owner");
        CollectionAssert.Contains(Strings(sensitive.GetProperty("workspacePermissions")), "conversation:delete");
        Assert.IsGreaterThan(auditBefore, await AuditCountAsync(host), "Sensitive probes are audited by Access.");
    }

    [TestMethod]
    public async Task DescribeToolsListsEveryToolIncludingDisabledOnesWithCallerVerdicts()
    {
        await using var host = await McpTestHost.CreateAsync(options => options.Toolsets = TicketFlowToolsets.Conversations);
        var (_, described) = await host.CallAsync("describe_tools", user: "light");
        var tools = described.GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        CollectionAssert.AreEquivalent(TicketFlowToolCatalog.Tools.Select(tool => tool.Name).ToList(), tools.Keys.ToList());
        Assert.IsTrue(tools["conversations_add_note"].GetProperty("callerHasPermissions").GetBoolean());
        Assert.IsFalse(tools["conversations_reply"].GetProperty("callerHasPermissions").GetBoolean());
        Assert.IsTrue(tools["conversations_reply"].GetProperty("enabledOnServer").GetBoolean());
        Assert.IsFalse(tools["contacts_search"].GetProperty("enabledOnServer").GetBoolean(), "The contacts toolset is not plugged in on this server.");
        CollectionAssert.Contains(Strings(tools["conversations_reply"].GetProperty("grantedByBuiltInRoles")), "agent");
        Assert.AreEqual("conversations", tools["conversations_reply"].GetProperty("toolset").GetString());

        var (_, toolsets) = await host.CallAsync("list_toolsets");
        var conversations = toolsets.GetProperty("toolsets").EnumerateArray().Single(item => string.Equals(item.GetProperty("name").GetString(), "conversations", StringComparison.Ordinal));
        Assert.IsTrue(conversations.GetProperty("enabled").GetBoolean());
        var assist = toolsets.GetProperty("toolsets").EnumerateArray().Single(item => string.Equals(item.GetProperty("name").GetString(), "assist", StringComparison.Ordinal));
        Assert.IsFalse(assist.GetProperty("registered").GetBoolean());
        Assert.AreEqual("contacts_search", TicketFlowToolCatalog.Find("contacts_search")!.Name);
        Assert.AreEqual("toolset-unavailable", McpTestHost.Code((await host.CallAsync("contacts_search")).Content), "Unregistered toolsets are not callable.");
    }

    [TestMethod]
    public async Task ExplainAccessGivesReasonsAndRolesWithoutLeakingResources()
    {
        await using var host = await McpTestHost.CreateAsync();
        var (_, reply) = await host.CallAsync("explain_access", new() { ["tool"] = "conversations_reply" }, user: "viewer");
        Assert.IsFalse(reply.GetProperty("allowed").GetBoolean());
        var decision = reply.GetProperty("permissions")[0];
        Assert.AreEqual("denied", decision.GetProperty("decision").GetString());
        Assert.AreEqual("noMatchingGrant", decision.GetProperty("reason").GetString());
        CollectionAssert.Contains(Strings(decision.GetProperty("grantedByBuiltInRoles")), "agent");

        var (_, masked) = await host.CallAsync("explain_access", new() { ["permission"] = "contact:read", ["contactId"] = host.Contact.Value }, user: "agent");
        Assert.AreEqual("masked", masked.GetProperty("permissions")[0].GetProperty("decision").GetString());
        Assert.AreEqual("contact.email", masked.GetProperty("permissions")[0].GetProperty("maskedFields")[0].GetProperty("field").GetString());
        Assert.DoesNotContain("grace", masked.GetRawText(), StringComparison.Ordinal);

        var (hidden, notVisible) = await host.CallAsync("explain_access", new() { ["permission"] = "conversation:read", ["conversationId"] = host.Conversation.Value }, user: "nobody");
        Assert.IsTrue(hidden);
        Assert.AreEqual("not-found", McpTestHost.Code(notVisible), "Resources the caller cannot read are reported as not found.");

        var (_, unknown) = await host.CallAsync("explain_access", new() { ["permission"] = "events:read" }, user: "owner");
        Assert.AreEqual("unknownPermission", unknown.GetProperty("permissions")[0].GetProperty("reason").GetString(), "the events feature is not installed here.");
        Assert.AreEqual("invalid", McpTestHost.Code((await host.CallAsync("explain_access", new() { ["permission"] = "a:b", ["tool"] = "whoami" })).Content));
    }

    [TestMethod]
    public async Task ToolListIsMarkedOrFilteredByTheCallersPermissions()
    {
        await using (var showAll = await McpTestHost.CreateAsync())
        {
            showAll.Caller.User = "light";
            var tools = (await showAll.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);
            Assert.IsFalse(tools["conversations_reply"].ProtocolTool.Meta!["ticketflow/permitted"]!.GetValue<bool>());
            Assert.IsTrue(tools["conversations_add_note"].ProtocolTool.Meta!["ticketflow/permitted"]!.GetValue<bool>());
            Assert.IsFalse(tools["conversations_delete"].ProtocolTool.Meta!.ContainsKey("ticketflow/permitted"), "Sensitive permissions are not probed for tools/list by default.");
            Assert.IsTrue(tools.ContainsKey("whoami"));
        }
        await using var hide = await McpTestHost.CreateAsync(options => options.ToolList = TicketFlowToolListMode.HideUnpermitted);
        hide.Caller.User = "light";
        var visible = (await hide.Client.ListToolsAsync()).Select(tool => tool.Name).ToList();
        CollectionAssert.DoesNotContain(visible, "conversations_reply");
        CollectionAssert.Contains(visible, "conversations_add_note");
        CollectionAssert.DoesNotContain(visible, "conversations_delete", "Unprobed sensitive tools fail closed under HideUnpermitted.");
        CollectionAssert.Contains(visible, "describe_tools");

        // Unresolvable caller (decisions is null) fails closed for all permission-gated tools under HideUnpermitted.
        hide.Caller.User = null;
        hide.Caller.ApiClient = null;
        var anonVisible = (await hide.Client.ListToolsAsync()).Select(tool => tool.Name).ToList();
        CollectionAssert.DoesNotContain(anonVisible, "conversations_add_note");
        CollectionAssert.DoesNotContain(anonVisible, "conversations_reply");
        CollectionAssert.DoesNotContain(anonVisible, "conversations_delete");
        CollectionAssert.Contains(anonVisible, "whoami");
        CollectionAssert.Contains(anonVisible, "describe_tools");
    }

    [TestMethod]
    public async Task TeamScopedCallersSeeThePermissionsTheyHoldOnTheirTeam()
    {
        await using (var host = await McpTestHost.CreateAsync(options => options.ToolList = TicketFlowToolListMode.HideUnpermitted))
        {
            var (_, me) = await host.CallAsync("whoami", user: "teamagent");
            CollectionAssert.Contains(Strings(me.GetProperty("heldPermissions")), "conversation:reply", "A team-scoped Agent grant holds conversation:reply on its team.");
            CollectionAssert.DoesNotContain(Strings(me.GetProperty("workspacePermissions")), "conversation:reply", "It does not hold it workspace-wide.");

            var (_, described) = await host.CallAsync("describe_tools", new() { ["tool"] = "conversations_reply" }, user: "teamagent");
            Assert.IsTrue(described.GetProperty("tools")[0].GetProperty("callerHasPermissions").GetBoolean());

            host.Caller.User = "teamagent";
            var visible = (await host.Client.ListToolsAsync()).Select(tool => tool.Name).ToList();
            CollectionAssert.Contains(visible, "conversations_reply", "HideUnpermitted keeps tools usable under team/inbox grants.");
            CollectionAssert.DoesNotContain(visible, "conversations_start", "Agents do not hold conversation:create anywhere.");

            Assert.IsFalse((await host.CallAsync("conversations_reply", new() { ["conversationId"] = host.TeamConversation.Value, ["body"] = "On it" }, user: "teamagent")).IsError);
            Assert.AreEqual("forbidden", McpTestHost.Code((await host.CallAsync("conversations_reply", new() { ["conversationId"] = host.Conversation.Value, ["body"] = "x" },
                user: "teamagent")).Content), "Outside the team the service still refuses.");
        }
    }

    [TestMethod]
    public async Task WhoAmIReportsIdentityProviderExternalRolesAndFederatedGrantsAndRespectsFederatedOnly()
    {
        await using (var merged = await McpTestHost.CreateAsync(configureAccess: access =>
        {
            access.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
            access.RoleFederation.StaticMappings["ext-viewer"] = BuiltInRoles.Viewer.Id;
        }))
        {
            await using (var scope = merged.Services.CreateAsyncScope())
            {
                var mappings = scope.ServiceProvider.GetRequiredService<IRoleFederationMappingStore>();
                await mappings.UpsertAsync(
                    new RoleFederationMappingId("okta-agent"),
                    new RoleFederationMapping("okta", "support-agents", BuiltInRoles.Agent.Id, AccessPartition.From(merged.Scope), new WorkspaceGrantScope(merged.Scope.WorkspaceId)),
                    isEnabled: true,
                    "seed");
            }

            // Matching provider activates dynamic mapping + static mapping
            var (_, me) = await merged.CallAsync("whoami", user: "nobody", externalRoles: ["support-agents", "ext-viewer"], identityProvider: "okta");
            var principal = me.GetProperty("principal");
            Assert.AreEqual("okta", principal.GetProperty("identityProvider").GetString());
            CollectionAssert.AreEquivalent(new[] { "ext-viewer", "support-agents" }, Strings(principal.GetProperty("externalRoles")));
            var roles = Strings(me.GetProperty("roles"));
            CollectionAssert.Contains(roles, "agent");
            CollectionAssert.Contains(roles, "viewer");
            var grants = me.GetProperty("grants").EnumerateArray().ToList();
            Assert.IsTrue(grants.Any(g => string.Equals(g.GetProperty("role").GetString(), "agent", StringComparison.Ordinal)
                && string.Equals(g.GetProperty("kind").GetString(), "federated", StringComparison.Ordinal)));
            CollectionAssert.Contains(Strings(me.GetProperty("workspacePermissions")), "conversation:reply");

            // Wrong provider ignores okta-pinned dynamic mapping, keeps static mapping
            var (_, wrongProvider) = await merged.CallAsync("whoami", user: "nobody", externalRoles: ["support-agents", "ext-viewer"], identityProvider: "azuread");
            var wrongRoles = Strings(wrongProvider.GetProperty("roles"));
            CollectionAssert.DoesNotContain(wrongRoles, "agent", "Okta-pinned mapping must not apply to azuread callers.");
            CollectionAssert.Contains(wrongRoles, "viewer");
            CollectionAssert.DoesNotContain(Strings(wrongProvider.GetProperty("workspacePermissions")), "conversation:reply");
        }

        // FederatedOnly mode must NOT report ignored local non-admin allow grants as active, while still showing federated roles and preserved local admin grants.
        await using (var fedOnly = await McpTestHost.CreateAsync(features: false, configureAccess: access =>
        {
            access.RoleFederation.Mode = RoleFederationMode.FederatedOnly;
            access.RoleFederation.StaticMappings["idp-viewer"] = BuiltInRoles.Viewer.Id;
        }))
        {
            // "agent" has a local Agent allow grant, which FederatedOnly ignores; external role grants Viewer.
            var (_, agentFed) = await fedOnly.CallAsync("whoami", user: "agent", externalRoles: ["idp-viewer"], identityProvider: "okta");
            var agentRoles = Strings(agentFed.GetProperty("roles"));
            CollectionAssert.Contains(agentRoles, "viewer");
            CollectionAssert.DoesNotContain(agentRoles, "agent", "Ignored local non-admin allow grants must not appear as active in FederatedOnly mode.");
            CollectionAssert.DoesNotContain(Strings(agentFed.GetProperty("workspacePermissions")), "conversation:reply");
            CollectionAssert.Contains(Strings(agentFed.GetProperty("workspacePermissions")), "conversation:read");

            // "owner" has a local Owner grant; with AllowAdministrativeRoles = false, local admin grant stays preserved for admin permissions.
            var (_, ownerFed) = await fedOnly.CallAsync("whoami", new() { ["includeSensitive"] = true }, user: "owner", externalRoles: ["idp-viewer"], identityProvider: "okta");
            var ownerRoles = Strings(ownerFed.GetProperty("roles"));
            CollectionAssert.Contains(ownerRoles, "owner");
            CollectionAssert.Contains(ownerRoles, "viewer");
            CollectionAssert.Contains(Strings(ownerFed.GetProperty("workspacePermissions")), "access:manage");
        }
    }

    [TestMethod]
    public async Task ToolListHideUnpermittedRespectsAnyOfForBreakGlassAndTenantAdmin()
    {
        await using var host = await McpTestHost.CreateAsync(options =>
        {
            options.ToolList = TicketFlowToolListMode.HideUnpermitted;
            options.ProbeSensitivePermissions = true;
        });

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<IAccessRoleStore>();
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            await roles.CreateAsync(host.Scope, new AccessRole(new RoleId("oncall"), "On-Call", [AccessPermissions.AccessBreakGlass]));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-oncall"), AccessPartition.From(host.Scope), new TicketFlow.Core.UserId("oncall-user"),
                new RoleId("oncall"), new WorkspaceGrantScope(host.Scope.WorkspaceId)));
            // Tenant-scoped Owner + workspace deny of AccessManage & AccessBreakGlass leaves ONLY AccessManageTenant active.
            await roles.CreateAsync(host.Scope, new AccessRole(new RoleId("deny-ws-admin"), "Deny WS Admin", [AccessPermissions.AccessManage, AccessPermissions.AccessBreakGlass]));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-tenant-only"), AccessPartition.From(host.Scope), new TicketFlow.Core.UserId("tenant-only"),
                BuiltInRoles.Owner.Id, TenantGrantScope.Instance));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-tenant-only-deny"), AccessPartition.From(host.Scope), new TicketFlow.Core.UserId("tenant-only"),
                new RoleId("deny-ws-admin"), new WorkspaceGrantScope(host.Scope.WorkspaceId), GrantEffect.Deny));

            // Workspace-scoped role with AccessManageTenant must NOT satisfy tenant-only tools.
            await roles.CreateAsync(host.Scope, new AccessRole(new RoleId("ws-tenant-ops"), "WS Tenant Ops", [AccessPermissions.AccessManageTenant]));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-ws-tenant-ops"), AccessPartition.From(host.Scope), new TicketFlow.Core.UserId("ws-tenant-fake"),
                new RoleId("ws-tenant-ops"), new WorkspaceGrantScope(host.Scope.WorkspaceId)));
        }

        host.Caller.User = "oncall-user";
        var onCallTools = (await host.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        CollectionAssert.Contains(onCallTools, "access_activate_break_glass");
        CollectionAssert.Contains(onCallTools, "access_list_break_glass");
        CollectionAssert.DoesNotContain(onCallTools, "access_list_roles");
        CollectionAssert.DoesNotContain(onCallTools, "access_list_service_accounts");

        host.Caller.User = "tenant-only";
        var tenantTools = (await host.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        CollectionAssert.Contains(tenantTools, "access_list_grants");
        CollectionAssert.Contains(tenantTools, "access_create_grant");
        CollectionAssert.Contains(tenantTools, "access_list_service_accounts");
        CollectionAssert.Contains(tenantTools, "access_list_federation_mappings");
        CollectionAssert.DoesNotContain(tenantTools, "access_list_roles");
        CollectionAssert.DoesNotContain(tenantTools, "access_activate_break_glass");

        host.Caller.User = "ws-tenant-fake";
        var fakeTenantTools = (await host.Client.ListToolsAsync()).Select(t => t.Name).ToList();
        CollectionAssert.DoesNotContain(fakeTenantTools, "access_list_grants");
        CollectionAssert.DoesNotContain(fakeTenantTools, "access_list_service_accounts");
    }

    [TestMethod]
    public async Task WhoAmIFiltersExpiredAndFutureLocalGrantsAndExcludesDenyFromRoles()
    {
        await using var host = await McpTestHost.CreateAsync(features: false);
        var now = DateTimeOffset.UtcNow;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            var partition = AccessPartition.From(host.Scope);
            var ws = new WorkspaceGrantScope(host.Scope.WorkspaceId);
            var subject = new TicketFlow.Core.UserId("windowed");
            await grants.CreateAsync(new AccessGrant(new GrantId("g-expired"), partition, subject, BuiltInRoles.Supervisor.Id, ws, validUntil: now.AddMinutes(-5)));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-future"), partition, subject, BuiltInRoles.Admin.Id, ws, validFrom: now.AddMinutes(30)));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-active"), partition, subject, BuiltInRoles.Viewer.Id, ws, validFrom: now.AddMinutes(-5), validUntil: now.AddHours(1)));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-deny"), partition, subject, BuiltInRoles.Agent.Id, ws, GrantEffect.Deny));
        }

        var (_, me) = await host.CallAsync("whoami", user: "windowed");
        var grantRoles = me.GetProperty("grants").EnumerateArray().Select(g => g.GetProperty("role").GetString()!).ToList();
        CollectionAssert.Contains(grantRoles, "viewer");
        CollectionAssert.DoesNotContain(grantRoles, "supervisor", "Expired local grant must be filtered out.");
        CollectionAssert.DoesNotContain(grantRoles, "admin", "Future local grant must be filtered out.");

        var activeRoles = Strings(me.GetProperty("roles"));
        CollectionAssert.AreEquivalent(new[] { "viewer" }, activeRoles, "Only active allow grants should appear in roles.");
    }

    [TestMethod]
    public async Task CoreMcpEnforcesScopeClaimsAndServiceAccountStatusEvenWithCustomResolver()
    {
        await using var host = await McpTestHost.CreateAsync(options => options.ToolList = TicketFlowToolListMode.HideUnpermitted);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var saStore = scope.ServiceProvider.GetRequiredService<IServiceAccountStore>();
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            var partition = AccessPartition.From(host.Scope);

            var activeId = new ApiClientId("sa-active");
            await saStore.CreateAsync(new ServiceAccount(activeId, partition, new WorkspaceGrantScope(host.Scope.WorkspaceId), "Active SA"), "seed");
            await grants.CreateAsync(new AccessGrant(new GrantId("g-sa-active"), partition, activeId, BuiltInRoles.Agent.Id, new WorkspaceGrantScope(host.Scope.WorkspaceId)));

            var disabledId = new ApiClientId("sa-disabled");
            await saStore.CreateAsync(new ServiceAccount(disabledId, partition, new WorkspaceGrantScope(host.Scope.WorkspaceId), "Disabled SA"), "seed");
            await saStore.SetEnabledAsync(partition, disabledId, isEnabled: false, actorPrincipalId: "seed", expectedVersion: 1);
            await grants.CreateAsync(new AccessGrant(new GrantId("g-sa-disabled"), partition, disabledId, BuiltInRoles.Owner.Id, new WorkspaceGrantScope(host.Scope.WorkspaceId)));

            var otherWsId = new ApiClientId("sa-other-ws");
            await saStore.CreateAsync(new ServiceAccount(otherWsId, partition, new WorkspaceGrantScope(new WorkspaceId("other-ws")), "Other WS SA"), "seed");
            await grants.CreateAsync(new AccessGrant(new GrantId("g-sa-other-ws"), partition, otherWsId, BuiltInRoles.Owner.Id, TenantGrantScope.Instance));
        }

        // Active in-scope service account succeeds on whoami, tools, and HideUnpermitted tools/list.
        var (activeErr, activeMe) = await host.CallAsync("whoami", user: null, apiClient: "sa-active");
        Assert.IsFalse(activeErr, activeMe.GetRawText());
        Assert.AreEqual("sa-active", activeMe.GetProperty("principal").GetProperty("id").GetString());
        host.Caller.User = null;
        host.Caller.ApiClient = "sa-active";
        CollectionAssert.Contains((await host.Client.ListToolsAsync()).Select(t => t.Name).ToList(), "conversations_reply");

        // Disabled and out-of-scope service accounts are rejected even with custom ResolvePrincipal (including on whoami and tools/list).
        foreach (var rejectedClient in new[] { "sa-disabled", "sa-other-ws" })
        {
            var (whoErr, whoBody) = await host.CallAsync("whoami", user: null, apiClient: rejectedClient);
            Assert.IsTrue(whoErr);
            Assert.AreEqual("unauthenticated", McpTestHost.Code(whoBody));

            var (listErr, listBody) = await host.CallAsync("conversations_list", user: null, apiClient: rejectedClient);
            Assert.IsTrue(listErr);
            Assert.AreEqual("unauthenticated", McpTestHost.Code(listBody));

            host.Caller.User = null;
            host.Caller.ApiClient = rejectedClient;
            var visible = (await host.Client.ListToolsAsync()).Select(t => t.Name).ToList();
            CollectionAssert.DoesNotContain(visible, "conversations_reply");
            CollectionAssert.DoesNotContain(visible, "conversations_list");
        }

        // Scope-claim pinning: a ClaimsPrincipal pinned to another workspace or tenant cannot cross boundaries via TicketFlowToolContext.
        host.Caller.User = "owner";
        host.Caller.ApiClient = null;
        var descriptor = TicketFlowToolCatalog.Find("whoami")!;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var wrongWorkspaceUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("workspace_id", "other-ws")], "test"));
            var wrongWorkspaceCtx = new TicketFlowToolContext(scope.ServiceProvider, wrongWorkspaceUser, descriptor);
            Assert.IsNull(await wrongWorkspaceCtx.ResolvePrincipalAsync(host.Scope.WorkspaceId, CancellationToken.None));
            var wrongWsResult = await wrongWorkspaceCtx.RunAsync(host.Scope.WorkspaceId.Value, _ => Task.FromResult<object>(new { ok = true }), CancellationToken.None);
            Assert.IsTrue(wrongWsResult.IsError);
            Assert.AreEqual("unauthenticated", McpTestHost.Code(wrongWsResult.StructuredContent!.Value));

            var wrongTenantUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "other-tenant")], "test"));
            var wrongTenantCtx = new TicketFlowToolContext(scope.ServiceProvider, wrongTenantUser, descriptor);
            Assert.IsNull(await wrongTenantCtx.ResolvePrincipalAsync(host.Scope.WorkspaceId, CancellationToken.None));

            var matchingUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", host.Scope.TenantId), new Claim("workspace_id", host.Scope.WorkspaceId.Value)], "test"));
            var matchingCtx = new TicketFlowToolContext(scope.ServiceProvider, matchingUser, descriptor);
            Assert.IsNotNull(await matchingCtx.ResolvePrincipalAsync(host.Scope.WorkspaceId, CancellationToken.None));
        }
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    private static async Task<int> AuditCountAsync(McpTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var sink = scope.ServiceProvider.GetRequiredService<IAccessAuditSink>();
        return sink is InMemoryAccessAuditSink memory ? memory.Snapshot().Count : 0;
    }
}
