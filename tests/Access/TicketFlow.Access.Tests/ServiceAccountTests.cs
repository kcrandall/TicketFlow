using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed partial class ServiceAccountTests
{
    [GeneratedRegex(@"^tf_sa_[0-9a-f]{8}\.[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
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
}
