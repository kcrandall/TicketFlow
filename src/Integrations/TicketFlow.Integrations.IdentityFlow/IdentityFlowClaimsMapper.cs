using System.Security.Claims;
using IdentityFlow.Hosting.AspNetCore.Sessions;

namespace TicketFlow.Integrations.IdentityFlow;

/// <summary>How IdentityFlow API-key authentications map to TicketFlow principals.</summary>
public enum IdentityFlowApiKeyMapping
{
    /// <summary>The key's identity becomes an <c>ApiClientPrincipal</c> (grants target the API client). Default.</summary>
    ApiClient = 1,

    /// <summary>The key acts as its owning user (<c>UserPrincipal</c>, team snapshot included).</summary>
    User = 2,
}

public enum IdentityFlowPrincipalKind
{
    User = 1,
    ApiClient = 2,
}

/// <summary>The TicketFlow identity derived from IdentityFlow claims: a user or API client identifier (prefix applied).</summary>
public sealed record IdentityFlowIdentity(IdentityFlowPrincipalKind Kind, string Id)
{
    public override string ToString() => $"IdentityFlowIdentity {{ Kind = {Kind} }}";
}

public sealed class IdentityFlowPrincipalOptions
{
    /// <summary>Default <see cref="IdentityFlowApiKeyMapping.ApiClient"/>.</summary>
    public IdentityFlowApiKeyMapping ApiKeys { get; set; } = IdentityFlowApiKeyMapping.ApiClient;

    /// <summary>Prefix added to IdentityFlow subjects to form <c>UserId</c>s (default none).</summary>
    public string UserIdPrefix { get; set; } = string.Empty;

    /// <summary>Prefix added to API-key identities and OAuth client identifiers to form <c>ApiClientId</c>s (default none).</summary>
    public string ApiClientIdPrefix { get; set; } = string.Empty;

    /// <summary>
    /// When non-empty, API keys, personal access tokens and bearer tokens must carry at least one of these <c>scope</c> values (browser
    /// sessions carry none and are always accepted).
    /// </summary>
    public ISet<string> RequiredScopes { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Query-string parameter naming the workspace of SignalR agent connections (default <c>workspace</c>).</summary>
    public string RealtimeWorkspaceParameter { get; set; } = "workspace";
}

/// <summary>
/// Maps IdentityFlow-authenticated users to TicketFlow identities using IdentityFlow's claim conventions:
/// <list type="bullet">
/// <item>API keys and personal access tokens: <c>identityflow_auth_method</c> (<c>ApiKey</c>, <c>PersonalAccessToken</c>) with the identity in
/// <see cref="ClaimTypes.NameIdentifier"/>; PATs are users, API keys follow <see cref="IdentityFlowPrincipalOptions.ApiKeys"/>.</item>
/// <item>Browser sessions and OpenIddict bearer tokens: <c>sub</c> (or <see cref="ClaimTypes.NameIdentifier"/>) → user.</item>
/// <item>Client-credentials tokens without a subject: <c>identityflow:client_id</c> (or <c>client_id</c>) → API client.</item>
/// </list>
/// GUID subjects are normalized to the "D" format so cookie (D) and API-key (N) authentications of one identity map to one user.
/// Unauthenticated users, missing subjects and unmet required scopes map to <see langword="null"/>.
/// </summary>
public static class IdentityFlowClaimsMapper
{
    /// <summary>IdentityFlow's authentication-method claim on API-key and PAT authentications.</summary>
    public const string AuthenticationMethodClaim = "identityflow_auth_method";

    public const string ApiKeyMethod = "ApiKey";
    public const string PersonalAccessTokenMethod = "PersonalAccessToken";
    public const string ScopeClaim = "scope";

    public static IdentityFlowIdentity? Map(ClaimsPrincipal? user, IdentityFlowPrincipalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (user?.Identity is not { IsAuthenticated: true })
            return null;
        var method = Single(user, AuthenticationMethodClaim);
        var session = Single(user, IdentityFlowClaimTypes.SessionId) is not null;
        if (!session && options.RequiredScopes.Count > 0 && !Scopes(user).Any(options.RequiredScopes.Contains))
            return null;
        var subject = Single(user, IdentityFlowClaimTypes.Subject) ?? Single(user, ClaimTypes.NameIdentifier);
        switch (method)
        {
            case ApiKeyMethod when subject is not null:
                return options.ApiKeys == IdentityFlowApiKeyMapping.User
                    ? Identity(IdentityFlowPrincipalKind.User, options.UserIdPrefix, subject)
                    : Identity(IdentityFlowPrincipalKind.ApiClient, options.ApiClientIdPrefix, subject);
            case ApiKeyMethod:
                return null;
        }
        if (subject is not null)
            return Identity(IdentityFlowPrincipalKind.User, options.UserIdPrefix, subject);
        var client = Single(user, IdentityFlowClaimTypes.ClientId) ?? Single(user, "client_id");
        return client is null ? null : Identity(IdentityFlowPrincipalKind.ApiClient, options.ApiClientIdPrefix, client);
    }

    private static IdentityFlowIdentity? Identity(IdentityFlowPrincipalKind kind, string prefix, string value)
    {
        var normalized = Guid.TryParse(value, out var guid) ? guid.ToString("D") : value;
        var id = prefix + normalized;
        return id.Length is 0 or > Core.TicketScope.MaximumIdentifierLength || id.Any(char.IsControl) || !System.Text.Unicode.Utf16.IsValid(id) ? null : new IdentityFlowIdentity(kind, id);
    }

    /// <summary>The single value of <paramref name="type"/>; several distinct values are ambiguous and ignored.</summary>
    private static string? Single(ClaimsPrincipal user, string type)
    {
        string? found = null;
        foreach (var claim in user.FindAll(type))
        {
            if (found is not null && found != claim.Value)
                return null;
            found = claim.Value;
        }
        return string.IsNullOrWhiteSpace(found) ? null : found;
    }

    private static IEnumerable<string> Scopes(ClaimsPrincipal user) =>
        user.FindAll(ScopeClaim).SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
