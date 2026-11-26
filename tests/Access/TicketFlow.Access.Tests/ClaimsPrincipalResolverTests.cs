using System.Security.Claims;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Access.Tests;

[TestClass]
public sealed class ClaimsPrincipalResolverTests
{
    private static readonly TicketScope Scope = TestScopes.Create();

    [TestMethod]
    public async Task UnauthenticatedPrincipalReturnsNull()
    {
        var resolver = new ClaimsPrincipalResolver();
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")]));
        Assert.IsNull(await resolver.ResolveAsync(unauthenticated, Scope));
    }

    [TestMethod]
    public async Task ResolvesIdentityFlowUserClaimsWithTeamsExternalRolesAndDisplayName()
    {
        var resolver = new ClaimsPrincipalResolver();
        var claims = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-42"),
            new Claim("display_name", "Ada Lovelace"),
            new Claim("groups", "tier-1, tier-2"),
            new Claim("role", "support-supervisor"),
            new Claim(ClaimTypes.Role, "idflow-auditor, support-supervisor"),
        ], authenticationType: "IdentityFlow"));

        var resolved = await resolver.ResolveAsync(claims, Scope);
        Assert.IsInstanceOfType<UserPrincipal>(resolved);
        var user = (UserPrincipal)resolved;
        Assert.AreEqual(new UserId("user-42"), user.UserId);
        Assert.HasCount(2, user.Teams);
        Assert.IsTrue(user.Teams.Contains(new TeamId("tier-1")));
        Assert.IsTrue(user.Teams.Contains(new TeamId("tier-2")));
        Assert.HasCount(2, user.ExternalRoles);
        Assert.IsTrue(user.ExternalRoles.Contains("SUPPORT-SUPERVISOR"));
        Assert.IsTrue(user.ExternalRoles.Contains("idflow-auditor"));
        Assert.AreEqual("Ada Lovelace", resolver.TryGetDisplayName(claims));
    }

    [TestMethod]
    public async Task ResolvesIdentityFlowApiKeyAndOAuth2ClientCredentialsTokensAsApiClient()
    {
        var resolver = new ClaimsPrincipalResolver();

        // IdentityFlow primary_method = ApiKey with identityflow:client_id
        var idfApiKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("identityflow:client_id", "idf-sync"),
            new Claim("identityflow:primary_method", "ApiKey"),
            new Claim("role", "ingest-writer"),
        ], authenticationType: "Bearer"));
        var resolvedIdf = await resolver.ResolveAsync(idfApiKey, Scope);
        Assert.IsInstanceOfType<ApiClientPrincipal>(resolvedIdf);
        Assert.AreEqual(new ApiClientId("idf-sync"), ((ApiClientPrincipal)resolvedIdf).ClientId);
        Assert.IsTrue(resolvedIdf.ExternalRoles.Contains("ingest-writer"));

        // OAuth2 client_credentials via gty + azp
        var m2m = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "m2m-worker@clients"),
            new Claim("azp", "m2m-worker"),
            new Claim("gty", "client-credentials"),
            new Claim("roles", "automation-agent"),
        ], authenticationType: "Bearer"));
        var resolvedM2m = await resolver.ResolveAsync(m2m, Scope);
        Assert.IsInstanceOfType<ApiClientPrincipal>(resolvedM2m);
        Assert.AreEqual(new ApiClientId("m2m-worker"), ((ApiClientPrincipal)resolvedM2m).ClientId);
        Assert.IsTrue(resolvedM2m.ExternalRoles.Contains("automation-agent"));

        // OAuth2 grant_type = client_credentials with client_id
        var grantType = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "billing-bot"),
            new Claim("grant_type", "client_credentials"),
        ], authenticationType: "Bearer"));
        var resolvedGrantType = await resolver.ResolveAsync(grantType, Scope);
        Assert.IsInstanceOfType<ApiClientPrincipal>(resolvedGrantType);
        Assert.AreEqual(new ApiClientId("billing-bot"), ((ApiClientPrincipal)resolvedGrantType).ClientId);

        // Fails closed when principal_type=service_account lacks client_id
        var missingClientId = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "someone"),
            new Claim("principal_type", "service_account"),
        ], authenticationType: "ExternalIdentity"));
        Assert.IsNull(await resolver.ResolveAsync(missingClientId, Scope));
    }

    [TestMethod]
    public async Task SupportsCustomClaimTypesAndPrincipalExternalRoleEquality()
    {
        var options = new ClaimsPrincipalResolverOptions();
        options.SubjectClaimTypes.Insert(0, "custom_sub");
        options.RoleClaimTypes.Insert(0, "custom_role");

        var resolver = new ClaimsPrincipalResolver(Options.Create(options));
        var claims = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("custom_sub", "ext-7"),
            new Claim("custom_role", "ops-lead"),
        ], authenticationType: "Custom"));

        var resolved = await resolver.ResolveAsync(claims, Scope);
        Assert.IsInstanceOfType<UserPrincipal>(resolved);
        Assert.AreEqual(new UserId("ext-7"), ((UserPrincipal)resolved).UserId);
        Assert.IsTrue(resolved.ExternalRoles.Contains("ops-lead"));

        var u1 = new UserPrincipal(Scope, new UserId("ext-7"), externalRoles: ["ops-lead", "OPS-LEAD"]);
        var u2 = new UserPrincipal(Scope, new UserId("ext-7"), externalRoles: ["OPS-LEAD"]);
        Assert.AreEqual(u1, u2);
        Assert.AreEqual(u1.GetHashCode(), u2.GetHashCode());
        Assert.AreNotEqual(u1, new UserPrincipal(Scope, new UserId("ext-7")));
        Assert.AreEqual("UserPrincipal { <redacted> }", u1.ToString());

        var c1 = new ApiClientPrincipal(Scope, new ApiClientId("bot"), ["r1"]);
        var c2 = new ApiClientPrincipal(Scope, new ApiClientId("bot"), ["R1"]);
        Assert.AreEqual(c1, c2);
        Assert.AreEqual(c1.GetHashCode(), c2.GetHashCode());
        Assert.AreEqual("ApiClientPrincipal { <redacted> }", c1.ToString());
    }

    [TestMethod]
    public async Task HardensReservedWorkersConflictingClaimsScopeVerificationProviderAndNativeIdentityFlowApiKeys()
    {
        var resolver = new ClaimsPrincipalResolver();

        // Reserved worker IDs rejected
        foreach (var reserved in new[] { "automation-worker", "sla-worker", "channels", "ticketflow-routing" })
        {
            var spoof = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("client_id", reserved),
                new Claim("principal_type", "service_account"),
            ], authenticationType: "ExternalIdentity"));
            Assert.IsNull(await resolver.ResolveAsync(spoof, Scope), $"Reserved worker '{reserved}' must be rejected.");
        }

        // Conflicting multi-valued sub rejected
        var conflictingSub = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "alice"),
            new Claim("sub", "bob"),
        ], authenticationType: "Bearer"));
        Assert.IsNull(await resolver.ResolveAsync(conflictingSub, Scope));

        // Conflicting multi-valued client_id rejected
        var conflictingClient = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("client_id", "bot-1"),
            new Claim("client_id", "bot-2"),
        ], authenticationType: "Bearer"));
        Assert.IsNull(await resolver.ResolveAsync(conflictingClient, Scope));

        // Scope claim mismatch fails closed
        var wrongTenant = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "alice"),
            new Claim("tenant_id", "other-tenant"),
        ], authenticationType: "Bearer"));
        Assert.IsNull(await resolver.ResolveAsync(wrongTenant, Scope));

        var wrongWorkspace = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "alice"),
            new Claim("workspace_id", "other-workspace"),
        ], authenticationType: "Bearer"));
        Assert.IsNull(await resolver.ResolveAsync(wrongWorkspace, Scope));

        // Matching scope claims + IdentityProvider succeed
        var matching = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "alice"),
            new Claim("idp", "okta"),
            new Claim("tenant_id", Scope.TenantId),
            new Claim("project_id", Scope.ProjectId),
            new Claim("environment_id", Scope.EnvironmentId),
            new Claim("app_id", Scope.AppId),
            new Claim("workspace_id", Scope.WorkspaceId.Value),
        ], authenticationType: "Bearer"));
        var resolvedUser = Assert.IsInstanceOfType<UserPrincipal>(await resolver.ResolveAsync(matching, Scope));
        Assert.AreEqual("okta", resolvedUser.IdentityProvider);
        Assert.AreNotEqual(resolvedUser, new UserPrincipal(Scope, new UserId("alice"), identityProvider: "identityflow"));

        // Native IdentityFlow API key falls back to SubjectClaimTypes when ClientIdClaimTypes absent
        var nativeIdfKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "idf-key-subject"),
            new Claim("identityflow_auth_method", "ApiKey"),
            new Claim("iss", "https://id.example.test"),
        ], authenticationType: "IdentityFlow"));
        var resolvedKey = Assert.IsInstanceOfType<ApiClientPrincipal>(await resolver.ResolveAsync(nativeIdfKey, Scope));
        Assert.AreEqual(new ApiClientId("idf-key-subject"), resolvedKey.ClientId);
        Assert.AreEqual("https://id.example.test", resolvedKey.IdentityProvider);
    }
}
