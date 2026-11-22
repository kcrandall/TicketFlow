using System.Security.Claims;
using IdentityFlow.Hosting.AspNetCore.Sessions;

namespace TicketFlow.Integrations.IdentityFlow;

/// <summary>How IdentityFlow API-key authentications map to TicketFlow principals.</summary>
public enum IdentityFlowApiKeyMapping
{
    /// <summary>The key becomes an <c>ApiClientPrincipal</c> (grants target the API client). Default.</summary>
    ApiClient = 1,

    /// <summary>The key acts as its owning user (<c>UserPrincipal</c>, team snapshot included).</summary>
    User = 2,
}

/// <summary>Which claim names the API client of an API-key authentication.</summary>
public enum IdentityFlowApiKeyClientSource
{
    /// <summary>The key's owning identity (<c>NameIdentifier</c>): every key of one identity is one API client. Default.</summary>
    Identity = 1,

    /// <summary>The key itself (<c>identityflow_method_id</c>): each key is its own API client with its own grants.</summary>
    Method = 2,
}

/// <summary>What happens to a user whose IdentityFlow subject has no staff identity link when an <c>IStaffIdentityResolver</c> is registered.</summary>
public enum IdentityFlowUnmappedStaff
{
    /// <summary>Fall back to the prefixed subject (<see cref="IdentityFlowPrincipalOptions.UserIdPrefix"/> + subject). Default.</summary>
    UsePrefixedSubject = 1,

    /// <summary>Resolve no principal (401) until an administrator links the identity.</summary>
    Refuse = 2,
}

public enum IdentityFlowPrincipalKind
{
    User = 1,
    ApiClient = 2,
}

/// <summary>
/// The TicketFlow identity derived from IdentityFlow claims: the kind, the TicketFlow identifier (prefix applied) and the normalized,
/// unprefixed IdentityFlow subject (used for staff identity links).
/// </summary>
public sealed record IdentityFlowIdentity(IdentityFlowPrincipalKind Kind, string Id, string Subject)
{
    public override string ToString() => $"IdentityFlowIdentity {{ Kind = {Kind} }}";
}

public sealed class IdentityFlowPrincipalOptions
{
    /// <summary>Default <see cref="IdentityFlowApiKeyMapping.ApiClient"/>.</summary>
    public IdentityFlowApiKeyMapping ApiKeys { get; set; } = IdentityFlowApiKeyMapping.ApiClient;

    /// <summary>Default <see cref="IdentityFlowApiKeyClientSource.Identity"/>.</summary>
    public IdentityFlowApiKeyClientSource ApiKeyClients { get; set; } = IdentityFlowApiKeyClientSource.Identity;

    /// <summary>Prefix added to IdentityFlow subjects to form <c>UserId</c>s when no staff identity link applies (default none).</summary>
    public string UserIdPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Prefix added to API-key and OAuth client identities to form <c>ApiClientId</c>s (default <c>idf:</c>), so IdentityFlow callers
    /// can never collide with TicketFlow's own worker clients.
    /// </summary>
    public string ApiClientIdPrefix { get; set; } = "idf:";

