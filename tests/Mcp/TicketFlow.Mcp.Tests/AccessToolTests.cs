using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Mcp.Tests;

[TestClass]
public sealed class AccessToolTests
{
    [TestMethod]
    public async Task TenantGrantsRequireTenantAuthorityAndRejectWorkspaceOnlyAdmins()
    {
        await using var host = await McpTestHost.CreateAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            await grants.CreateAsync(new AccessGrant(
                new GrantId("seed-tenant-owner"),
                AccessPartition.From(host.Scope),
                new UserId("tenant-owner"),
                BuiltInRoles.Owner.Id,
                TenantGrantScope.Instance));
        }

        // Workspace-only owner lacks tenant-scoped access:manage-tenant authority
        var (wsCreateFailed, wsCreate) = await host.CallAsync("access_create_grant", new()
        {
            ["subjectKind"] = "user", ["subjectId"] = "agent", ["role"] = "viewer", ["scope"] = "tenant",
        }, user: "owner");
        Assert.IsTrue(wsCreateFailed);
        Assert.AreEqual("forbidden", McpTestHost.Code(wsCreate));

        var (wsListFailed, wsList) = await host.CallAsync("access_list_grants", new()
        {
            ["scope"] = "tenant", ["subjectKind"] = "user", ["subjectId"] = "agent",
        }, user: "owner");
        Assert.IsTrue(wsListFailed);
        Assert.AreEqual("forbidden", McpTestHost.Code(wsList));

        // Tenant owner can create, list, and revoke tenant-scoped grants
        var (createdFailed, created) = await host.CallAsync("access_create_grant", new()
        {
            ["subjectKind"] = "user", ["subjectId"] = "agent", ["role"] = "viewer", ["scope"] = "tenant", ["justification"] = "Cross-workspace read",
        }, user: "tenant-owner");
        Assert.IsFalse(createdFailed, created.GetRawText());
        Assert.AreEqual("tenant", created.GetProperty("scope").GetProperty("kind").GetString());
        Assert.AreEqual("Cross-workspace read", created.GetProperty("justification").GetString());
        var grantId = created.GetProperty("id").GetString()!;
        var revision = created.GetProperty("revision").GetInt64();

        var (listedFailed, listed) = await host.CallAsync("access_list_grants", new()
        {
            ["scope"] = "tenant", ["subjectKind"] = "user", ["subjectId"] = "agent",
        }, user: "tenant-owner");
        Assert.IsFalse(listedFailed, listed.GetRawText());
        Assert.IsTrue(listed.GetProperty("items").EnumerateArray().Any(item => string.Equals(item.GetProperty("id").GetString(), grantId, StringComparison.Ordinal)));

