using TicketFlow.Core;

namespace TicketFlow.Testing;

/// <summary>Scope factories for tests, including one variant per scope level for isolation checks.</summary>
public static class TestScopes
{
    public static TicketScope Create(
        string tenantId = "tenant",
        string projectId = "project",
        string environmentId = "test",
        string appId = "app",
        string workspaceId = "workspace") =>
        new(tenantId, projectId, environmentId, appId, new WorkspaceId(workspaceId));

    /// <summary>Returns <paramref name="scope"/> followed by five scopes that each differ from it at exactly one level.</summary>
    public static IReadOnlyList<TicketScope> LevelVariants(TicketScope scope) =>
    [
        scope,
        new("other-tenant", scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId),
        new(scope.TenantId, "other-project", scope.EnvironmentId, scope.AppId, scope.WorkspaceId),
        new(scope.TenantId, scope.ProjectId, "other-environment", scope.AppId, scope.WorkspaceId),
        new(scope.TenantId, scope.ProjectId, scope.EnvironmentId, "other-app", scope.WorkspaceId),
        new(scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, new WorkspaceId("other-workspace")),
    ];

    /// <summary>A 256-code-unit identifier of varied (incompressible) characters for byte-limit tests.</summary>
    public static string LongIdentifier(int seed = 0) => new(Enumerable.Range(0, TicketScope.MaximumIdentifierLength)
        .Select(index => (char)(0x4e00 + ((long)seed * 313 + index * 137L) % 20992)).ToArray());
}
