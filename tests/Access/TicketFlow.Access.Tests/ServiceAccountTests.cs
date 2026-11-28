using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed partial class ServiceAccountTests
{
    [GeneratedRegex(@"^tf_sa_[0-9a-f]{8}\.[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApiKeyFormatRegex();

    [TestMethod]
    public async Task ServiceAccountLifecycleAndApiKeyAuthenticationWorkEndToEnd()
    {
        using var fixture = AccessFixture.Create();
        var service = fixture.Provider.CreateScope().ServiceProvider.GetRequiredService<IServiceAccountService>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var clientId = new ApiClientId("etl-sync");
        var account = new ServiceAccount(clientId, fixture.Partition, fixture.Workspace, "ETL Sync Bot", "Nightly sync");

        var createResult = await service.CreateServiceAccountAsync(owner, account);
        Assert.IsTrue(createResult.TryGetValue(out TicketValue<AccessChangeApplied<StoredServiceAccount>> created));
        Assert.AreEqual(1L, created.Value.Value.Version);
        Assert.IsTrue(created.Value.Value.IsEnabled);

        var listResult = await service.ListServiceAccountsAsync(owner);
        Assert.IsTrue(listResult.TryGetValue(out TicketValue<IReadOnlyList<StoredServiceAccount>> listed));
        Assert.HasCount(1, listed.Value);

        var updateResult = await service.UpdateServiceAccountAsync(owner, clientId, "ETL Sync v2", "Updated desc", expectedVersion: 1);
        Assert.IsTrue(updateResult.TryGetValue(out TicketValue<AccessChangeApplied<StoredServiceAccount>> updated));
        Assert.AreEqual(2L, updated.Value.Value.Version);
        Assert.AreEqual("ETL Sync v2", updated.Value.Value.Account.DisplayName);

        // Concurrency conflict on stale version
        var staleUpdate = await service.UpdateServiceAccountAsync(owner, clientId, "Stale", expectedVersion: 1);
        Assert.IsTrue(staleUpdate.TryGetValue(out TicketFailure staleFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, staleFailure.Kind);
        Assert.AreEqual(2L, staleFailure.CurrentRevision);

        // Issue API key
        var expiresAt = fixture.Clock.GetUtcNow().AddHours(2);
        var issueResult = await service.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(clientId, "primary", expiresAt));
        Assert.IsTrue(issueResult.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> issued));
        var plaintext = issued.Value.Value.PlaintextKey;
        var metadata = issued.Value.Value.Key;
        Assert.IsTrue(ApiKeyFormatRegex().IsMatch(plaintext), $"Unexpected key format: {plaintext}");
        Assert.IsTrue(plaintext.StartsWith(metadata.KeyPrefix + ".", StringComparison.Ordinal));
        Assert.IsNull(metadata.LastUsedAtUtc);

        // Verify zero secret leakage in audit events and StoredApiKey metadata
        foreach (var auditEvent in fixture.Audit.Snapshot())
        {
            Assert.IsFalse(auditEvent.ResourceId?.Contains(plaintext, StringComparison.Ordinal) == true);
            Assert.IsFalse(auditEvent.Justification?.Contains(plaintext, StringComparison.Ordinal) == true);
        }
        Assert.IsFalse(metadata.ToString().Contains(plaintext, StringComparison.Ordinal));

        // Authenticate valid key
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        var authOk = await service.AuthenticateApiKeyAsync(plaintext, fixture.Scope);
        Assert.IsTrue(authOk.IsAuthenticated);
        Assert.IsNotNull(authOk.Principal);
        Assert.AreEqual(clientId, authOk.Principal.ClientId);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), authOk.ApiKey!.LastUsedAtUtc);

        // Wrong secret with same prefix fails
        var wrongSecret = metadata.KeyPrefix + "." + new string('A', 43);
        var authWrong = await service.AuthenticateApiKeyAsync(wrongSecret, fixture.Scope);
        Assert.IsFalse(authWrong.IsAuthenticated);

        // Wrong workspace fails scope check
        var otherWorkspaceScope = TestScopes.Create(workspaceId: "ws-other");
        var authOutOfScope = await service.AuthenticateApiKeyAsync(plaintext, otherWorkspaceScope);
        Assert.IsFalse(authOutOfScope.IsAuthenticated);

        // Disabled account blocks authentication and key issuance
        var disableResult = await service.SetServiceAccountEnabledAsync(owner, clientId, isEnabled: false, reason: "maintenance", expectedVersion: 2);
        Assert.IsTrue(disableResult.IsSuccess);
        Assert.IsFalse((await service.AuthenticateApiKeyAsync(plaintext, fixture.Scope)).IsAuthenticated);

        var issueDisabled = await service.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(clientId, "blocked"));
        Assert.IsTrue(issueDisabled.TryGetValue(out TicketFailure disabledFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, disabledFailure.Kind);

        // Re-enable restores authentication
        Assert.IsTrue((await service.SetServiceAccountEnabledAsync(owner, clientId, isEnabled: true, expectedVersion: 3)).IsSuccess);
        Assert.IsTrue((await service.AuthenticateApiKeyAsync(plaintext, fixture.Scope)).IsAuthenticated);

        // Expiration blocks authentication
        fixture.Clock.Advance(TimeSpan.FromHours(3));
        Assert.IsFalse((await service.AuthenticateApiKeyAsync(plaintext, fixture.Scope)).IsAuthenticated);
    }

    [TestMethod]
    public async Task DeletingServiceAccountCascadesKeyAndGrantRevocation()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var clientId = new ApiClientId("cascade-bot");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(clientId, fixture.Partition, TenantGrantScope.Instance, "Cascade Bot"))).IsSuccess);

        var keyResult = await service.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(clientId, "key-1"));
        Assert.IsTrue(keyResult.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> issued));

        var otherWorkspace = new WorkspaceId("ws-second");
        await fixture.GrantAsync(clientId, BuiltInRoles.Agent, fixture.Workspace);
        await fixture.GrantAsync(clientId, BuiltInRoles.Viewer, new WorkspaceGrantScope(otherWorkspace));

        var deleteResult = await service.DeleteServiceAccountAsync(owner, clientId);
        Assert.IsTrue(deleteResult.IsSuccess);

        Assert.IsFalse((await service.AuthenticateApiKeyAsync(issued.Value.Value.PlaintextKey, fixture.Scope)).IsAuthenticated);
        Assert.IsEmpty(await fixture.Grants.ListForSubjectsAsync(fixture.Partition, fixture.Scope.WorkspaceId, [clientId]));
        Assert.IsEmpty(await fixture.Grants.ListForSubjectsAsync(fixture.Partition, otherWorkspace, [clientId]));
    }

    [TestMethod]
    public async Task ServiceAccountAdministrationEnforcesRbacScopeContainmentAndAntiBreakGlass()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var viewer = fixture.User("viewer");
        await fixture.GrantAsync(viewer.UserId, BuiltInRoles.Viewer, fixture.Workspace);
        var viewerCreate = await service.CreateServiceAccountAsync(viewer, new ServiceAccount(new ApiClientId("sa-1"), fixture.Partition, fixture.Workspace, "SA 1"));
        Assert.IsTrue(viewerCreate.TryGetValue(out TicketFailure viewerFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, viewerFailure.Kind);

        // Workspace admin cannot create tenant-scoped service account or another workspace's service account
        var wsAdmin = fixture.User("ws-admin");
        await fixture.GrantAsync(wsAdmin.UserId, BuiltInRoles.Admin, fixture.Workspace);
        var tenantSaAttempt = await service.CreateServiceAccountAsync(wsAdmin, new ServiceAccount(new ApiClientId("sa-tenant"), fixture.Partition, TenantGrantScope.Instance, "Tenant SA"));
        Assert.IsTrue(tenantSaAttempt.TryGetValue(out TicketFailure tenantFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, tenantFailure.Kind);

        var otherWsAttempt = await service.CreateServiceAccountAsync(wsAdmin, new ServiceAccount(new ApiClientId("sa-other-ws"), fixture.Partition, new WorkspaceGrantScope(new WorkspaceId("ws-other")), "Other WS SA"));
        Assert.IsTrue(otherWsAttempt.TryGetValue(out TicketFailure otherWsFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, otherWsFailure.Kind);
        Assert.AreEqual("out-of-scope", otherWsFailure.Code);

        // Break-glass grant holding AccessManage cannot manage service accounts
        var bgUser = fixture.User("bg-user");
        var bgRole = await fixture.CustomRoleAsync("bg-admin", AccessPermissions.AccessManage);
        var now = fixture.Clock.GetUtcNow();
        await fixture.SeedAsync(new AccessGrant(
            new GrantId("bg-grant"),
            fixture.Partition,
            bgUser.UserId,
            bgRole.Id,
            fixture.Workspace,
            GrantEffect.Allow,
            validFrom: now,
            validUntil: now.AddHours(1),
            justification: "Emergency incident response",
            kind: GrantKind.BreakGlass));

        var bgAttempt = await service.CreateServiceAccountAsync(bgUser, new ServiceAccount(new ApiClientId("sa-bg"), fixture.Partition, fixture.Workspace, "BG SA"));
        Assert.IsTrue(bgAttempt.TryGetValue(out TicketFailure bgFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, bgFailure.Kind);
        Assert.AreEqual("break-glass-not-allowed", bgFailure.Code);
    }

    [TestMethod]
    public async Task EvaluatorPlannerAndRevealImmediatelyDenyDisabledOrOutOfScopeServiceAccounts()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var clientId = new ApiClientId("team-bot");
        var teamA = new TeamId("team-a");
        var teamB = new TeamId("team-b");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(clientId, fixture.Partition, new TeamGrantScope(fixture.Scope.WorkspaceId, teamA), "Team A Bot"))).IsSuccess);
        await fixture.GrantAsync(clientId, BuiltInRoles.Owner, fixture.Workspace);

        var field = new FieldPath("contact.email");
        await fixture.Policies.CreateAsync(fixture.Scope, new AccessFieldPolicy(field, [AccessPermissions.ContactRead], revealPermission: AccessPermissions.ContactReveal));

        var bot = fixture.Client(clientId.Value);

        // Permitted on team A, denied on team B (out-of-scope)
        Assert.AreEqual("allowed", await fixture.DescribeAsync(bot, AccessPermissions.ConversationRead, fixture.Resource(team: "team-a")));
        Assert.AreEqual("denied:ScopeMismatch", await fixture.DescribeAsync(bot, AccessPermissions.ConversationRead, fixture.Resource(team: "team-b")));

        // Batch decision context enforces team scope too
        var batch = await fixture.Evaluator.LoadDecisionContextAsync(bot, fixture.Scope, [AccessPermissions.ConversationRead]);
        Assert.IsTrue((await batch.DecideAsync(AccessPermissions.ConversationRead, fixture.Resource(team: "team-a"))).IsPermitted);
        Assert.AreEqual("denied:ScopeMismatch", AccessFixture.Describe(await batch.DecideAsync(AccessPermissions.ConversationRead, fixture.Resource(team: "team-b"))));

        // Query planner intersects with service account's team scope
        var plan = await fixture.Planner.PlanAsync(new AccessQuery(bot, AccessPermissions.ConversationRead, fixture.Scope));
        Assert.IsTrue(AccessFilterEvaluator.Matches(plan, fixture.Resource(team: "team-a")));
        Assert.IsFalse(AccessFilterEvaluator.Matches(plan, fixture.Resource(team: "team-b")));

        // Reveal succeeds while enabled on team A
        var revealOk = await fixture.Evaluator.RevealAsync(new RevealRequest(bot, fixture.Resource(team: "team-a"), AccessPermissions.ContactRead, field));
        Assert.IsInstanceOfType<RevealGranted>(revealOk.Value);

        // Disabling immediately denies point evaluation, batch context, planner, and reveal
        Assert.IsTrue((await service.SetServiceAccountEnabledAsync(owner, clientId, isEnabled: false, reason: "compromised")).IsSuccess);
        Assert.AreEqual("denied:ServiceAccountDisabled", await fixture.DescribeAsync(bot, AccessPermissions.ConversationRead, fixture.Resource(team: "team-a")));
        Assert.AreSame(DenyAllFilter.Instance, await fixture.Planner.PlanAsync(new AccessQuery(bot, AccessPermissions.ConversationRead, fixture.Scope)));

        var revealDenied = await fixture.Evaluator.RevealAsync(new RevealRequest(bot, fixture.Resource(team: "team-a"), AccessPermissions.ContactRead, field));
        Assert.IsInstanceOfType<RevealRefused>(revealDenied.Value);
        Assert.AreEqual(RevealRefusal.ReadDenied, ((RevealRefused)revealDenied.Value!).Reason);

        var disabledBatch = await fixture.Evaluator.LoadDecisionContextAsync(bot, fixture.Scope, [AccessPermissions.ConversationRead]);
        Assert.AreEqual("denied:ServiceAccountDisabled", AccessFixture.Describe(await disabledBatch.DecideAsync(AccessPermissions.ConversationRead, fixture.Resource(team: "team-a"))));
    }

    [TestMethod]
    public async Task CreateServiceAccountRejectsReservedWorkerIdsAndConflictingPreexistingGrants()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        foreach (var reserved in new[] { "automation-worker", "sla-worker", "channels", "ticketflow-routing" })
        {
            var res = await service.CreateServiceAccountAsync(owner, new ServiceAccount(new ApiClientId(reserved), fixture.Partition, fixture.Workspace, "Reserved"));
            Assert.IsTrue(res.TryGetValue(out TicketFailure failure));
            Assert.AreEqual(TicketFailureKind.Rejected, failure.Kind);
            Assert.AreEqual("reserved-client-id", failure.Code);
        }

        // Pre-existing grant outside requested SA scope -> uniform non-enumerating Conflict
        var outOfScopeClient = new ApiClientId("pre-tenant-bot");
        await fixture.GrantAsync(outOfScopeClient, BuiltInRoles.Viewer, TenantGrantScope.Instance);
        var outOfScopeCreate = await service.CreateServiceAccountAsync(owner, new ServiceAccount(outOfScopeClient, fixture.Partition, fixture.Workspace, "Bot"));
        Assert.IsTrue(outOfScopeCreate.TryGetValue(out TicketFailure outOfScopeFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, outOfScopeFailure.Kind);
        Assert.AreEqual("service-account-conflict", outOfScopeFailure.Code);
        Assert.IsNull(outOfScopeFailure.CurrentRevision);

        // Pre-existing Deny grant -> uniform Conflict
        var deniedClient = new ApiClientId("pre-denied-bot");
        await fixture.GrantAsync(deniedClient, BuiltInRoles.Viewer, fixture.Workspace, GrantEffect.Deny);
        var deniedCreate = await service.CreateServiceAccountAsync(owner, new ServiceAccount(deniedClient, fixture.Partition, fixture.Workspace, "Denied Bot"));
        Assert.IsTrue(deniedCreate.TryGetValue(out TicketFailure denyFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, denyFailure.Kind);
        Assert.AreEqual("service-account-conflict", denyFailure.Code);

        // Pre-existing Allow grant conferring permissions actor lacks -> uniform Conflict
        var wsAdmin = fixture.User("ws-admin");
        await fixture.GrantAsync(wsAdmin.UserId, BuiltInRoles.Admin, fixture.Workspace);
        var highPrivClient = new ApiClientId("pre-owner-bot");
        await fixture.GrantAsync(highPrivClient, BuiltInRoles.Owner, fixture.Workspace);
        var escCreate = await service.CreateServiceAccountAsync(wsAdmin, new ServiceAccount(highPrivClient, fixture.Partition, fixture.Workspace, "High Priv Bot"));
        Assert.IsTrue(escCreate.TryGetValue(out TicketFailure escFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, escFailure.Kind);
        Assert.AreEqual("service-account-conflict", escFailure.Code);

        // Pre-existing unresolvable role grant -> uniform Conflict without echoing role name
        var ghostRoleClient = new ApiClientId("pre-ghost-role-bot");
        await fixture.SeedAsync(new AccessGrant(new GrantId("g-ghost-role"), fixture.Partition, ghostRoleClient, new RoleId("secret-deleted-role"), fixture.Workspace));
        var ghostCreate = await service.CreateServiceAccountAsync(wsAdmin, new ServiceAccount(ghostRoleClient, fixture.Partition, fixture.Workspace, "Ghost Role Bot"));
        Assert.IsTrue(ghostCreate.TryGetValue(out TicketFailure ghostFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, ghostFailure.Kind);
        Assert.AreEqual("service-account-conflict", ghostFailure.Code);
        Assert.IsFalse(ghostFailure.Errors.Any(e => e.Contains("secret-deleted-role", StringComparison.Ordinal)),
            "CreateServiceAccountAsync must not echo unresolvable role names.");
    }

    [TestMethod]
    public async Task IssueApiKeyAndEnableEnforceAntiEscalationAndDeleteProtectsOutOfScopeAndDenyGrants()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var wsAdmin = fixture.User("ws-admin");
        await fixture.GrantAsync(wsAdmin.UserId, BuiltInRoles.Admin, fixture.Workspace);

        // Workspace-scoped SA that holds Owner grant (more than wsAdmin holds!)
        var botId = new ApiClientId("high-priv-sa");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(botId, fixture.Partition, fixture.Workspace, "High Priv SA"))).IsSuccess);
        await fixture.GrantAsync(botId, BuiltInRoles.Owner, fixture.Workspace);

        // wsAdmin cannot issue an API key for a SA holding permissions wsAdmin lacks, and individual unheld permission names are not leaked
        var issueEsc = await service.IssueApiKeyAsync(wsAdmin, new ApiKeyCreateRequest(botId, "hijack"));
        Assert.IsTrue(issueEsc.TryGetValue(out TicketFailure issueFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, issueFailure.Kind);
        Assert.AreEqual("privilege-escalation", issueFailure.Code);
        Assert.IsFalse(issueFailure.Errors.Any(e => e.Contains("workspace:manage", StringComparison.Ordinal) || e.Contains("access:manage-tenant", StringComparison.Ordinal)),
            "CheckActorCanConferClientGrantsAsync must not leak individual unheld permission names.");

        // wsAdmin CAN disable a compromised SA, but CANNOT re-enable it (anti-escalation)
        Assert.IsTrue((await service.SetServiceAccountEnabledAsync(wsAdmin, botId, isEnabled: false, reason: "incident")).IsSuccess);
        var enableEsc = await service.SetServiceAccountEnabledAsync(wsAdmin, botId, isEnabled: true);
        Assert.IsTrue(enableEsc.TryGetValue(out TicketFailure enableFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, enableFailure.Kind);
        Assert.AreEqual("privilege-escalation", enableFailure.Code);

        // Delete refuses with uniform sanitized Forbidden when SA has an active grant outside SA scope or unrevocable deny grant
        var crossScopeBot = new ApiClientId("cross-scope-sa");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(crossScopeBot, fixture.Partition, fixture.Workspace, "Cross Scope SA"))).IsSuccess);
        await fixture.GrantAsync(crossScopeBot, BuiltInRoles.Viewer, TenantGrantScope.Instance);
        var deleteCross = await service.DeleteServiceAccountAsync(wsAdmin, crossScopeBot);
        Assert.IsTrue(deleteCross.TryGetValue(out TicketFailure crossFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, crossFailure.Kind);
        Assert.AreEqual("forbidden", crossFailure.Code);
        Assert.IsNotEmpty(await fixture.Grants.ListAllForSubjectAsync(fixture.Partition, crossScopeBot, fixture.Scope.WorkspaceId));

        // Delete refuses with identical sanitized Forbidden when SA has an active Deny grant whose permissions actor cannot confer/revoke
        var denyBot = new ApiClientId("deny-protected-sa");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(denyBot, fixture.Partition, fixture.Workspace, "Deny Protected SA"))).IsSuccess);
        await fixture.GrantAsync(denyBot, BuiltInRoles.Owner, fixture.Workspace, GrantEffect.Deny);
        var deleteDeny = await service.DeleteServiceAccountAsync(wsAdmin, denyBot);
        Assert.IsTrue(deleteDeny.TryGetValue(out TicketFailure denyDelFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, denyDelFailure.Kind);
        Assert.AreEqual("forbidden", denyDelFailure.Code);
        CollectionAssert.AreEqual(crossFailure.Errors.ToArray(), denyDelFailure.Errors.ToArray());

        // Owner CAN delete and grant revocations are audited
        var auditBefore = fixture.Audit.Snapshot().Count;
        Assert.IsTrue((await service.DeleteServiceAccountAsync(owner, denyBot)).IsSuccess);
        var auditAfter = fixture.Audit.Snapshot().Skip(auditBefore).ToArray();
        Assert.IsTrue(auditAfter.Any(e => e.Kind == AccessAuditKind.GrantRevoked && e.Outcome == AccessAuditOutcome.Allowed));
        Assert.IsTrue(auditAfter.Any(e => e.Kind == AccessAuditKind.ServiceAccountDeleted && e.Outcome == AccessAuditOutcome.Allowed));
    }

    [TestMethod]
    public async Task IssueApiKeyToctouImmediatelyRevokesKeyWhenAccountDisabledConcurrently()
    {
        using var fixture = AccessFixture.Create();
        var saStore = fixture.Provider.GetRequiredService<IServiceAccountStore>();
        var innerKeyStore = fixture.Provider.GetRequiredService<IApiKeyStore>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var clientId = new ApiClientId("race-bot");
        Assert.IsTrue((await saStore.CreateAsync(new ServiceAccount(clientId, fixture.Partition, fixture.Workspace, "Race Bot"), "user:owner")).IsSuccess);

        var racingKeyStore = new RacingApiKeyStore(innerKeyStore, async () =>
        {
            // Simulate concurrent disable between FindAsync and post-CreateAsync verification
            await saStore.SetEnabledAsync(fixture.Partition, clientId, isEnabled: false, "user:other", "disabled mid-flight");
        });

        var service = new ServiceAccountService(
            saStore,
            racingKeyStore,
            fixture.Grants,
            fixture.Roles,
            fixture.Catalog,
            fixture.Audit,
            fixture.Clock);

        var result = await service.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(clientId, "raced-key"));
        Assert.IsTrue(result.TryGetValue(out TicketFailure failure));
        Assert.AreEqual(TicketFailureKind.Conflict, failure.Kind);
        Assert.AreEqual("service-account-disabled", failure.Code);

        // Verify the just-created key was immediately revoked in the store
        var storedKeys = await innerKeyStore.ListForClientAsync(fixture.Partition, clientId, includeInactive: true);
        Assert.HasCount(1, storedKeys);
        Assert.IsTrue(storedKeys[0].IsRevoked, "Key created during concurrent disable must be immediately revoked.");
    }

    [TestMethod]
    public async Task ExplicitDenyBlocksServiceAccountAdministrationAndOutOfScopeServiceAccountBlockedFromAdmin()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var deniedAdmin = fixture.User("denied-tenant-admin");
        await fixture.GrantAsync(deniedAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var denyRole = await fixture.CustomRoleAsync("deny-access-manage", AccessPermissions.AccessManage);
        await fixture.GrantAsync(deniedAdmin.UserId, denyRole, fixture.Workspace, GrantEffect.Deny);

        // Explicit deny on AccessManage at workspace scope MUST NOT fall through to AccessManageTenant
        var createRes = await service.CreateServiceAccountAsync(deniedAdmin, new ServiceAccount(new ApiClientId("blocked-sa"), fixture.Partition, fixture.Workspace, "Blocked"));
        Assert.IsTrue(createRes.TryGetValue(out TicketFailure denyFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, denyFailure.Kind);
        Assert.AreEqual(nameof(AccessDenialReason.ExplicitDeny), denyFailure.Code);

        // Workspace-scoped service account holding tenant Owner grant still cannot create tenant-scoped grant or SA
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var wsSaId = new ApiClientId("ws-scoped-admin-sa");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(wsSaId, fixture.Partition, fixture.Workspace, "WS SA"))).IsSuccess);
        await fixture.GrantAsync(wsSaId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var wsSaPrincipal = fixture.Client(wsSaId.Value);
        var tenantGrant = new AccessGrant(new GrantId("sa-tenant-grant"), fixture.Partition, new UserId("u1"), BuiltInRoles.Viewer.Id, TenantGrantScope.Instance);
        Assert.AreEqual("refused:Forbidden", AccessFixture.Describe(await fixture.Admin.CreateGrantAsync(wsSaPrincipal, tenantGrant)));

        var tenantSaAttempt = await service.CreateServiceAccountAsync(wsSaPrincipal, new ServiceAccount(new ApiClientId("escalated-tenant-sa"), fixture.Partition, TenantGrantScope.Instance, "Esc"));
        Assert.IsTrue(tenantSaAttempt.TryGetValue(out TicketFailure saScopeFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, saScopeFailure.Kind);
    }

    [TestMethod]
    public async Task CreateServiceAccountRejectsExistingActiveApiKeysAndDeleteCascadeIgnoresPostCommitCancellation()
    {
        using var fixture = AccessFixture.Create();
        var saStore = fixture.Provider.GetRequiredService<IServiceAccountStore>();
        var keyStore = fixture.Provider.GetRequiredService<IApiKeyStore>();
        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        // Orphaned active API key blocks recreating a service account with the same ClientId
        var orphanClient = new ApiClientId("orphan-key-bot");
        var keyCreate = await keyStore.CreateAsync(
            ApiKeyId.NewId(),
            fixture.Partition,
            orphanClient,
            "orphaned",
            "tf_sa_deadbeef",
            new string('a', 64),
            "system:seed");
        Assert.IsTrue(keyCreate.IsSuccess);

        var conflictCreate = await service.CreateServiceAccountAsync(owner, new ServiceAccount(orphanClient, fixture.Partition, fixture.Workspace, "Orphan Bot"));
        Assert.IsTrue(conflictCreate.TryGetValue(out TicketFailure conflictFailure));
        Assert.AreEqual(TicketFailureKind.Conflict, conflictFailure.Kind);
        Assert.AreEqual("service-account-conflict", conflictFailure.Code);

        // Once the orphaned key is revoked, creation succeeds
        Assert.AreEqual(1, await keyStore.RevokeAllForClientAsync(fixture.Partition, orphanClient, "system:cleanup", "cleanup"));
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(orphanClient, fixture.Partition, fixture.Workspace, "Orphan Bot"))).IsSuccess);

        // Post-delete key/grant cascade completes even if caller's CancellationToken is cancelled right as DeleteAsync commits
        var cascadeClient = new ApiClientId("cancel-cascade-bot");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(cascadeClient, fixture.Partition, fixture.Workspace, "Cascade Bot"))).IsSuccess);
        var issuedKey = await service.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(cascadeClient, "k1"));
        Assert.IsTrue(issuedKey.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> issued));
        await fixture.GrantAsync(cascadeClient, BuiltInRoles.Viewer, fixture.Workspace);

        // Verify PrintMembers redacts PlaintextKey and KeyHashHex
        var issuedText = issued.Value.Value.ToString();
        Assert.IsFalse(issuedText.Contains(issued.Value.Value.PlaintextKey, StringComparison.Ordinal));
        StringAssert.Contains(issuedText, "<redacted>", StringComparison.Ordinal);
        var secretRecord = new StoredApiKeySecret(issued.Value.Value.Key, new string('f', 64));
        var secretText = secretRecord.ToString();
        Assert.IsFalse(secretText.Contains(secretRecord.KeyHashHex, StringComparison.Ordinal));
        StringAssert.Contains(secretText, "<redacted>", StringComparison.Ordinal);

        using var cts = new CancellationTokenSource();
        var cancellingStore = new CancellingOnDeleteServiceAccountStore(saStore, cts);
        var cancelService = new ServiceAccountService(cancellingStore, keyStore, fixture.Grants, fixture.Roles, fixture.Catalog, fixture.Audit, fixture.Clock);

        var deleted = await cancelService.DeleteServiceAccountAsync(owner, cascadeClient, expectedVersion: 1, cts.Token);
        Assert.IsTrue(deleted.IsSuccess);
        Assert.IsFalse((await cancelService.AuthenticateApiKeyAsync(issued.Value.Value.PlaintextKey, fixture.Scope)).IsAuthenticated);
        Assert.IsEmpty(await fixture.Grants.ListAllForSubjectAsync(fixture.Partition, cascadeClient, fixture.Scope.WorkspaceId));
    }

    [TestMethod]
    public async Task AccessAdministratorTenantChecksEnforceTenantAllowsOnlyAndStandardGrantsOnly()
    {
        using var fixture = AccessFixture.Create();
        var tenantOwner = fixture.User("tenant-owner");
        await fixture.GrantAsync(tenantOwner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var workspaceOwner = fixture.User("workspace-owner");
        await fixture.GrantAsync(workspaceOwner.UserId, BuiltInRoles.Owner, fixture.Workspace);

        var bgTenantUser = fixture.User("bg-tenant");
        var bgRole = await fixture.CustomRoleAsync("bg-tenant-role", AccessPermissions.AccessManageTenant);
        var now = fixture.Clock.GetUtcNow();
        await fixture.SeedAsync(new AccessGrant(
            new GrantId("g-bg-tenant"),
            fixture.Partition,
            bgTenantUser.UserId,
            bgRole.Id,
            fixture.Workspace,
            GrantEffect.Allow,
            validFrom: now,
            validUntil: now.AddHours(1),
            justification: "Emergency test",
            kind: GrantKind.BreakGlass));

        var saClient = new ApiClientId("sa-tenant-subject");
        await fixture.GrantAsync(saClient, BuiltInRoles.Viewer, TenantGrantScope.Instance);
        await fixture.GrantAsync(saClient, BuiltInRoles.Agent, fixture.Workspace);

        Assert.IsInstanceOfType<AccessCheckPassed>((await fixture.Admin.CheckCanManageTenantAsync(tenantOwner)).Value);
        Assert.IsInstanceOfType<TicketFailure>((await fixture.Admin.CheckCanManageTenantAsync(workspaceOwner)).Value);
        Assert.IsInstanceOfType<TicketFailure>((await fixture.Admin.CheckCanManageTenantAsync(bgTenantUser)).Value);

        var listedByTenant = await fixture.Admin.ListTenantGrantsForSubjectsAsync(tenantOwner, [saClient]);
        Assert.IsTrue(listedByTenant.TryGetValue(out TicketValue<IReadOnlyList<StoredAccessGrant>> ok));
        Assert.HasCount(1, ok.Value);
        Assert.IsInstanceOfType<TenantGrantScope>(ok.Value[0].Grant.Scope);

        var listedByWorkspace = await fixture.Admin.ListTenantGrantsForSubjectsAsync(workspaceOwner, [saClient]);
        Assert.IsTrue(listedByWorkspace.TryGetValue(out TicketFailure wsFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsFailure.Kind);

        var listedByBg = await fixture.Admin.ListTenantGrantsForSubjectsAsync(bgTenantUser, [saClient]);
        Assert.IsTrue(listedByBg.TryGetValue(out TicketFailure bgFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, bgFailure.Kind);
    }

    [TestMethod]
    public async Task CrossScopeAndUnauthorizedServiceAccountOperationsAreOpaqueAndDoNotExfiltrateAuditMetadata()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var wsAdmin = fixture.User("ws-admin");
        await fixture.GrantAsync(wsAdmin.UserId, BuiltInRoles.Admin, fixture.Workspace);

        var otherWs = new WorkspaceId("ws-other");
        var otherOwner = new UserPrincipal(fixture.Partition.InWorkspace(otherWs), new UserId("owner"));
        var otherClient = new ApiClientId("sa-other-ws-secret");
        Assert.IsTrue((await service.CreateServiceAccountAsync(otherOwner, new ServiceAccount(otherClient, fixture.Partition, new TeamGrantScope(otherWs, new TeamId("other-team")), "Other WS"))).IsSuccess);
        Assert.IsTrue((await service.UpdateServiceAccountAsync(otherOwner, otherClient, "Other WS v2", expectedVersion: 1)).IsSuccess);
        var otherKeyRes = await service.IssueApiKeyAsync(otherOwner, new ApiKeyCreateRequest(otherClient, "k-other"));
        Assert.IsTrue(otherKeyRes.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> otherKey));

        var tenantClient = new ApiClientId("sa-tenant-secret");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(tenantClient, fixture.Partition, TenantGrantScope.Instance, "Tenant SA"))).IsSuccess);
        var tenantKeyRes = await service.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(tenantClient, "k-tenant"));
        Assert.IsTrue(tenantKeyRes.TryGetValue(out TicketValue<AccessChangeApplied<IssuedApiKey>> tenantKey));

        var sameWsClient = new ApiClientId("sa-same-ws");
        Assert.IsTrue((await service.CreateServiceAccountAsync(wsAdmin, new ServiceAccount(sameWsClient, fixture.Partition, fixture.Workspace, "Same WS"))).IsSuccess);

        // 1. CreateServiceAccountAsync collapses same-scope, other-workspace, and tenant existing accounts into identical uniform Conflict without leaking version or scope in audit.
        var createAuditBefore = fixture.Audit.Snapshot().Count;
        foreach (var probeId in new[] { otherClient, tenantClient, sameWsClient })
        {
            var attempt = await service.CreateServiceAccountAsync(wsAdmin, new ServiceAccount(probeId, fixture.Partition, fixture.Workspace, "Probe"));
            Assert.IsTrue(attempt.TryGetValue(out TicketFailure failure));
            Assert.AreEqual(TicketFailureKind.Conflict, failure.Kind, $"CreateServiceAccountAsync leaked cross-scope state for {probeId}.");
            Assert.AreEqual("service-account-conflict", failure.Code);
            Assert.IsNull(failure.CurrentRevision);
        }
        var createAudits = fixture.Audit.Snapshot().Skip(createAuditBefore).ToList();
        Assert.HasCount(3, createAudits);
        foreach (var entry in createAudits)
        {
            Assert.AreEqual(AccessPermissions.AccessManage, entry.Permission);
            Assert.IsNull(entry.TeamId);
            Assert.IsNull(entry.InboxId);
            Assert.IsNull(entry.DenialReason);
        }

        // 2. Single-ID lookup/update/enable/delete/issue-key/revoke-key return opaque NotFound for cross-workspace & unauthorized tenant records and never leak scope metadata into audit logs.
        var missingClient = new ApiClientId("sa-missing");
        var missingKey = ApiKeyId.NewId();

        Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.GetServiceAccountAsync(wsAdmin, otherClient)).Value!).Kind);
        Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.GetServiceAccountAsync(wsAdmin, missingClient)).Value!).Kind);
        Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.ListApiKeysAsync(wsAdmin, otherClient)).Value!).Kind);
        Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.ListApiKeysAsync(wsAdmin, missingClient)).Value!).Kind);

        var opAuditBefore = fixture.Audit.Snapshot().Count;
        foreach (var probeId in new[] { otherClient, tenantClient, missingClient })
        {
            Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.UpdateServiceAccountAsync(wsAdmin, probeId, "New Name", expectedVersion: 1)).Value!).Kind);
            Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.SetServiceAccountEnabledAsync(wsAdmin, probeId, false, expectedVersion: 1)).Value!).Kind);
            Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.DeleteServiceAccountAsync(wsAdmin, probeId, expectedVersion: 1)).Value!).Kind);
            Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.IssueApiKeyAsync(wsAdmin, new ApiKeyCreateRequest(probeId, "probe"))).Value!).Kind);
        }
        foreach (var probeKey in new[] { otherKey.Value.Value.Key.Id, tenantKey.Value.Value.Key.Id, missingKey })
            Assert.AreEqual(TicketFailureKind.NotFound, ((TicketFailure)(await service.RevokeApiKeyAsync(wsAdmin, probeKey, expectedVersion: 1)).Value!).Kind);

        var opAudits = fixture.Audit.Snapshot().Skip(opAuditBefore).ToList();
        Assert.HasCount(15, opAudits);
        foreach (var entry in opAudits)
        {
            Assert.AreEqual(AccessAuditOutcome.Denied, entry.Outcome);
            Assert.AreEqual(AccessPermissions.AccessManage, entry.Permission, $"Audit leaked permission '{entry.Permission}' for {entry.Kind} {entry.ResourceId}.");
            Assert.IsNull(entry.TeamId);
            Assert.IsNull(entry.InboxId);
            Assert.IsNull(entry.DenialReason, $"Audit leaked DenialReason '{entry.DenialReason}' for {entry.Kind} {entry.ResourceId}.");
        }
    }

    [TestMethod]
    public async Task AccessManageTenantOnlyPrincipalCannotDeleteServiceAccountWithActiveNonTenantBreakGlassGrant()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        // Tenant-only manager: holds tenant Owner MINUS access:manage in actor's workspace (only access:manage-tenant remains active).
        var tenantOnlyAdmin = fixture.User("tenant-only-admin");
        await fixture.GrantAsync(tenantOnlyAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var denyManageRole = await fixture.CustomRoleAsync("deny-access-manage-only", AccessPermissions.AccessManage);
        await fixture.GrantAsync(tenantOnlyAdmin.UserId, denyManageRole, fixture.Workspace, GrantEffect.Deny);

        // Confirm IAccessAdministrator refuses revoking a workspace break-glass grant by tenantOnlyAdmin.
        var tenantSa = new ApiClientId("sa-tenant-with-bg");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(tenantSa, fixture.Partition, TenantGrantScope.Instance, "Tenant SA"))).IsSuccess);
        var now = fixture.Clock.GetUtcNow();
        var bgGrantSameWs = new AccessGrant(
            new GrantId("g-sa-bg-same-ws"),
            fixture.Partition,
            tenantSa,
            BuiltInRoles.Supervisor.Id,
            fixture.Workspace,
            GrantEffect.Allow,
            validFrom: now,
            validUntil: now.AddHours(1),
            justification: "Emergency incident break-glass on service account",
            kind: GrantKind.BreakGlass);
        await fixture.SeedAsync(bgGrantSameWs);

        Assert.AreEqual("refused:NotFound", AccessFixture.Describe(await fixture.Admin.RevokeGrantAsync(tenantOnlyAdmin, bgGrantSameWs.Id, 1)));

        // DeleteServiceAccountAsync MUST also refuse deleting tenantSa and MUST NOT cascade-revoke the active break-glass grant!
        var deleteSameWs = await service.DeleteServiceAccountAsync(tenantOnlyAdmin, tenantSa);
        Assert.IsTrue(deleteSameWs.TryGetValue(out TicketFailure sameWsFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, sameWsFailure.Kind);
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, bgGrantSameWs.Id), "Active break-glass grant must not be cascade-revoked.");

        // Same protection for a tenant-scoped SA holding an active non-tenant break-glass grant in ANOTHER workspace.
        var tenantSaOtherWs = new ApiClientId("sa-tenant-with-other-ws-bg");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(tenantSaOtherWs, fixture.Partition, TenantGrantScope.Instance, "Tenant SA Other WS"))).IsSuccess);
        var bgGrantOtherWs = new AccessGrant(
            new GrantId("g-sa-bg-other-ws"),
            fixture.Partition,
            tenantSaOtherWs,
            BuiltInRoles.Supervisor.Id,
            new WorkspaceGrantScope(new WorkspaceId("ws-other")),
            GrantEffect.Allow,
            validFrom: now,
            validUntil: now.AddHours(1),
            justification: "Emergency incident break-glass in other workspace",
            kind: GrantKind.BreakGlass);
        await fixture.SeedAsync(bgGrantOtherWs);

        var deleteOtherWs = await service.DeleteServiceAccountAsync(tenantOnlyAdmin, tenantSaOtherWs);
        Assert.IsTrue(deleteOtherWs.TryGetValue(out TicketFailure otherWsFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, otherWsFailure.Kind);
        Assert.IsNotNull(await fixture.Grants.GetAsync(fixture.Partition, bgGrantOtherWs.Id), "Active cross-workspace break-glass grant must not be cascade-revoked.");

        // Full tenant owner (holding both access:manage and access:manage-tenant) CAN delete both service accounts.
        Assert.IsTrue((await service.DeleteServiceAccountAsync(owner, tenantSa)).IsSuccess);
        Assert.IsNull(await fixture.Grants.GetAsync(fixture.Partition, bgGrantSameWs.Id));
        Assert.IsTrue((await service.DeleteServiceAccountAsync(owner, tenantSaOtherWs)).IsSuccess);
        Assert.IsNull(await fixture.Grants.GetAsync(fixture.Partition, bgGrantOtherWs.Id));
    }

    [TestMethod]
    public async Task PrimaryServiceAccountAdminGatesEvaluateResourceConditionsConservatively()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var existingSa = new ApiClientId("sa-cond-target");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(existingSa, fixture.Partition, fixture.Workspace, "Target SA"))).IsSuccess);
        var teamScope = new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("other-team"));
        await fixture.GrantAsync(existingSa, BuiltInRoles.Viewer, teamScope);

        // Conditional Deny on AssigneeIsSelfCondition / TeamInPrincipalTeamsCondition must fail closed on base and scope admin gates.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("sa-deny-assignee", new AssigneeIsSelfCondition()),
            ("sa-deny-team", new TeamInPrincipalTeamsCondition()),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin, effect: GrantEffect.Deny, condition: condition);

            var list = await service.ListServiceAccountsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, name);

            var create = await service.CreateServiceAccountAsync(actor, new ServiceAccount(new ApiClientId($"sa-new-{name}"), fixture.Partition, fixture.Workspace, "New SA"));
            Assert.IsTrue(create.TryGetValue(out TicketFailure createFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, createFailure.Kind, name);

            var delete = await service.DeleteServiceAccountAsync(actor, existingSa);
            Assert.IsTrue(delete.TryGetValue(out TicketFailure deleteFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, deleteFailure.Kind, name);
        }

        // Conditional Deny on team grant target (CanManageGrantTarget) blocks cascade-revocation during DeleteServiceAccountAsync.
        var teamCondDenyActor = fixture.User("sa-team-cond-deny", "ops");
        await fixture.GrantAsync(teamCondDenyActor.UserId, BuiltInRoles.Admin);
        var denyManageRole = await fixture.CustomRoleAsync("sa-deny-manage", AccessPermissions.AccessManage);
        await fixture.GrantAsync(teamCondDenyActor.UserId, denyManageRole, teamScope, GrantEffect.Deny, condition: new TeamInPrincipalTeamsCondition());
        var deleteWithTeamDeny = await service.DeleteServiceAccountAsync(teamCondDenyActor, existingSa);
        Assert.IsTrue(deleteWithTeamDeny.TryGetValue(out TicketFailure teamDenyFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, teamDenyFailure.Kind);

        // Conditional Allow with Not(AssigneeIsSelfCondition) / Not(TeamInPrincipalTeamsCondition) must NOT authorize service account administration.
        foreach (var (name, condition) in new (string, AccessCondition)[]
        {
            ("sa-allow-not-assignee", new NotCondition(new AssigneeIsSelfCondition())),
            ("sa-allow-not-team", new NotCondition(new TeamInPrincipalTeamsCondition())),
        })
        {
            var actor = fixture.User(name, "ops");
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin, condition: condition);

            var list = await service.ListServiceAccountsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, name);

            var create = await service.CreateServiceAccountAsync(actor, new ServiceAccount(new ApiClientId($"sa-new-{name}"), fixture.Partition, fixture.Workspace, "New SA"));
            Assert.IsTrue(create.TryGetValue(out TicketFailure createFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, createFailure.Kind, name);
        }
    }

    private sealed class CancellingOnDeleteServiceAccountStore(IServiceAccountStore inner, CancellationTokenSource cts) : IServiceAccountStore
    {
        public ValueTask<StoredServiceAccount?> FindAsync(AccessPartition partition, ApiClientId clientId, CancellationToken cancellationToken = default) =>
            inner.FindAsync(partition, clientId, cancellationToken);
        public ValueTask<IReadOnlyList<StoredServiceAccount>> ListForPartitionAsync(AccessPartition partition, bool includeDisabled = true, CancellationToken cancellationToken = default) =>
            inner.ListForPartitionAsync(partition, includeDisabled, cancellationToken);
        public ValueTask<TicketResult<StoredServiceAccount>> CreateAsync(ServiceAccount account, string actorPrincipalId, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(account, actorPrincipalId, cancellationToken);
        public ValueTask<TicketResult<StoredServiceAccount>> UpdateAsync(ServiceAccount account, string actorPrincipalId, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(account, actorPrincipalId, expectedVersion, cancellationToken);
        public ValueTask<TicketResult<StoredServiceAccount>> SetEnabledAsync(AccessPartition partition, ApiClientId clientId, bool isEnabled, string actorPrincipalId, string? reason = null, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            inner.SetEnabledAsync(partition, clientId, isEnabled, actorPrincipalId, reason, expectedVersion, cancellationToken);
        public async ValueTask<TicketResult<bool>> DeleteAsync(AccessPartition partition, ApiClientId clientId, long? expectedVersion = null, CancellationToken cancellationToken = default)
        {
            var result = await inner.DeleteAsync(partition, clientId, expectedVersion, cancellationToken).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            return result;
        }
    }

    [TestMethod]
    public async Task SubScopeAndCrossWorkspaceDenyBlockServiceAccountAdministrationAndConferral()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var noDeleteRole = await fixture.CustomRoleAsync("sa-no-delete", AccessPermissions.ConversationDelete);
        var noManageRole = await fixture.CustomRoleAsync("sa-no-manage", AccessPermissions.AccessManage);
        var inboxVip = new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("vip"));
        var teamBilling = new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("billing"));

        // Seed a workspace SA holding Supervisor (includes ConversationDelete) and a pre-granted client ID.
        var wsSupSa = new ApiClientId("sa-ws-sup");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(wsSupSa, fixture.Partition, fixture.Workspace, "WS Sup SA"))).IsSuccess);
        await fixture.GrantAsync(wsSupSa, BuiltInRoles.Supervisor, fixture.Workspace);
        Assert.IsTrue((await service.SetServiceAccountEnabledAsync(owner, wsSupSa, isEnabled: false)).IsSuccess);

        var wsSupActiveSa = new ApiClientId("sa-ws-sup-active");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(wsSupActiveSa, fixture.Partition, fixture.Workspace, "WS Sup Active SA"))).IsSuccess);
        await fixture.GrantAsync(wsSupActiveSa, BuiltInRoles.Supervisor, fixture.Workspace);

        var orphanWsSup = new ApiClientId("sa-orphan-ws-sup");
        await fixture.GrantAsync(orphanWsSup, BuiltInRoles.Supervisor, fixture.Workspace);

        // 1. Workspace admin with sub-scope Deny(AccessManage) cannot create or list workspace service accounts.
        foreach (var (name, denyScope) in new (string, GrantScope)[]
        {
            ("sa-inbox-no-manage", inboxVip),
            ("sa-team-no-manage", teamBilling),
        })
        {
            var actor = fixture.User(name);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin);
            await fixture.GrantAsync(actor.UserId, noManageRole, denyScope, GrantEffect.Deny);

            var create = await service.CreateServiceAccountAsync(actor, new ServiceAccount(new ApiClientId($"sa-new-{name}"), fixture.Partition, fixture.Workspace, "New SA"));
            Assert.IsTrue(create.TryGetValue(out TicketFailure createFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, createFailure.Kind, name);

            var list = await service.ListServiceAccountsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, name);
        }

        // 2. Workspace admin with sub-scope Deny(ConversationDelete) cannot confer Supervisor via SA create, key issuance, or re-enable.
        foreach (var (name, denyScope) in new (string, GrantScope)[]
        {
            ("sa-inbox-no-delete", inboxVip),
            ("sa-team-no-delete", teamBilling),
        })
        {
            var actor = fixture.User(name);
            await fixture.GrantAsync(actor.UserId, BuiltInRoles.Admin);
            await fixture.GrantAsync(actor.UserId, noDeleteRole, denyScope, GrantEffect.Deny);

            var issue = await service.IssueApiKeyAsync(actor, new ApiKeyCreateRequest(wsSupActiveSa, "key"));
            Assert.IsTrue(issue.TryGetValue(out TicketFailure issueFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, issueFailure.Kind, name);

            var enable = await service.SetServiceAccountEnabledAsync(actor, wsSupSa, isEnabled: true);
            Assert.IsTrue(enable.TryGetValue(out TicketFailure enableFailure), name);
            Assert.AreEqual(TicketFailureKind.Forbidden, enableFailure.Kind, name);

            var createOrphan = await service.CreateServiceAccountAsync(actor, new ServiceAccount(orphanWsSup, fixture.Partition, fixture.Workspace, "Hijack"));
            Assert.IsTrue(createOrphan.TryGetValue(out TicketFailure orphanFailure), name);
            Assert.AreEqual(TicketFailureKind.Conflict, orphanFailure.Kind, name);
        }

        // 3. Tenant admin invoked from wsA with Deny(no-delete, Workspace(wsB)) cannot confer TenantGrantScope Supervisor via SA.
        var wsB = new WorkspaceId("ws-b");
        var wsBScope = fixture.Partition.InWorkspace(wsB);
        var noDeleteInWsB = new AccessRole(new RoleId("no-delete"), "No Delete in wsB", [AccessPermissions.ConversationDelete]);
        var noManageTenantInWsB = new AccessRole(new RoleId("no-manage-tenant"), "No Manage Tenant in wsB", [AccessPermissions.AccessManageTenant]);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await fixture.Roles.CreateAsync(wsBScope, noDeleteInWsB)).Value);
        Assert.IsInstanceOfType<StoreApplied<StoredAccessRole>>((await fixture.Roles.CreateAsync(wsBScope, noManageTenantInWsB)).Value);

        var tenantSupSa = new ApiClientId("sa-tenant-sup");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(tenantSupSa, fixture.Partition, TenantGrantScope.Instance, "Tenant Sup SA"))).IsSuccess);
        await fixture.GrantAsync(tenantSupSa, BuiltInRoles.Supervisor, TenantGrantScope.Instance);

        var tenantViewerSa = new ApiClientId("sa-tenant-viewer");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(tenantViewerSa, fixture.Partition, TenantGrantScope.Instance, "Tenant Viewer SA"))).IsSuccess);
        await fixture.GrantAsync(tenantViewerSa, BuiltInRoles.Viewer, TenantGrantScope.Instance);

        var tenantAdmin = fixture.User("sa-tenant-deny-wsb");
        await fixture.GrantAsync(tenantAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(tenantAdmin.UserId, noDeleteInWsB, new WorkspaceGrantScope(wsB), GrantEffect.Deny);

        var tenantIssueSup = await service.IssueApiKeyAsync(tenantAdmin, new ApiKeyCreateRequest(tenantSupSa, "escalated"));
        Assert.IsTrue(tenantIssueSup.TryGetValue(out TicketFailure tenantSupFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, tenantSupFailure.Kind);

        Assert.IsTrue((await service.IssueApiKeyAsync(tenantAdmin, new ApiKeyCreateRequest(tenantViewerSa, "viewer-key"))).IsSuccess);

        // Tenant admin invoked from wsA with Deny(AccessManageTenant, Workspace(wsB)) cannot create a TenantGrantScope SA.
        var blockedTenantAdmin = fixture.User("sa-tenant-deny-manage-wsb");
        await fixture.GrantAsync(blockedTenantAdmin.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        await fixture.GrantAsync(blockedTenantAdmin.UserId, noManageTenantInWsB, new WorkspaceGrantScope(wsB), GrantEffect.Deny);

        var createTenantSa = await service.CreateServiceAccountAsync(blockedTenantAdmin, new ServiceAccount(new ApiClientId("sa-t-blocked"), fixture.Partition, TenantGrantScope.Instance, "Blocked"));
        Assert.IsTrue(createTenantSa.TryGetValue(out TicketFailure blockedFailure));
        Assert.AreEqual(TicketFailureKind.Forbidden, blockedFailure.Kind);
    }

    [TestMethod]
    public async Task BaseAdminEnforcesServiceAccountScopeContainment()
    {
        using var fixture = AccessFixture.Create();
        using var scope = fixture.Provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

        var owner = fixture.User("owner");
        await fixture.GrantAsync(owner.UserId, BuiltInRoles.Owner, TenantGrantScope.Instance);

        var targetSa = new ApiClientId("sa-target-ws");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(targetSa, fixture.Partition, fixture.Workspace, "Target WS SA"))).IsSuccess);
        Assert.IsTrue((await service.IssueApiKeyAsync(owner, new ApiKeyCreateRequest(targetSa, "target-key"))).IsSuccess);

        var tenantTargetSa = new ApiClientId("sa-target-tenant");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(tenantTargetSa, fixture.Partition, TenantGrantScope.Instance, "Target Tenant SA"))).IsSuccess);

        // 1. Team-scoped and inbox-scoped service accounts holding workspace-level Admin/Owner grants MUST NOT pass workspace-wide base admin checks.
        var teamSaId = new ApiClientId("sa-team-scoped");
        var inboxSaId = new ApiClientId("sa-inbox-scoped");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(teamSaId, fixture.Partition, new TeamGrantScope(fixture.Scope.WorkspaceId, new TeamId("t1")), "Team SA"))).IsSuccess);
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(inboxSaId, fixture.Partition, new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i1")), "Inbox SA"))).IsSuccess);
        await fixture.GrantAsync(teamSaId, BuiltInRoles.Admin, fixture.Workspace);
        await fixture.GrantAsync(inboxSaId, BuiltInRoles.Admin, fixture.Workspace);

        foreach (var clientId in new[] { teamSaId, inboxSaId })
        {
            var actor = fixture.Client(clientId.Value);

            var list = await service.ListServiceAccountsAsync(actor);
            Assert.IsTrue(list.TryGetValue(out TicketFailure listFailure), clientId.Value);
            Assert.AreEqual(TicketFailureKind.Forbidden, listFailure.Kind, clientId.Value);

            var get = await service.GetServiceAccountAsync(actor, targetSa);
            Assert.IsTrue(get.TryGetValue(out TicketFailure getFailure), clientId.Value);
            Assert.AreEqual(TicketFailureKind.Forbidden, getFailure.Kind, clientId.Value);

            var keys = await service.ListApiKeysAsync(actor, targetSa);
            Assert.IsTrue(keys.TryGetValue(out TicketFailure keysFailure), clientId.Value);
            Assert.AreEqual(TicketFailureKind.Forbidden, keysFailure.Kind, clientId.Value);
        }

        // 2. Workspace-scoped service account CANNOT use AccessManageTenant fallback (nor manage tenant-scoped service accounts).
        var wsSaId = new ApiClientId("sa-ws-tenant-fallback");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(wsSaId, fixture.Partition, fixture.Workspace, "WS SA"))).IsSuccess);
        await fixture.GrantAsync(wsSaId, BuiltInRoles.Owner, TenantGrantScope.Instance);
        var denyManageRole = await fixture.CustomRoleAsync("sa-deny-manage", AccessPermissions.AccessManage);
        await fixture.GrantAsync(wsSaId, denyManageRole, fixture.Workspace, GrantEffect.Deny);

        var wsSaActor = fixture.Client(wsSaId.Value);
        Assert.IsTrue((await service.ListServiceAccountsAsync(wsSaActor)).TryGetValue(out TicketFailure wsListFail));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsListFail.Kind);
        Assert.IsTrue((await service.GetServiceAccountAsync(wsSaActor, targetSa)).TryGetValue(out TicketFailure wsGetFail));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsGetFail.Kind);
        Assert.IsTrue((await service.ListApiKeysAsync(wsSaActor, targetSa)).TryGetValue(out TicketFailure wsKeysFail));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsKeysFail.Kind);
        Assert.IsTrue((await service.CreateServiceAccountAsync(wsSaActor, new ServiceAccount(new ApiClientId("sa-ws-esc"), fixture.Partition, TenantGrantScope.Instance, "Esc"))).TryGetValue(out TicketFailure wsCreateTenantFail));
        Assert.AreEqual(TicketFailureKind.Forbidden, wsCreateTenantFail.Kind);

        // 3. Workspace-scoped SA with AccessManage CAN read workspace SAs/keys (but still cannot create tenant-scoped SAs); tenant-scoped SA with Owner CAN do both.
        var wsAdminSaId = new ApiClientId("sa-ws-admin");
        Assert.IsTrue((await service.CreateServiceAccountAsync(owner, new ServiceAccount(wsAdminSaId, fixture.Partition, fixture.Workspace, "WS Admin SA"))).IsSuccess);
        await fixture.GrantAsync(wsAdminSaId, BuiltInRoles.Admin, fixture.Workspace);
        var wsAdminActor = fixture.Client(wsAdminSaId.Value);
        Assert.IsTrue((await service.ListServiceAccountsAsync(wsAdminActor)).IsSuccess);
        Assert.IsTrue((await service.GetServiceAccountAsync(wsAdminActor, targetSa)).IsSuccess);
        Assert.IsTrue((await service.ListApiKeysAsync(wsAdminActor, targetSa)).IsSuccess);
        Assert.IsTrue((await service.UpdateServiceAccountAsync(wsAdminActor, tenantTargetSa, "Tamper")).TryGetValue(out TicketFailure wsTamperTenant));
        Assert.AreEqual(TicketFailureKind.NotFound, wsTamperTenant.Kind);
    }

    private sealed class RacingApiKeyStore(IApiKeyStore inner, Func<Task> onAfterCreate) : IApiKeyStore
    {
        public ValueTask<StoredApiKey?> FindByIdAsync(AccessPartition partition, ApiKeyId keyId, CancellationToken cancellationToken = default) =>
            inner.FindByIdAsync(partition, keyId, cancellationToken);

        public ValueTask<IReadOnlyList<StoredApiKeySecret>> FindByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default) =>
            inner.FindByPrefixAsync(keyPrefix, cancellationToken);

        public ValueTask<IReadOnlyList<StoredApiKey>> ListForClientAsync(AccessPartition partition, ApiClientId clientId, bool includeInactive = false, CancellationToken cancellationToken = default) =>
            inner.ListForClientAsync(partition, clientId, includeInactive, cancellationToken);

        public ValueTask<IReadOnlyList<StoredApiKey>> ListForPartitionAsync(AccessPartition partition, bool includeInactive = false, CancellationToken cancellationToken = default) =>
            inner.ListForPartitionAsync(partition, includeInactive, cancellationToken);

        public async ValueTask<TicketResult<StoredApiKey>> CreateAsync(ApiKeyId keyId, AccessPartition partition, ApiClientId clientId, string name, string keyPrefix, string keyHashHex, string actorPrincipalId, DateTimeOffset? expiresAtUtc = null, CancellationToken cancellationToken = default)
        {
            var created = await inner.CreateAsync(keyId, partition, clientId, name, keyPrefix, keyHashHex, actorPrincipalId, expiresAtUtc, cancellationToken);
            await onAfterCreate();
            return created;
        }

        public ValueTask<TicketResult<StoredApiKey>> RevokeAsync(AccessPartition partition, ApiKeyId keyId, string actorPrincipalId, string? reason = null, long? expectedVersion = null, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(partition, keyId, actorPrincipalId, reason, expectedVersion, cancellationToken);

        public ValueTask<int> RevokeAllForClientAsync(AccessPartition partition, ApiClientId clientId, string actorPrincipalId, string? reason = null, CancellationToken cancellationToken = default) =>
            inner.RevokeAllForClientAsync(partition, clientId, actorPrincipalId, reason, cancellationToken);

        public ValueTask RecordUsageAsync(AccessPartition partition, ApiKeyId keyId, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default) =>
            inner.RecordUsageAsync(partition, keyId, usedAtUtc, cancellationToken);
    }
}