        var (revokedFailed, _) = await host.CallAsync("access_revoke_grant", new()
        {
            ["grantId"] = grantId, ["expectedRevision"] = revision,
        }, user: "tenant-owner");
        Assert.IsFalse(revokedFailed);
    }

    [TestMethod]
    public async Task BreakGlassLifecycleEnforcesPermissionsRoleRestrictionsAndRevocation()
    {
        await using var host = await McpTestHost.CreateAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<IAccessRoleStore>();
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            await roles.CreateAsync(host.Scope, new AccessRole(new RoleId("oncall"), "On-Call Responder", [AccessPermissions.AccessBreakGlass]));
            await grants.CreateAsync(new AccessGrant(new GrantId("g-responder"), AccessPartition.From(host.Scope), new UserId("responder"),
                new RoleId("oncall"), new WorkspaceGrantScope(host.Scope.WorkspaceId)));
        }

        // Agent without access:break-glass is refused
        var (deniedActivate, deniedBody) = await host.CallAsync("access_activate_break_glass", new()
        {
            ["role"] = "supervisor", ["justification"] = "Incident INC-404 emergency triage", ["durationMinutes"] = 30,
        }, user: "agent");
        Assert.IsTrue(deniedActivate);
        Assert.AreEqual("forbidden", McpTestHost.Code(deniedBody));

        // Break-glass cannot elevate to administrative roles
        var (adminRoleFailed, adminRoleBody) = await host.CallAsync("access_activate_break_glass", new()
        {
            ["role"] = "owner", ["justification"] = "Incident INC-404 emergency triage", ["durationMinutes"] = 30,
        }, user: "responder");
        Assert.IsTrue(adminRoleFailed);
        Assert.AreEqual("invalid", McpTestHost.Code(adminRoleBody));

        // Responder activates Supervisor break-glass
        var (okFailed, activated) = await host.CallAsync("access_activate_break_glass", new()
        {
            ["role"] = "supervisor", ["justification"] = "Incident INC-404 emergency triage", ["durationMinutes"] = 45,
        }, user: "responder");
        Assert.IsFalse(okFailed, activated.GetRawText());
        Assert.AreEqual("breakGlass", activated.GetProperty("kind").GetString());
        Assert.AreEqual("supervisor", activated.GetProperty("role").GetString());
        var grantId = activated.GetProperty("id").GetString()!;
        var revision = activated.GetProperty("revision").GetInt64();

        // Responder can list their own active break-glass grants via access_list_break_glass
        var (responderListFailed, responderList) = await host.CallAsync("access_list_break_glass", user: "responder");
        Assert.IsFalse(responderListFailed, responderList.GetRawText());
        Assert.AreEqual(1, responderList.GetProperty("items").GetArrayLength());
        Assert.AreEqual(grantId, responderList.GetProperty("items")[0].GetProperty("id").GetString());

        // Responder cannot revoke break-glass without access:manage
        var (responderRevokeFailed, responderRevoke) = await host.CallAsync("access_revoke_break_glass", new()
        {
            ["grantId"] = grantId, ["expectedRevision"] = revision,
        }, user: "responder");
        Assert.IsTrue(responderRevokeFailed);
        Assert.AreEqual("forbidden", McpTestHost.Code(responderRevoke));

        // Owner sees it in access_list_break_glass, cannot revoke a standard grant via access_revoke_break_glass, and can revoke the break-glass grant
        var (ownerListFailed, ownerList) = await host.CallAsync("access_list_break_glass", user: "owner");
        Assert.IsFalse(ownerListFailed);
        Assert.IsTrue(ownerList.GetProperty("items").EnumerateArray().Any(item => string.Equals(item.GetProperty("id").GetString(), grantId, StringComparison.Ordinal)));

        var (standardRejectFailed, standardReject) = await host.CallAsync("access_revoke_break_glass", new()
        {
            ["grantId"] = "seed-1", ["expectedRevision"] = 1L,
        }, user: "owner");
        Assert.IsTrue(standardRejectFailed);
        Assert.AreEqual("invalid", McpTestHost.Code(standardReject));

        var (revokedFailed, _) = await host.CallAsync("access_revoke_break_glass", new()
        {
            ["grantId"] = grantId, ["expectedRevision"] = revision,
        }, user: "owner");
        Assert.IsFalse(revokedFailed);

        var (_, afterRevoke) = await host.CallAsync("access_list_break_glass", user: "owner");
        Assert.AreEqual(0, afterRevoke.GetProperty("items").GetArrayLength());
    }

    [TestMethod]
    public async Task ServiceAccountAndApiKeyToolsManageLifecycleWithoutLoggingSecrets()
    {
        await using var host = await McpTestHost.CreateAsync();

        // Unauthorized caller is refused
        var (agentFailed, agentBody) = await host.CallAsync("access_create_service_account", new()
        {
            ["clientId"] = "sa-unauth", ["displayName"] = "Unauth",
        }, user: "agent");
        Assert.IsTrue(agentFailed);
        Assert.AreEqual("forbidden", McpTestHost.Code(agentBody));

        // Reserved worker id is rejected
        var (reservedFailed, reservedBody) = await host.CallAsync("access_create_service_account", new()
        {
            ["clientId"] = "automation-worker", ["displayName"] = "Impostor",
        }, user: "owner");
        Assert.IsTrue(reservedFailed);
        Assert.AreEqual("rejected", McpTestHost.Code(reservedBody));

        // Create service account, issue API key, verify metadata & audit do not leak plaintext key
        var (createFailed, created) = await host.CallAsync("access_create_service_account", new()
        {
            ["clientId"] = "sa-mcp-sync", ["displayName"] = "MCP Sync Bot", ["description"] = "Syncs tickets",
        }, user: "owner");
        Assert.IsFalse(createFailed, created.GetRawText());
        Assert.AreEqual("sa-mcp-sync", created.GetProperty("clientId").GetString());
        Assert.IsTrue(created.GetProperty("isEnabled").GetBoolean());

        var (issueFailed, issued) = await host.CallAsync("access_issue_api_key", new()
        {
            ["clientId"] = "sa-mcp-sync", ["name"] = "ci-runner",
        }, user: "owner");
        Assert.IsFalse(issueFailed, issued.GetRawText());
        var plaintext = issued.GetProperty("plaintextKey").GetString()!;
        Assert.StartsWith("tf_sa_", plaintext, StringComparison.Ordinal);
        var keyId = issued.GetProperty("key").GetProperty("id").GetString()!;
        var keyVersion = issued.GetProperty("key").GetProperty("version").GetInt64();

        // Listing API keys never returns plaintext
        var (keysFailed, keys) = await host.CallAsync("access_list_api_keys", new() { ["clientId"] = "sa-mcp-sync" }, user: "owner");
        Assert.IsFalse(keysFailed);
        Assert.DoesNotContain(plaintext, keys.GetRawText(), StringComparison.Ordinal);
        Assert.AreEqual(1, keys.GetProperty("items").GetArrayLength());

        // Audit records never contain plaintext key
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var sink = (InMemoryAccessAuditSink)scope.ServiceProvider.GetRequiredService<IAccessAuditSink>();
            foreach (var entry in sink.Snapshot())
                Assert.DoesNotContain(plaintext, entry.Justification ?? string.Empty, StringComparison.Ordinal);
        }

        // Disable, verify disabled, revoke key, delete service account
        var (disableFailed, disabled) = await host.CallAsync("access_set_service_account_enabled", new()
        {
            ["clientId"] = "sa-mcp-sync", ["isEnabled"] = false, ["reason"] = "rotation", ["expectedVersion"] = created.GetProperty("version").GetInt64(),
        }, user: "owner");
        Assert.IsFalse(disableFailed, disabled.GetRawText());
        Assert.IsFalse(disabled.GetProperty("isEnabled").GetBoolean());

        var (revokeFailed, revoked) = await host.CallAsync("access_revoke_api_key", new()
        {
            ["keyId"] = keyId, ["reason"] = "rotated", ["expectedVersion"] = keyVersion,
        }, user: "owner");
        Assert.IsFalse(revokeFailed, revoked.GetRawText());
        Assert.IsTrue(revoked.TryGetProperty("revokedAtUtc", out _));

        var (deleteFailed, deleted) = await host.CallAsync("access_delete_service_account", new()
        {
            ["clientId"] = "sa-mcp-sync", ["expectedVersion"] = disabled.GetProperty("version").GetInt64(),
        }, user: "owner");
        Assert.IsFalse(deleteFailed, deleted.GetRawText());
        Assert.AreEqual("sa-mcp-sync", deleted.GetProperty("clientId").GetString());
    }

    [TestMethod]
    public async Task RoleFederationToolsEnforceAntiEscalationAndCrud()
    {
        await using var host = await McpTestHost.CreateAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            await grants.CreateAsync(new AccessGrant(new GrantId("g-ws-admin"), AccessPartition.From(host.Scope), new UserId("ws-admin"),
                BuiltInRoles.Admin.Id, new WorkspaceGrantScope(host.Scope.WorkspaceId)));
        }

        // Workspace Admin lacks WorkspaceManage / AccessManageTenant -> cannot map Owner (anti-escalation)
        var (escalateFailed, escalateBody) = await host.CallAsync("access_upsert_federation_mapping", new()
        {
            ["provider"] = "okta", ["externalRole"] = "superadmins", ["role"] = "owner",
        }, user: "ws-admin");
        Assert.IsTrue(escalateFailed);
        Assert.AreEqual("forbidden", McpTestHost.Code(escalateBody));

        // Agent without access:manage is refused
        var (agentFailed, agentBody) = await host.CallAsync("access_list_federation_mappings", user: "agent");
        Assert.IsTrue(agentFailed);
        Assert.AreEqual("forbidden", McpTestHost.Code(agentBody));

        // Owner creates, updates, lists, and deletes a mapping
        var (createFailed, created) = await host.CallAsync("access_upsert_federation_mapping", new()
        {
            ["provider"] = "okta", ["externalRole"] = "tier1-agents", ["role"] = "agent", ["description"] = "Tier 1 agents",
        }, user: "owner");
        Assert.IsFalse(createFailed, created.GetRawText());
        var mappingId = created.GetProperty("id").GetString()!;
        Assert.AreEqual("okta", created.GetProperty("provider").GetString());
        Assert.IsTrue(created.GetProperty("isEnabled").GetBoolean());

        var (updateFailed, updated) = await host.CallAsync("access_upsert_federation_mapping", new()
        {
            ["mappingId"] = mappingId, ["provider"] = "okta", ["externalRole"] = "tier1-agents", ["role"] = "viewer",
            ["isEnabled"] = false, ["expectedVersion"] = created.GetProperty("version").GetInt64(),
        }, user: "owner");
        Assert.IsFalse(updateFailed, updated.GetRawText());
        Assert.AreEqual("viewer", updated.GetProperty("role").GetString());
        Assert.IsFalse(updated.GetProperty("isEnabled").GetBoolean());

        var (listFailed, activeOnly) = await host.CallAsync("access_list_federation_mappings", new() { ["includeDisabled"] = false }, user: "owner");
        Assert.IsFalse(listFailed);
        Assert.AreEqual(0, activeOnly.GetProperty("items").GetArrayLength());

        var (deleteFailed, deleted) = await host.CallAsync("access_delete_federation_mapping", new()
        {
            ["mappingId"] = mappingId, ["expectedVersion"] = updated.GetProperty("version").GetInt64(),
        }, user: "owner");
        Assert.IsFalse(deleteFailed, deleted.GetRawText());
        Assert.AreEqual(mappingId, deleted.GetProperty("id").GetString());
    }

    [TestMethod]
    public void RegistryMarksOptionalAccessServicesUnavailableWithoutDisablingCoreAccessTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddTicketFlow().AddAccessControl();
        // Register ONLY core access stores — intentionally omit IServiceAccountStore, IApiKeyStore and IRoleFederationMappingStore
        services.AddSingleton<IAccessRoleStore, InMemoryAccessRoleStore>();
        services.AddSingleton<IAccessGrantStore, InMemoryAccessGrantStore>();
        services.AddSingleton<IAccessFieldPolicyStore, InMemoryAccessFieldPolicyStore>();
        services.AddSingleton<IAccessAuditSink, InMemoryAccessAuditSink>();
        services.AddMcpServer().WithTicketFlowTools(TicketFlowToolsets.Access);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var registry = provider.GetRequiredService<TicketFlowMcpRegistry>();

        Assert.IsTrue(registry.ServicesAvailable(TicketFlowToolsets.Access));
        Assert.IsTrue(registry.IsEnabled(TicketFlowToolCatalog.Find("access_list_roles")!));
        Assert.IsTrue(registry.IsEnabled(TicketFlowToolCatalog.Find("access_activate_break_glass")!));
        Assert.IsFalse(registry.IsEnabled(TicketFlowToolCatalog.Find("access_list_service_accounts")!),
            "Service account tools must be disabled when service account stores are not registered.");
        Assert.IsFalse(registry.IsEnabled(TicketFlowToolCatalog.Find("access_list_federation_mappings")!),
            "Role federation tools must be disabled when role federation mapping store is not registered.");
    }

    [TestMethod]
    public async Task ListBreakGlassPaginatesPastMaximumTargetPage()
    {
        await using var host = await McpTestHost.CreateAsync(features: false);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            var partition = AccessPartition.From(host.Scope);
            var target = new WorkspaceGrantScope(host.Scope.WorkspaceId);
            for (var i = 0; i < AccessLimits.MaximumTargetPage + 10; i++)
                await grants.CreateAsync(new AccessGrant(
                    new GrantId($"std-{i:D5}"),
                    partition,
                    new UserId($"u-{i}"),
                    BuiltInRoles.Viewer.Id,
                    target));

            var now = DateTimeOffset.UtcNow;
            await grants.CreateAsync(new AccessGrant(
                new GrantId("zzz-bg-after-page"),
                partition,
                new UserId("responder"),
                BuiltInRoles.Supervisor.Id,
                target,
                validFrom: now.AddMinutes(-1),
                validUntil: now.AddHours(1),
                justification: "Incident 1010 pagination check",
                kind: GrantKind.BreakGlass));
        }

        var (failed, listed) = await host.CallAsync("access_list_break_glass", user: "owner");
        Assert.IsFalse(failed, listed.GetRawText());
        var ids = listed.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()).ToList();
        CollectionAssert.Contains(ids, "zzz-bg-after-page");
    }

    [TestMethod]
    public async Task SingleIdAccessToolsReturnOpaqueNotFoundForCrossWorkspaceAndTenantRecordsAndHideForbiddenDetails()
    {
        await using var host = await McpTestHost.CreateAsync(features: false);
        var otherWorkspace = new WorkspaceId("ws-other");
        var partition = AccessPartition.From(host.Scope);
        string otherKeyId;
        string tenantKeyId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            var saStore = scope.ServiceProvider.GetRequiredService<IServiceAccountStore>();
            var apiKeys = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
            var mappings = scope.ServiceProvider.GetRequiredService<IRoleFederationMappingStore>();
            var now = DateTimeOffset.UtcNow;

            await grants.CreateAsync(new AccessGrant(new GrantId("other-std"), partition, new UserId("bob"), BuiltInRoles.Viewer.Id, new WorkspaceGrantScope(otherWorkspace)));
            await grants.CreateAsync(new AccessGrant(new GrantId("other-bg"), partition, new UserId("bob"), BuiltInRoles.Supervisor.Id, new WorkspaceGrantScope(otherWorkspace),
                validFrom: now, validUntil: now.AddHours(1), justification: "Cross-workspace incident 42", kind: GrantKind.BreakGlass));
            await grants.CreateAsync(new AccessGrant(new GrantId("tenant-std"), partition, new UserId("bob"), BuiltInRoles.Viewer.Id, TenantGrantScope.Instance));

            await saStore.CreateAsync(new ServiceAccount(new ApiClientId("sa-other-ws"), partition, new WorkspaceGrantScope(otherWorkspace), "Other WS SA"), "system:seed");
            await saStore.CreateAsync(new ServiceAccount(new ApiClientId("sa-tenant"), partition, TenantGrantScope.Instance, "Tenant SA"), "system:seed");

            var otherKey = ApiKeyId.NewId();
            var tenantKey = ApiKeyId.NewId();
            otherKeyId = otherKey.Value;
            tenantKeyId = tenantKey.Value;
            await apiKeys.CreateAsync(otherKey, partition, new ApiClientId("sa-other-ws"), "k-other", "tf_sa_11111111", new string('a', 64), "system:seed");
            await apiKeys.CreateAsync(tenantKey, partition, new ApiClientId("sa-tenant"), "k-tenant", "tf_sa_22222222", new string('b', 64), "system:seed");

            await mappings.UpsertAsync(new RoleFederationMappingId("map-other-ws"),
                new RoleFederationMapping("okta", "other-group", BuiltInRoles.Viewer.Id, partition, new WorkspaceGrantScope(otherWorkspace)), isEnabled: true, "system:seed");
            await mappings.UpsertAsync(new RoleFederationMappingId("map-tenant"),
                new RoleFederationMapping("okta", "tenant-group", BuiltInRoles.Viewer.Id, partition, TenantGrantScope.Instance), isEnabled: true, "system:seed");
        }

        // 1. Grant revocation tools return identical "not-found" for cross-workspace, tenant, and missing grants.
        foreach (var id in new[] { "other-std", "other-bg", "tenant-std", "missing-grant" })
        {
            var (bgFailed, bgBody) = await host.CallAsync("access_revoke_break_glass", new()
            {
                ["grantId"] = id, ["expectedRevision"] = 999L,
            }, user: "owner");
            Assert.IsTrue(bgFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(bgBody), $"access_revoke_break_glass leaked oracle for {id}: {bgBody.GetRawText()}");

            var (revFailed, revBody) = await host.CallAsync("access_revoke_grant", new()
            {
                ["grantId"] = id, ["expectedRevision"] = 999L,
            }, user: "owner");
            Assert.IsTrue(revFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(revBody), $"access_revoke_grant leaked oracle for {id}: {revBody.GetRawText()}");
        }

        // 2. Service account & API key single-ID tools return identical "not-found" for cross-workspace, unauthorized tenant, and missing IDs.
        foreach (var clientId in new[] { "sa-other-ws", "sa-tenant", "sa-missing" })
        {
            var (enFailed, enBody) = await host.CallAsync("access_set_service_account_enabled", new()
            {
                ["clientId"] = clientId, ["isEnabled"] = false,
            }, user: "owner");
            Assert.IsTrue(enFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(enBody), $"access_set_service_account_enabled leaked oracle for {clientId}: {enBody.GetRawText()}");

            var (delFailed, delBody) = await host.CallAsync("access_delete_service_account", new()
            {
                ["clientId"] = clientId,
            }, user: "owner");
            Assert.IsTrue(delFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(delBody), $"access_delete_service_account leaked oracle for {clientId}: {delBody.GetRawText()}");

            var (issueFailed, issueBody) = await host.CallAsync("access_issue_api_key", new()
            {
                ["clientId"] = clientId, ["name"] = "probe",
            }, user: "owner");
            Assert.IsTrue(issueFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(issueBody), $"access_issue_api_key leaked oracle for {clientId}: {issueBody.GetRawText()}");
        }

        foreach (var clientId in new[] { "sa-other-ws", "sa-missing" })
        {
            var (listFailed, listBody) = await host.CallAsync("access_list_api_keys", new()
            {
                ["clientId"] = clientId,
            }, user: "owner");
            Assert.IsTrue(listFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(listBody), $"access_list_api_keys leaked oracle for {clientId}: {listBody.GetRawText()}");
        }

        foreach (var keyId in new[] { otherKeyId, tenantKeyId, ApiKeyId.NewId().Value })
        {
            var (rkFailed, rkBody) = await host.CallAsync("access_revoke_api_key", new()
            {
                ["keyId"] = keyId,
            }, user: "owner");
            Assert.IsTrue(rkFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(rkBody), $"access_revoke_api_key leaked oracle for {keyId}: {rkBody.GetRawText()}");
        }

        // 3. Role federation single-ID tools return identical "not-found" for cross-workspace, tenant, and missing IDs (including expectedVersion > 0).
        foreach (var mappingId in new[] { "map-other-ws", "map-tenant", "map-missing" })
        {
            var (upFailed, upBody) = await host.CallAsync("access_upsert_federation_mapping", new()
            {
                ["mappingId"] = mappingId, ["provider"] = "okta", ["externalRole"] = "probe", ["role"] = "viewer", ["expectedVersion"] = 1L,
            }, user: "owner");
            Assert.IsTrue(upFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(upBody), $"access_upsert_federation_mapping leaked oracle for {mappingId}: {upBody.GetRawText()}");

            var (delFailed, delBody) = await host.CallAsync("access_delete_federation_mapping", new()
            {
                ["mappingId"] = mappingId, ["expectedVersion"] = 1L,
            }, user: "owner");
            Assert.IsTrue(delFailed);
            Assert.AreEqual("not-found", McpTestHost.Code(delBody), $"access_delete_federation_mapping leaked oracle for {mappingId}: {delBody.GetRawText()}");
        }

        // 4. Forbidden responses from AccessTools never leak internal permission/scope-tier details in errors.
        var (tenantCreateFailed, tenantCreateBody) = await host.CallAsync("access_create_service_account", new()
        {
            ["clientId"] = "sa-tenant-denied", ["displayName"] = "Denied", ["scope"] = "tenant",
        }, user: "owner");
        Assert.IsTrue(tenantCreateFailed);
        Assert.AreEqual("forbidden", McpTestHost.Code(tenantCreateBody));
        Assert.IsFalse(tenantCreateBody.GetProperty("error").TryGetProperty("errors", out _),
            $"Forbidden MCP response must not expose internal error details: {tenantCreateBody.GetRawText()}");
    }
}
