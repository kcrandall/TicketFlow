using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class RoleFederationTests
{
    [TestMethod]
    public async Task DisabledModeIgnoresExternalRoles()
    {
        using var fixture = AccessFixture.Create();
        var user = new UserPrincipal(fixture.Scope, new UserId("fed-1"), externalRoles: ["agent", "owner"]);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(user, AccessPermissions.ConversationRead));
    }

    [TestMethod]
    public async Task MergeWithLocalGrantsResolvesStaticDynamicDirectAndTeamRolesWhileLocalDenyAndFieldPoliciesOverride()
    {
        using var fixture = AccessFixture.Create(options: o =>
        {
            o.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
            o.RoleFederation.AllowDirectRoleIdMatching = true;
            o.RoleFederation.MapTeamsAsExternalRoles = true;
            o.RoleFederation.StaticMappings["idp-agent"] = BuiltInRoles.Agent.Id;
        });

        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var customRole = await fixture.CustomRoleAsync("macro-runner", new Permission("macro:run"));
        var mappingResult = await federation.UpsertMappingAsync(
            owner,
            new RoleFederationMappingId("map-1"),
            new RoleFederationMapping("identityflow", "team-macros", customRole.Id, fixture.Partition, fixture.Workspace));
        Assert.IsTrue(mappingResult.IsSuccess);

        // Field policy masks contact.email for Agent (only Owner unmasked)
        var emailField = new FieldPath("contact.email");
        await fixture.Policies.CreateAsync(fixture.Scope, new AccessFieldPolicy(emailField, [AccessPermissions.ContactRead], [BuiltInRoles.Owner.Id], AccessPermissions.ContactReveal));

        var user = new UserPrincipal(fixture.Scope, new UserId("alice"), teams: [new TeamId("team-macros")], externalRoles: ["idp-agent", "viewer"]);

        // Static mapping grants Agent permissions
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        // Direct role ID matching grants Viewer permissions
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, AccessPermissions.ReportView));
        // Team-as-external-role + dynamic mapping grants custom role
        Assert.AreEqual("allowed", await fixture.DescribeAsync(user, new Permission("macro:run")));
        // Field policy masks contact.email even when allowed via federated role
        Assert.AreEqual("masked:contact.email", await fixture.DescribeAsync(user, AccessPermissions.ContactRead));

        // Local explicit Deny ALWAYS overrides federated Allow (both evaluator and query planner)
        await fixture.GrantAsync(user.UserId, BuiltInRoles.Agent, fixture.Workspace, GrantEffect.Deny);
        Assert.AreEqual("denied:ExplicitDeny", await fixture.DescribeAsync(user, AccessPermissions.ConversationReply));
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(user, AccessPermissions.ConversationReply, fixture.Scope)));
    }

    [TestMethod]
    public async Task FederatedOnlyIgnoresLocalAllowsAndHonorsAllowedRolesAndAdminStripping()
    {
        using var fixture = AccessFixture.Create(options: o =>
        {
            o.RoleFederation.Mode = RoleFederationMode.FederatedOnly;
            o.RoleFederation.AllowAdministrativeRoles = false;
            o.RoleFederation.StaticMappings["idp-owner"] = BuiltInRoles.Owner.Id;
            o.RoleFederation.StaticMappings["idp-viewer"] = BuiltInRoles.Viewer.Id;
            o.RoleFederation.AllowedRoles.Add(BuiltInRoles.Owner.Id);
        });

        // Local allow grant is ignored in FederatedOnly mode
        var localOnly = fixture.User("local-only");
        await fixture.GrantAsync(localOnly.UserId, BuiltInRoles.Agent, fixture.Workspace);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(localOnly, AccessPermissions.ConversationRead));

        // Viewer is excluded by AllowedRoles
        var excluded = new UserPrincipal(fixture.Scope, new UserId("excluded"), externalRoles: ["idp-viewer"]);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(excluded, AccessPermissions.ConversationRead));

        // Owner is allowed, BUT administrative permissions are stripped when AllowAdministrativeRoles == false!
        var fedOwner = new UserPrincipal(fixture.Scope, new UserId("fed-owner"), externalRoles: ["idp-owner"]);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fedOwner, AccessPermissions.ConversationRead));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fedOwner, AccessPermissions.ConversationDelete));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fedOwner, AccessPermissions.AccessManage));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fedOwner, AccessPermissions.AccessManageTenant));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fedOwner, AccessPermissions.AccessBreakGlass));

        // Cannot create roles through AccessAdministrator when AllowAdministrativeRoles == false
        var createRole = await fixture.Admin.CreateRoleAsync(fedOwner, new AccessRole(new RoleId("sneaky"), "Sneaky", [AccessPermissions.ConversationRead]));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(createRole));
    }

    [TestMethod]
    public async Task AllowAdministrativeRolesPermitsFederatedAccessAdministration()
    {
        using var fixture = AccessFixture.Create(options: o =>
        {
            o.RoleFederation.Mode = RoleFederationMode.FederatedOnly;
            o.RoleFederation.AllowAdministrativeRoles = true;
            o.RoleFederation.StaticMappings["idp-owner"] = BuiltInRoles.Owner.Id;
        });

        var fedOwner = new UserPrincipal(fixture.Scope, new UserId("fed-owner"), externalRoles: ["idp-owner"]);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fedOwner, AccessPermissions.AccessManage));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fedOwner, AccessPermissions.AccessManageTenant));

        var createRole = await fixture.Admin.CreateRoleAsync(fedOwner, new AccessRole(new RoleId("fed-custom"), "Federated Custom", [AccessPermissions.ConversationRead]));
        Assert.AreEqual("applied", AccessFixture.Describe(createRole));
    }

    [TestMethod]
    public async Task RoleFederationServiceEnforcesAntiEscalationAntiBreakGlassScopeAndExportsCatalog()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        // Workspace Admin holds AccessManage but NOT WorkspaceManage or AccessManageTenant -> cannot federate Owner (anti-escalation)
        var wsAdmin = fixture.User("ws-admin");
        await fixture.GrantAsync(wsAdmin.UserId, BuiltInRoles.Admin, fixture.Workspace);
        var escalate = await service.UpsertMappingAsync(
            wsAdmin,
            new RoleFederationMappingId("map-esc"),
            new RoleFederationMapping("identityflow", "all-owners", BuiltInRoles.Owner.Id, fixture.Partition, fixture.Workspace));
        Assert.IsTrue(escalate.TryGetValue(out TicketFailure escFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, escFailure.Kind);
        Assert.AreEqual("privilege-escalation", escFailure.Code);

        // Tenant mapping cannot reference custom role
        var customRole = await fixture.CustomRoleAsync("custom-agent", AccessPermissions.ConversationRead);
        var tenantCustom = await service.UpsertMappingAsync(
            owner,
            new RoleFederationMappingId("map-tenant-custom"),
            new RoleFederationMapping("identityflow", "custom", customRole.Id, fixture.Partition, TenantGrantScope.Instance));
        Assert.IsTrue(tenantCustom.TryGetValue(out TicketFailure tenantCustomFailure));
        Assert.AreEqual(TicketFailureKind.Invalid, tenantCustomFailure.Kind);

        // Break-glass cannot manage federation mappings
        var bgUser = fixture.User("bg-user");
        var bgRole = await fixture.CustomRoleAsync("bg-fed", AccessPermissions.AccessManage, AccessPermissions.ConversationRead);
        var now = fixture.Clock.GetUtcNow();
        await fixture.SeedAsync(new AccessGrant(
            new GrantId("bg-fed-grant"),
            fixture.Partition,
            bgUser.UserId,
            bgRole.Id,
            fixture.Workspace,
            GrantEffect.Allow,
            validFrom: now,
            validUntil: now.AddHours(1),
            justification: "Emergency federation check",
            kind: GrantKind.BreakGlass));
        var bgAttempt = await service.UpsertMappingAsync(
            bgUser,
            new RoleFederationMappingId("map-bg"),
            new RoleFederationMapping("identityflow", "readers", customRole.Id, fixture.Partition, fixture.Workspace));
        Assert.IsTrue(bgAttempt.TryGetValue(out TicketFailure bgFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, bgFailure.Kind);
        Assert.AreEqual("break-glass-not-allowed", bgFailure.Code);

        // Owner creates mapping, lists, updates, exports catalog, deletes
        var created = await service.UpsertMappingAsync(
            owner,
            new RoleFederationMappingId("map-ok"),
            new RoleFederationMapping("identityflow", "support-custom", customRole.Id, fixture.Partition, fixture.Workspace, "Mapped from IdentityFlow"));
        Assert.IsTrue(created.TryGetValue(out TicketValue<AccessChangeApplied<StoredRoleFederationMapping>> ok));
        Assert.AreEqual(1L, ok.Value.Value.Version);

        var catalogResult = await service.GetRoleCatalogAsync(owner);
        Assert.IsTrue(catalogResult.TryGetValue(out TicketValue<FederatedRoleCatalog> catalog));
        Assert.AreEqual(fixture.Scope.TenantId, catalog.Value.TenantId);
        Assert.AreEqual(fixture.Scope.WorkspaceId, catalog.Value.WorkspaceId);
        Assert.AreEqual(BuiltInRoles.All.Count + 2, catalog.Value.Roles.Count);
        var customDescriptor = catalog.Value.Roles.Single(r => r.RoleId == customRole.Id);
        Assert.IsFalse(customDescriptor.IsBuiltIn);
        Assert.HasCount(1, customDescriptor.Mappings);
        Assert.AreEqual("support-custom", customDescriptor.Mappings[0].ExternalRole);

        var deleted = await service.DeleteMappingAsync(owner, new RoleFederationMappingId("map-ok"), expectedVersion: 1);
        Assert.IsTrue(deleted.IsSuccess);
        Assert.IsTrue((await service.ListMappingsAsync(owner)).TryGetValue(out TicketValue<IReadOnlyList<StoredRoleFederationMapping>> remaining));
        Assert.IsEmpty(remaining.Value);
    }
}