    /// <summary>
    /// API client identifiers a mapped caller must never receive (TicketFlow worker clients). Defaults to the built-in worker ids;
    /// hosts add configured ones (for example from <c>TicketFlowServiceIdentities.Collect</c> in TicketFlow.Hosting.Worker).
    /// </summary>
    public ISet<string> ReservedApiClientIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "sla-worker", "automation-worker", "ticketflow-routing", "channels",
    };

    /// <summary>
    /// When non-empty, API keys, personal access tokens and bearer tokens must carry at least one of these <c>scope</c> values (browser
    /// sessions carry none and are accepted).
    /// </summary>
    public ISet<string> RequiredScopes { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Authentication types (schemes) whose principals are browser sessions (default IdentityFlow's <c>IdentityFlow.Cookie</c>).</summary>
    public ISet<string> BrowserSessionAuthenticationTypes { get; } = new HashSet<string>(StringComparer.Ordinal) { "IdentityFlow.Cookie" };

    /// <summary>The staff identity provider name used with <c>IStaffIdentityResolver</c> (default <c>identityflow</c>).</summary>
    public string StaffIdentityProvider { get; set; } = "identityflow";

    /// <summary>Default <see cref="IdentityFlowUnmappedStaff.UsePrefixedSubject"/>.</summary>
    public IdentityFlowUnmappedStaff UnmappedStaff { get; set; } = IdentityFlowUnmappedStaff.UsePrefixedSubject;

    /// <summary>Query-string parameter naming the workspace of SignalR agent connections (default <c>workspace</c>).</summary>
    public string RealtimeWorkspaceParameter { get; set; } = "workspace";
}

/// <summary>
/// Maps IdentityFlow-authenticated users to TicketFlow identities using IdentityFlow's claim conventions:
/// <list type="bullet">
/// <item>API keys and personal access tokens: <c>identityflow_auth_method</c> (<c>ApiKey</c>, <c>PersonalAccessToken</c>) with the identity in
/// <see cref="ClaimTypes.NameIdentifier"/> (and the key in <c>identityflow_method_id</c>); PATs are users, API keys follow
/// <see cref="IdentityFlowPrincipalOptions.ApiKeys"/> and <see cref="IdentityFlowPrincipalOptions.ApiKeyClients"/>.</item>
/// <item>Browser sessions and OpenIddict bearer tokens: <c>sub</c> (or <see cref="ClaimTypes.NameIdentifier"/>) → user.</item>
/// <item>Client-credentials tokens without a subject: <c>identityflow:client_id</c> (or <c>client_id</c>) → API client.</item>
/// </list>
/// A principal is a browser session only when its authentication type is one of
/// <see cref="IdentityFlowPrincipalOptions.BrowserSessionAuthenticationTypes"/>, or it carries <c>identityflow:session_id</c> but no client
/// identifier and no authentication method (OpenIddict access tokens carry the session id too, with a client id). Every other principal must
/// satisfy <see cref="IdentityFlowPrincipalOptions.RequiredScopes"/>. GUID subjects are normalized to the "D" format. Unauthenticated users,
/// missing or ambiguous subjects, unmet scopes and reserved API client identifiers map to <see langword="null"/>.
/// </summary>
public static class IdentityFlowClaimsMapper
{
    public const string AuthenticationMethodClaim = "identityflow_auth_method";
    public const string MethodIdClaim = "identityflow_method_id";
    public const string ApiKeyMethod = "ApiKey";
    public const string PersonalAccessTokenMethod = "PersonalAccessToken";
    public const string ScopeClaim = "scope";

    public static IdentityFlowIdentity? Map(ClaimsPrincipal? user, IdentityFlowPrincipalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (user?.Identity is not { IsAuthenticated: true } identity)
            return null;
        var method = Single(user, AuthenticationMethodClaim);
        var client = Single(user, IdentityFlowClaimTypes.ClientId) ?? Single(user, "client_id");
        var session = identity.AuthenticationType is { } type && options.BrowserSessionAuthenticationTypes.Contains(type)
            || Single(user, IdentityFlowClaimTypes.SessionId) is not null && client is null && method is null;
        if (!session && options.RequiredScopes.Count > 0 && !Scopes(user).Any(options.RequiredScopes.Contains))
            return null;
        var subject = Single(user, IdentityFlowClaimTypes.Subject) ?? Single(user, ClaimTypes.NameIdentifier);
        if (method == ApiKeyMethod)
        {
            if (subject is null)
                return null;
            if (options.ApiKeys == IdentityFlowApiKeyMapping.User)
                return Identity(IdentityFlowPrincipalKind.User, options.UserIdPrefix, subject, options);
            var source = options.ApiKeyClients == IdentityFlowApiKeyClientSource.Method ? Single(user, MethodIdClaim) : subject;
            return source is null ? null : Identity(IdentityFlowPrincipalKind.ApiClient, options.ApiClientIdPrefix, source, options);
        }
        if (subject is not null)
            return Identity(IdentityFlowPrincipalKind.User, options.UserIdPrefix, subject, options);
        return client is null ? null : Identity(IdentityFlowPrincipalKind.ApiClient, options.ApiClientIdPrefix, client, options);
    }

    private static IdentityFlowIdentity? Identity(IdentityFlowPrincipalKind kind, string prefix, string value, IdentityFlowPrincipalOptions options)
    {
        var normalized = Guid.TryParse(value, out var guid) ? guid.ToString("D") : value;
        var id = prefix + normalized;
        if (id.Length is 0 or > Core.TicketScope.MaximumIdentifierLength || id.Any(char.IsControl) || !System.Text.Unicode.Utf16.IsValid(id))
            return null;
        if (kind == IdentityFlowPrincipalKind.ApiClient && options.ReservedApiClientIds.Contains(id))
            return null;
        return new IdentityFlowIdentity(kind, id, normalized);
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
