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
            new RoleFederationMapping("local", "team-macros", customRole.Id, fixture.Partition, fixture.Workspace));
        Assert.IsTrue(mappingResult.IsSuccess);

        // Field policy masks contact.email for Agent (only Owner unmasked)
        var emailField = new FieldPath("contact.email");
        await fixture.Policies.CreateAsync(fixture.Scope, new AccessFieldPolicy(emailField, [AccessPermissions.ContactRead], [BuiltInRoles.Owner.Id], AccessPermissions.ContactReveal));

        var user = new UserPrincipal(fixture.Scope, new UserId("alice"), teams: [new TeamId("team-macros")], externalRoles: ["idp-agent", "viewer"], identityProvider: "identityflow");

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
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(fedOwner, AccessPermissions.AccessManageTenant),
            "Static mappings synthesize workspace-scoped grants and must not confer tenant-scoped access:manage-tenant.");

        var createRole = await fixture.Admin.CreateRoleAsync(fedOwner, new AccessRole(new RoleId("fed-custom"), "Federated Custom", [AccessPermissions.ConversationRead]));
        Assert.AreEqual("applied", AccessFixture.Describe(createRole));

        // Explicit tenant-scoped dynamic mapping DOES confer access:manage-tenant when AllowAdministrativeRoles is true.
        var mappingStore = fixture.Provider.GetRequiredService<IRoleFederationMappingStore>();
        await mappingStore.UpsertAsync(
            new RoleFederationMappingId("tenant-owner-map"),
            new RoleFederationMapping("identityflow", "idp-tenant-owner", BuiltInRoles.Owner.Id, fixture.Partition, TenantGrantScope.Instance),
            isEnabled: true,
            "seed");
        var fedTenantOwner = new UserPrincipal(fixture.Scope, new UserId("fed-tenant-owner"), externalRoles: ["idp-tenant-owner"], identityProvider: "identityflow");
        Assert.AreEqual("allowed", await fixture.DescribeAsync(fedTenantOwner, AccessPermissions.AccessManageTenant));
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

    [TestMethod]
    public async Task RoleFederationEnforcesProviderMatchingAndRestrictsTeamTokens()
    {
        using var fixture = AccessFixture.Create(options: o =>
        {
            o.RoleFederation.Mode = RoleFederationMode.MergeWithLocalGrants;
            o.RoleFederation.MapTeamsAsExternalRoles = true;
        });

        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        Assert.IsTrue((await federation.UpsertMappingAsync(owner, new RoleFederationMappingId("m-idf"),
            new RoleFederationMapping("identityflow", "support-agents", BuiltInRoles.Agent.Id, fixture.Partition, fixture.Workspace))).IsSuccess);
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, new RoleFederationMappingId("m-any"),
            new RoleFederationMapping("*", "global-viewers", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace))).IsSuccess);
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, new RoleFederationMappingId("m-local"),
            new RoleFederationMapping("local", "team-local", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace))).IsSuccess);

        // Matching provider succeeds
        var idfUser = new UserPrincipal(fixture.Scope, new UserId("u-idf"), externalRoles: ["support-agents"], identityProvider: "identityflow");
        Assert.AreEqual("allowed", await fixture.DescribeAsync(idfUser, AccessPermissions.ConversationReply));

        // Mismatched provider or missing provider is denied for provider-specific mapping
        var oktaUser = new UserPrincipal(fixture.Scope, new UserId("u-okta"), externalRoles: ["support-agents"], identityProvider: "okta");
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(oktaUser, AccessPermissions.ConversationReply));

        var noIdpUser = new UserPrincipal(fixture.Scope, new UserId("u-none"), externalRoles: ["support-agents"]);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(noIdpUser, AccessPermissions.ConversationReply));

        // Wildcard provider "*" matches any or null IdentityProvider
        var wildcardUser = new UserPrincipal(fixture.Scope, new UserId("u-wild"), externalRoles: ["global-viewers"], identityProvider: "okta");
        Assert.AreEqual("allowed", await fixture.DescribeAsync(wildcardUser, AccessPermissions.ConversationRead));

        // Local team membership cannot match a provider-specific external IdP mapping when provider doesn't match
        var teamOnlyOkta = new UserPrincipal(fixture.Scope, new UserId("u-team-okta"), teams: [new TeamId("support-agents")], identityProvider: "okta");
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(teamOnlyOkta, AccessPermissions.ConversationReply));

        var teamOnlyNoIdp = new UserPrincipal(fixture.Scope, new UserId("u-team-none"), teams: [new TeamId("support-agents")]);
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(teamOnlyNoIdp, AccessPermissions.ConversationReply));

        // Local team membership MUST NOT forge IdP-pinned mapping even when principal's IdentityProvider matches, nor wildcard "*" IdP mapping
        var teamSameIdp = new UserPrincipal(fixture.Scope, new UserId("u-team-idf"), teams: [new TeamId("support-agents")], identityProvider: "identityflow");
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(teamSameIdp, AccessPermissions.ConversationReply));

        var teamWildcard = new UserPrincipal(fixture.Scope, new UserId("u-team-wild"), teams: [new TeamId("global-viewers")], identityProvider: "identityflow");
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(teamWildcard, AccessPermissions.ConversationRead));

        // Local team membership DOES match provider "local" (even when principal also has an external IdentityProvider)
        var teamLocal = new UserPrincipal(fixture.Scope, new UserId("u-team-local"), teams: [new TeamId("team-local")]);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(teamLocal, AccessPermissions.ConversationRead));

        var teamLocalWithIdp = new UserPrincipal(fixture.Scope, new UserId("u-team-local-idp"), teams: [new TeamId("team-local")], identityProvider: "identityflow");
        Assert.AreEqual("allowed", await fixture.DescribeAsync(teamLocalWithIdp, AccessPermissions.ConversationRead));
    }

    [TestMethod]
    public async Task FederatedOnlyPreservesBreakGlassAndLocalAdminPermissionsWithoutLeakingRegularLocalAllows()
    {
        using var fixture = AccessFixture.Create(options: o =>
        {
            o.RoleFederation.Mode = RoleFederationMode.FederatedOnly;
            o.RoleFederation.AllowAdministrativeRoles = false;
        });

        var localAdmin = fixture.User("local-admin");
        await fixture.GrantAsync(localAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        // Local admin keeps AccessManage/AccessManageTenant/AccessBreakGlass in evaluator & planner,
        // while regular non-admin permissions stay denied in FederatedOnly mode
        Assert.AreEqual("allowed", await fixture.DescribeAsync(localAdmin, AccessPermissions.AccessManage));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(localAdmin, AccessPermissions.AccessManageTenant));
        Assert.AreEqual("allowed", await fixture.DescribeAsync(localAdmin, AccessPermissions.AccessBreakGlass));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(localAdmin, AccessPermissions.ConversationRead));

        var held = await fixture.Planner.HeldAnywhereAsync(localAdmin, fixture.Scope, [AccessPermissions.AccessManage, AccessPermissions.ConversationRead]);
        Assert.Contains(AccessPermissions.AccessManage, held);
        Assert.DoesNotContain(AccessPermissions.ConversationRead, held);

        // Break-glass grants are preserved in FederatedOnly mode
        var responder = fixture.User("responder");
        var now = fixture.Clock.GetUtcNow();
        await fixture.SeedAsync(new AccessGrant(
            new GrantId("bg-fed-only"),
            fixture.Partition,
            responder.UserId,
            BuiltInRoles.Agent.Id,
            fixture.Workspace,
            GrantEffect.Allow,
            validFrom: now,
            validUntil: now.AddHours(1),
            justification: "Urgent production incident",
            kind: GrantKind.BreakGlass));
        Assert.AreEqual("allowed:break-glass", await fixture.DescribeAsync(responder, AccessPermissions.ConversationRead));
    }

    [TestMethod]
    public async Task StaticDirectFederationDoesNotImplicitlyPromoteToTenantScopeAndDeleteRoleBlocksReferencedMappings()
    {
        using var fixture = AccessFixture.Create(options: o =>
        {
            o.RoleFederation.Mode = RoleFederationMode.FederatedOnly;
            o.RoleFederation.AllowAdministrativeRoles = true;
            o.RoleFederation.StaticMappings["idp-owner"] = BuiltInRoles.Owner.Id;
        });

        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();

        // Static mapping to Owner grants workspace admin, NOT implicit tenant admin
        var fedOwner = new UserPrincipal(fixture.Scope, new UserId("fed-owner"), externalRoles: ["idp-owner"], identityProvider: "identityflow");
        var tenantGrantAttempt = new AccessGrant(new GrantId("g-tenant"), fixture.Partition, new UserId("bob"), BuiltInRoles.Viewer.Id, TenantGrantScope.Instance);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(fedOwner, tenantGrantAttempt)));

        // DeleteRoleAsync refuses while referenced by a role federation mapping
        var owner = fixture.User("real-owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var customRole = await fixture.CustomRoleAsync("fed-custom-role", AccessPermissions.ConversationRead);
        var mappingRes = await federation.UpsertMappingAsync(
            fedOwner,
            new RoleFederationMappingId("map-custom-ref"),
            new RoleFederationMapping("identityflow", "custom-group", customRole.Id, fixture.Partition, fixture.Workspace));
        Assert.IsTrue(mappingRes.IsSuccess);

        var deleteReferenced = await fixture.Admin.DeleteRoleAsync(fedOwner, customRole.Id, expectedRevision: 1);
        Assert.IsTrue(deleteReferenced.TryGetValue(out TicketFailure refFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, refFailure.Kind);
        Assert.AreEqual("role-in-use", refFailure.Code);

        // Explicit deny on AccessManage blocks RoleFederationService without falling through to AccessManageTenant
        var deniedAdmin = fixture.User("denied-fed-admin");
        await fixture.GrantAsync(deniedAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var denyRole = await fixture.CustomRoleAsync("deny-fed-manage", AccessPermissions.AccessManage);
        await fixture.GrantAsync(deniedAdmin.UserId, denyRole, fixture.Workspace, GrantEffect.Deny);
        var deniedMap = await federation.UpsertMappingAsync(
            deniedAdmin,
            new RoleFederationMappingId("map-denied"),
            new RoleFederationMapping("identityflow", "grp", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace));
        Assert.IsTrue(deniedMap.TryGetValue(out TicketFailure denyFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, denyFailure.Kind);
        Assert.AreEqual(nameof(AccessDenialReason.ExplicitDeny), denyFailure.Code);
    }

    [TestMethod]
    public async Task FederatedOnlyWithoutAdminRolesAlignsAdministrationWithEvaluator()
    {
        using var fixture = AccessFixture.Create(options: o =>
        {
            o.RoleFederation.Mode = RoleFederationMode.FederatedOnly;
            o.RoleFederation.AllowAdministrativeRoles = false;
            o.RoleFederation.StaticMappings["idp-agent"] = BuiltInRoles.Agent.Id;
            o.RoleFederation.StaticMappings["idp-owner"] = BuiltInRoles.Owner.Id;
        });

        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();
        var serviceAccounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        // Local-only Owner (no federated roles): holds admin perms, NOT operational perms (e.g. ConversationRead)
        var localOnlyAdmin = fixture.User("local-admin-no-fed");
        await fixture.GrantAsync(localOnlyAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        Assert.AreEqual("allowed", await fixture.DescribeAsync(localOnlyAdmin, AccessPermissions.AccessManage));
        Assert.AreEqual("denied:NoMatchingGrant", await fixture.DescribeAsync(localOnlyAdmin, AccessPermissions.ConversationRead));

        // Cannot create a role, grant, or federation mapping conferring ConversationRead (which evaluator denies!)
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(
            await fixture.Admin.CreateRoleAsync(localOnlyAdmin, new AccessRole(new RoleId("op-role"), "Op", [AccessPermissions.ConversationRead]))));
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(
            await fixture.Admin.CreateGrantAsync(localOnlyAdmin, new AccessGrant(new GrantId("g-esc"), fixture.Partition, new UserId("bob"), BuiltInRoles.Viewer.Id, fixture.Workspace))));
        var fedEsc = await federation.UpsertMappingAsync(
            localOnlyAdmin,
            new RoleFederationMappingId("map-esc-local"),
            new RoleFederationMapping("identityflow", "readers", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace));
        Assert.IsTrue(fedEsc.TryGetValue(out TicketFailure fedEscFailure));
        Assert.AreEqual("privilege-escalation", fedEscFailure.Code);

        // Local admin WITH federated Agent role: holds AccessManage locally + Agent operational perms via federation
        var hybridAdmin = new UserPrincipal(fixture.Scope, new UserId("hybrid-admin"), externalRoles: ["idp-agent"], identityProvider: "identityflow");
        await fixture.GrantAsync(hybridAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        // CAN create role/grant/mapping for Agent permissions (e.g. ConversationRead, ConversationReply)
        Assert.AreEqual("applied", AccessFixture.Describe(
            await fixture.Admin.CreateRoleAsync(hybridAdmin, new AccessRole(new RoleId("fed-agent-role"), "Fed Agent", [AccessPermissions.ConversationRead, AccessPermissions.ConversationReply]))));
        Assert.AreEqual("applied", AccessFixture.Describe(
            await fixture.Admin.CreateGrantAsync(hybridAdmin, new AccessGrant(new GrantId("g-agent-ok"), fixture.Partition, new UserId("bob"), BuiltInRoles.Agent.Id, fixture.Workspace))));
        Assert.IsTrue((await federation.UpsertMappingAsync(
            hybridAdmin,
            new RoleFederationMappingId("map-agent-ok"),
            new RoleFederationMapping("identityflow", "agents", BuiltInRoles.Agent.Id, fixture.Partition, fixture.Workspace))).IsSuccess);

        // CANNOT confer operational permission outside federated Agent (e.g. ConversationDelete)
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(
            await fixture.Admin.CreateRoleAsync(hybridAdmin, new AccessRole(new RoleId("too-high"), "Too High", [AccessPermissions.ConversationDelete]))));
        var saClient = new ApiClientId("sa-high-existing");
        await fixture.GrantAsync(saClient, BuiltInRoles.Supervisor, fixture.Workspace);
        var saEsc = await serviceAccounts.CreateServiceAccountAsync(hybridAdmin, new ServiceAccount(saClient, fixture.Partition, fixture.Workspace, "SA"));
        Assert.IsTrue(saEsc.TryGetValue(out TicketFailure saEscFailure));
        Assert.AreEqual("service-account-conflict", saEscFailure.Code);

        // Federated-only Owner (no local grants) still cannot administer when AllowAdministrativeRoles == false
        var fedOnlyOwner = new UserPrincipal(fixture.Scope, new UserId("fed-only-owner"), externalRoles: ["idp-owner"], identityProvider: "identityflow");
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(
            await fixture.Admin.CreateRoleAsync(fedOnlyOwner, new AccessRole(new RoleId("blocked"), "Blocked", [AccessPermissions.ConversationRead]))));
    }

    [TestMethod]
    public async Task RoleFederationServiceValidatesExpectedVersionBeforeCas()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var id = new RoleFederationMappingId("map-ver");
        var mapping = new RoleFederationMapping("identityflow", "agents", BuiltInRoles.Agent.Id, fixture.Partition, fixture.Workspace);

        var negUpsert = await federation.UpsertMappingAsync(owner, id, mapping, expectedVersion: -1);
        Assert.IsTrue(negUpsert.TryGetValue(out TicketFailure negFailure));
        Assert.AreEqual(TicketFailureKind.Invalid, negFailure.Kind);

        var staleNew = await federation.UpsertMappingAsync(owner, id, mapping, expectedVersion: 5);
        Assert.IsTrue(staleNew.TryGetValue(out TicketFailure staleNewFailure));
        Assert.AreEqual(TicketFailureKind.NotFound, staleNewFailure.Kind);

        Assert.IsTrue((await federation.UpsertMappingAsync(owner, id, mapping, expectedVersion: 0)).IsSuccess);

        var staleUpdate = await federation.UpsertMappingAsync(owner, id, mapping, expectedVersion: 2);
        Assert.IsTrue(staleUpdate.TryGetValue(out TicketFailure staleUpdateFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, staleUpdateFailure.Kind);
        Assert.AreEqual(1L, staleUpdateFailure.CurrentRevision);

        var staleDelete = await federation.DeleteMappingAsync(owner, id, expectedVersion: 9);
        Assert.IsTrue(staleDelete.TryGetValue(out TicketFailure staleDeleteFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, staleDeleteFailure.Kind);
        Assert.AreEqual(1L, staleDeleteFailure.CurrentRevision);
    }

    [TestMethod]
    public async Task UpsertAndDeleteMappingReturnOpaqueNotFoundWithoutCrossScopeAuditLeak()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var wsAdmin = fixture.User("ws-admin");
        await fixture.GrantAsync(wsAdmin.UserId, BuiltInRoles.Admin, fixture.Workspace);

        var otherWs = new WorkspaceId("ws-other");
        var otherOwner = new UserPrincipal(fixture.Partition.InWorkspace(otherWs), new UserId("owner"));
        var otherId = new RoleFederationMappingId("map-other-ws");
        Assert.IsTrue((await federation.UpsertMappingAsync(otherOwner, otherId,
            new RoleFederationMapping("okta", "other-agents", BuiltInRoles.Owner.Id, fixture.Partition, new TeamGrantScope(otherWs, new TeamId("secret-other-team"))))).IsSuccess);
        Assert.IsTrue((await federation.UpsertMappingAsync(otherOwner, otherId,
            new RoleFederationMapping("okta", "other-agents", BuiltInRoles.Supervisor.Id, fixture.Partition, new TeamGrantScope(otherWs, new TeamId("secret-other-team"))), expectedVersion: 1)).IsSuccess);

        var tenantId = new RoleFederationMappingId("map-tenant");
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, tenantId,
            new RoleFederationMapping("okta", "tenant-owners", BuiltInRoles.Owner.Id, fixture.Partition, TenantGrantScope.Instance))).IsSuccess);

        var missingId = new RoleFederationMappingId("map-truly-missing");
        var auditBefore = fixture.Audit.Snapshot().Count;

        // Upsert with expectedVersion > 0 must return identical NotFound for missing, cross-workspace, and tenant mappings.
        foreach (var targetId in new[] { otherId, tenantId, missingId })
        {
            foreach (var version in new long?[] { 1, 99 })
            {
                var attempt = await federation.UpsertMappingAsync(wsAdmin, targetId,
                    new RoleFederationMapping("okta", "probe", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace), expectedVersion: version);
                Assert.IsTrue(attempt.TryGetValue(out TicketFailure failure));
                Assert.AreEqual(TicketFailureKind.NotFound, failure.Kind, $"UpsertMappingAsync leaked cross-scope oracle for {targetId} version {version}.");
                Assert.IsNull(failure.CurrentRevision);
            }
        }

        // Upsert with expectedVersion = 0 or null against cross-workspace or tenant mappings also returns opaque NotFound.
        foreach (var targetId in new[] { otherId, tenantId })
        {
            foreach (var version in new long?[] { null, 0 })
            {
                var attempt = await federation.UpsertMappingAsync(wsAdmin, targetId,
                    new RoleFederationMapping("okta", "probe", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace), expectedVersion: version);
                Assert.IsTrue(attempt.TryGetValue(out TicketFailure failure));
                Assert.AreEqual(TicketFailureKind.NotFound, failure.Kind);
                Assert.IsNull(failure.CurrentRevision);
            }
        }

        // Upsert audit events must NOT contain existing mapping's RoleId (owner/supervisor), Updated kind, AccessManageTenant, TeamId, or DenialReason.
        var upsertAudits = fixture.Audit.Snapshot().Skip(auditBefore).ToList();
        Assert.HasCount(10, upsertAudits);
        foreach (var entry in upsertAudits)
        {
            Assert.AreEqual(AccessAuditKind.RoleFederationMappingCreated, entry.Kind);
            Assert.AreEqual(AccessPermissions.AccessManage, entry.Permission);
            Assert.AreEqual(BuiltInRoles.Viewer.Id, entry.RoleId);
            Assert.IsNull(entry.TeamId);
            Assert.IsNull(entry.InboxId);
            Assert.IsNull(entry.DenialReason);
        }

        // DeleteMappingAsync must return identical opaque NotFound and zero foreign audit metadata for missing, cross-workspace, and tenant mappings.
        var deleteAuditBefore = fixture.Audit.Snapshot().Count;
        foreach (var targetId in new[] { otherId, tenantId, missingId })
        {
            var attempt = await federation.DeleteMappingAsync(wsAdmin, targetId, expectedVersion: 1);
            Assert.IsTrue(attempt.TryGetValue(out TicketFailure failure));
            Assert.AreEqual(TicketFailureKind.NotFound, failure.Kind, $"DeleteMappingAsync leaked oracle for {targetId}.");
            Assert.IsNull(failure.CurrentRevision);
        }

        var deleteAudits = fixture.Audit.Snapshot().Skip(deleteAuditBefore).ToList();
        Assert.HasCount(3, deleteAudits);
        foreach (var entry in deleteAudits)
        {
            Assert.AreEqual(AccessAuditKind.RoleFederationMappingDeleted, entry.Kind);
            Assert.AreEqual(AccessPermissions.AccessManage, entry.Permission);
            Assert.IsNull(entry.RoleId);
            Assert.IsNull(entry.TeamId);
            Assert.IsNull(entry.InboxId);
            Assert.IsNull(entry.DenialReason);
        }
    }

    [TestMethod]
    public async Task AccessManageTenantOnlyPrincipalCannotManageNonTenantFederationMappings()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        // Tenant-only manager: holds tenant Owner MINUS access:manage in actor's workspace (only access:manage-tenant remains active).
        var tenantOnlyAdmin = fixture.User("tenant-only-admin");
        await fixture.GrantAsync(tenantOnlyAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var denyManageRole = await fixture.CustomRoleAsync("deny-access-manage-only", AccessPermissions.AccessManage);
        await fixture.GrantAsync(tenantOnlyAdmin.UserId, denyManageRole, fixture.Workspace, GrantEffect.Deny);

        // Tenant admin whose access:manage is denied on a specific team/inbox (still passes base workspace admin check).
        var teamDeniedAdmin = fixture.User("team-denied-admin");
        await fixture.GrantAsync(teamDeniedAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(teamDeniedAdmin.UserId, denyManageRole, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("t1")), GrantEffect.Deny);
        await fixture.GrantAsync(teamDeniedAdmin.UserId, denyManageRole, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i1")), GrantEffect.Deny);

        // Seed existing non-tenant mappings as owner
        var wsMapId = new RoleFederationMappingId("ws-existing");
        var teamMapId = new RoleFederationMappingId("team-existing");
        var inboxMapId = new RoleFederationMappingId("inbox-existing");
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, wsMapId,
            new RoleFederationMapping("okta", "ws-viewers", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace))).IsSuccess);
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, teamMapId,
            new RoleFederationMapping("okta", "team-viewers", BuiltInRoles.Viewer.Id, fixture.Partition, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("t1"))))).IsSuccess);
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, inboxMapId,
            new RoleFederationMapping("okta", "inbox-viewers", BuiltInRoles.Viewer.Id, fixture.Partition, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i1"))))).IsSuccess);

        // Tenant-only admin CANNOT create or update workspace, team, or inbox mappings
        GrantScope[] nonTenantScopes =
        [
            fixture.Workspace,
            new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("t1")),
            new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i1")),
        ];
        foreach (var targetScope in nonTenantScopes)
        {
            var upsert = await federation.UpsertMappingAsync(
                tenantOnlyAdmin,
                new RoleFederationMappingId("denied-" + targetScope.GetType().Name),
                new RoleFederationMapping("okta", "grp", BuiltInRoles.Viewer.Id, fixture.Partition, targetScope));
            Assert.IsTrue(upsert.TryGetValue(out TicketFailure failure));
            Assert.AreEqual(TicketFailureKind.Forbidden, failure.Kind);
        }

        // Tenant-only admin CANNOT delete existing non-tenant mappings
        foreach (var existingId in new[] { wsMapId, teamMapId, inboxMapId })
        {
            var del = await federation.DeleteMappingAsync(tenantOnlyAdmin, existingId, expectedVersion: 1);
            Assert.IsTrue(del.TryGetValue(out TicketFailure failure));
            Assert.AreEqual(TicketFailureKind.Forbidden, failure.Kind);
        }

        // Team/inbox-denied tenant admin is blocked by sub-scope Deny containment on workspace base admin check (Forbidden) and cannot upsert/delete via access:manage-tenant fallback.
        foreach (var existingId in new[] { teamMapId, inboxMapId })
        {
            var del = await federation.DeleteMappingAsync(teamDeniedAdmin, existingId, expectedVersion: 1);
            Assert.IsTrue(del.TryGetValue(out TicketFailure failure));
            Assert.AreEqual(TicketFailureKind.Forbidden, failure.Kind);
        }
        foreach (var targetScope in nonTenantScopes)
        {
            var upsert = await federation.UpsertMappingAsync(
                teamDeniedAdmin,
                new RoleFederationMappingId("team-denied-" + targetScope.GetType().Name),
                new RoleFederationMapping("okta", "grp", BuiltInRoles.Viewer.Id, fixture.Partition, targetScope));
            Assert.IsTrue(upsert.TryGetValue(out TicketFailure failure));
            Assert.AreEqual(TicketFailureKind.Forbidden, failure.Kind);
        }

        // Tenant-only admin CAN still create a tenant-scoped mapping (using access:manage-tenant)
        var tenantMapId = new RoleFederationMappingId("tenant-ok");
        Assert.IsTrue((await federation.UpsertMappingAsync(tenantOnlyAdmin, tenantMapId,
            new RoleFederationMapping("okta", "tenant-viewers", BuiltInRoles.Viewer.Id, fixture.Partition, TenantGrantScope.Instance))).IsSuccess);
    }

    [TestMethod]
    public async Task PrimaryRoleFederationAdminGatesEvaluateResourceConditionsConservatively()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var existingId = new RoleFederationMappingId("rf-cond-existing");
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, existingId,
            new RoleFederationMapping("okta", "viewers", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace))).IsSuccess);

        // Conditional Deny on AssigneeIsSelfCondition / TeamInPrincipalTeamsCondition must fail closed on base and scope admin gates.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("rf-deny-assignee", new AssigneeIsSelfCondition()),
            ("rf-deny-team", new TeamInPrincipalTeamsCondition()),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin, effect: GrantEffect.Deny, condition: condition);

            var list = await federation.ListMappingsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, name);

            var catalog = await federation.GetRoleCatalogAsync(actor);
            Assert.IsTrue(catalog.TryGetValue(out TicketFailure catalogFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, catalogFailure.Kind, name);

            var upsert = await federation.UpsertMappingAsync(actor, new RoleFederationMappingId($"m-{name}"),
                new RoleFederationMapping("okta", "grp", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace));
            Assert.IsTrue(upsert.TryGetValue(out TicketFailure upsertFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, upsertFailure.Kind, name);

            var delete = await federation.DeleteMappingAsync(actor, existingId, expectedVersion: 1);
            Assert.IsTrue(delete.TryGetValue(out TicketFailure deleteFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, deleteFailure.Kind, name);
        }

        // Conditional Allow with Not(AssigneeIsSelfCondition) / Not(TeamInPrincipalTeamsCondition) must NOT authorize role federation administration.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("rf-allow-not-assignee", new NotCondition(new AssigneeIsSelfCondition())),
            ("rf-allow-not-team", new NotCondition(new TeamInPrincipalTeamsCondition())),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin, condition: condition);

            var list = await federation.ListMappingsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, name);

            var catalog = await federation.GetRoleCatalogAsync(actor);
            Assert.IsTrue(catalog.TryGetValue(out TicketFailure catalogFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, catalogFailure.Kind, name);

            var upsert = await federation.UpsertMappingAsync(actor, new RoleFederationMappingId($"m-{name}"),
                new RoleFederationMapping("okta", "grp", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace));
            Assert.IsTrue(upsert.TryGetValue(out TicketFailure upsertFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, upsertFailure.Kind, name);
        }
    }

    [TestMethod]
    public async Task SubScopeAndCrossWorkspaceDenyBlockRoleFederationAdministrationAndConferral()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();

        var noDeleteRole = await fixture.CustomRoleAsync("rf-no-delete", AccessPermissions.ConversationDelete);
        var noManageRole = await fixture.CustomRoleAsync("rf-no-manage", AccessPermissions.AccessManage);
        var inboxVip = new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip"));
        var teamBilling = new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing"));

        // 1. Workspace admin with sub-scope Deny(ConversationDelete) cannot create a workspace-scoped mapping conferring Supervisor.
        foreach (var (name, denyScope) in new (string, GrantScope)[]
        {
            ("rf-inbox-no-delete", inboxVip),
            ("rf-team-no-delete", teamBilling),
        })
        {
            var actor = fixture.User(name);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin);
            await fixture.GrantAsync(actor.UserId, noDeleteRole, denyScope, GrantEffect.Deny);

            var supMapping = await federation.UpsertMappingAsync(actor, new RoleFederationMappingId($"m-sup-{name}"),
                new RoleFederationMapping("okta", "sup-group", BuiltInRoles.Supervisor.Id, fixture.Partition, fixture.Workspace));
            Assert.IsTrue(supMapping.TryGetValue(out TicketFailure supFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, supFailure.Kind, name);

            var viewerMapping = await federation.UpsertMappingAsync(actor, new RoleFederationMappingId($"m-view-{name}"),
                new RoleFederationMapping("okta", $"view-group-{name}", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace));
            Assert.IsTrue(viewerMapping.IsSuccess, name);
        }

        // 2. Workspace admin with sub-scope Deny(AccessManage) cannot create workspace-scoped mappings or list/catalog.
        foreach (var (name, denyScope) in new (string, GrantScope)[]
        {
            ("rf-inbox-no-manage", inboxVip),
            ("rf-team-no-manage", teamBilling),
        })
        {
            var actor = fixture.User(name);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin);
            await fixture.GrantAsync(actor.UserId, noManageRole, denyScope, GrantEffect.Deny);

            var upsert = await federation.UpsertMappingAsync(actor, new RoleFederationMappingId($"m-mgr-{name}"),
                new RoleFederationMapping("okta", "view-group", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace));
            Assert.IsTrue(upsert.TryGetValue(out TicketFailure upsertFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, upsertFailure.Kind, name);

            var list = await federation.ListMappingsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, name);
        }

        // 3. Tenant admin invoked from wsA with Deny(no-delete, Workspace(wsB)) cannot create TenantGrantScope mapping for Supervisor.
        var wsB = new WorkspaceId("ws-b");
        var wsBScope = fixture.Partition.InWorkspace(wsB);
        var noDeleteInWsB = new AccessRole(new RoleId("no-delete"), "No Delete in wsB", [AccessPermissions.ConversationDelete]);
        var noManageTenantInWsB = new AccessRole(new RoleId("no-manage-tenant"), "No Manage Tenant in wsB", [AccessPermissions.AccessManageTenant]);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await fixture.Roles.CreateAsync(wsBScope, noDeleteInWsB)).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await fixture.Roles.CreateAsync(wsBScope, noManageTenantInWsB)).Value);

        var tenantAdmin = fixture.User("rf-tenant-deny-wsb");
        await fixture.GrantAsync(tenantAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(tenantAdmin.UserId, noDeleteInWsB, new WorkspaceGrantScope(wsB), GrantEffect.Deny);

        var tenantSup = await federation.UpsertMappingAsync(tenantAdmin, new RoleFederationMappingId("m-t-sup"),
            new RoleFederationMapping("okta", "tenant-sup", BuiltInRoles.Supervisor.Id, fixture.Partition, TenantGrantScope.Instance));
        Assert.IsTrue(tenantSup.TryGetValue(out TicketFailure tenantSupFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, tenantSupFailure.Kind);

        Assert.IsTrue((await federation.UpsertMappingAsync(tenantAdmin, new RoleFederationMappingId("m-t-view"),
            new RoleFederationMapping("okta", "tenant-view", BuiltInRoles.Viewer.Id, fixture.Partition, TenantGrantScope.Instance))).IsSuccess);

        // Tenant admin invoked from wsA with Deny(AccessManageTenant, Workspace(wsB)) cannot create any TenantGrantScope mapping.
        var blockedTenantAdmin = fixture.User("rf-tenant-deny-manage-wsb");
        await fixture.GrantAsync(blockedTenantAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(blockedTenantAdmin.UserId, noManageTenantInWsB, new WorkspaceGrantScope(wsB), GrantEffect.Deny);

        var blockedUpsert = await federation.UpsertMappingAsync(blockedTenantAdmin, new RoleFederationMappingId("m-t-blocked"),
            new RoleFederationMapping("okta", "tenant-view", BuiltInRoles.Viewer.Id, fixture.Partition, TenantGrantScope.Instance));
        Assert.IsTrue(blockedUpsert.TryGetValue(out TicketFailure blockedFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, blockedFailure.Kind);
    }

    [TestMethod]
    public async Task BaseAdminEnforcesServiceAccountScopeContainment()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var federation = scope.ServiceProvider.GetRequiredService<IRoleFederationService>();
        var saStore = scope.ServiceProvider.GetRequiredService<IServiceAccountStore>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var existingId = new RoleFederationMappingId("m-existing");
        Assert.IsTrue((await federation.UpsertMappingAsync(owner, existingId,
            new RoleFederationMapping("okta", "viewers", BuiltInRoles.Viewer.Id, fixture.Partition, fixture.Workspace))).IsSuccess);

        // 1. Team-scoped and inbox-scoped service accounts holding workspace Admin grants MUST NOT call ListMappingsAsync or GetRoleCatalogAsync.
        var teamSaId = new ApiClientId("rf-sa-team");
        var inboxSaId = new ApiClientId("rf-sa-inbox");
        Assert.IsTrue((await saStore.CreateAsync(new ServiceAccount(teamSaId, fixture.Partition, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("t1")), "Team SA"), "owner")).IsSuccess);
        Assert.IsTrue((await saStore.CreateAsync(new ServiceAccount(inboxSaId, fixture.Partition, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i1")), "Inbox SA"), "owner")).IsSuccess);
        await fixture.GrantAsync(teamSaId, BuiltInRoles.Admin, fixture.Workspace);
        await fixture.GrantAsync(inboxSaId, BuiltInRoles.Admin, fixture.Workspace);

        foreach (var clientId in new[] { teamSaId, inboxSaId })
        {
            var actor = fixture.Client(clientId.Value);

            var list = await federation.ListMappingsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), clientId.Value);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, clientId.Value);

            var catalog = await federation.GetRoleCatalogAsync(actor);
            Assert.IsTrue(catalog.TryGetValue(out TicketFailure catalogFailure), clientId.Value);
            Assert.AreEqual(TicketFailureKind.Forbidden, catalogFailure.Kind, clientId.Value);
        }

        // 2. Workspace-scoped service account CANNOT use AccessManageTenant fallback.
        var wsSaId = new ApiClientId("rf-sa-ws-tenant-fallback");
        Assert.IsTrue((await saStore.CreateAsync(new ServiceAccount(wsSaId, fixture.Partition, fixture.Workspace, "WS SA"), "owner")).IsSuccess);
        await fixture.GrantAsync(wsSaId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var denyManageRole = await fixture.CustomRoleAsync("rf-deny-manage", AccessPermissions.AccessManage);
        await fixture.GrantAsync(wsSaId, denyManageRole, fixture.Workspace, GrantEffect.Deny);

        var wsSaActor = fixture.Client(wsSaId.Value);
        Assert.IsTrue((await federation.ListMappingsAsync(wsSaActor)).TryGetValue(out TicketFailure wsListFail));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsListFail.Kind);
        Assert.IsTrue((await federation.GetRoleCatalogAsync(wsSaActor)).TryGetValue(out TicketFailure wsCatalogFail));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsCatalogFail.Kind);
        Assert.IsTrue((await federation.UpsertMappingAsync(wsSaActor, new RoleFederationMappingId("m-ws-esc"),
            new RoleFederationMapping("okta", "esc", BuiltInRoles.Viewer.Id, fixture.Partition, TenantGrantScope.Instance))).TryGetValue(out TicketFailure wsUpsertTenantFail));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsUpsertTenantFail.Kind);

        // 3. Workspace-scoped SA with AccessManage CAN list mappings and fetch role catalog for its workspace.
        var wsAdminSaId = new ApiClientId("rf-sa-ws-admin");
        Assert.IsTrue((await saStore.CreateAsync(new ServiceAccount(wsAdminSaId, fixture.Partition, fixture.Workspace, "WS Admin SA"), "owner")).IsSuccess);
        await fixture.GrantAsync(wsAdminSaId, BuiltInRoles.Admin, fixture.Workspace);
        var wsAdminActor = fixture.Client(wsAdminSaId.Value);
        Assert.IsTrue((await federation.ListMappingsAsync(wsAdminActor)).IsSuccess);
        Assert.IsTrue((await federation.GetRoleCatalogAsync(wsAdminActor)).IsSuccess);
    }
}
