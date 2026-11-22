using TicketFlow.Core;

namespace TicketFlow.Testing;

/// <summary>Principal factories for tests. Every principal is created within an explicit or default test scope.</summary>
public static class TestPrincipals
{
    public static UserPrincipal User(TicketScope? scope = null, string userId = "user", params string[] teams) =>
        new(scope ?? TestScopes.Create(), new UserId(userId), teams.Select(team => new TeamId(team)));

    public static ApiClientPrincipal ApiClient(TicketScope? scope = null, string clientId = "api-client") =>
        new(scope ?? TestScopes.Create(), new ApiClientId(clientId));

    public static AutomationPrincipal Automation(TicketScope? scope = null, string automationId = "automation") =>
        new(scope ?? TestScopes.Create(), automationId);

    public static SystemPrincipal System(TicketScope? scope = null, string component = "system") =>
        new(scope ?? TestScopes.Create(), component);

    /// <summary>One principal of each union case, in declaration order.</summary>
    public static IReadOnlyList<PrincipalRef> AllKinds(TicketScope? scope = null) =>
        [User(scope), ApiClient(scope), Automation(scope), System(scope)];
}
