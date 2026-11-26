using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using TicketFlow.Core;

namespace TicketFlow.Access.AspNetCore;

/// <summary>
/// Authenticates TicketFlow service-account API keys supplied via <c>X-Api-Key</c> or
/// <c>Authorization: Bearer tf_sa_...</c>.
/// </summary>
public sealed class TicketFlowApiKeyAuthenticationHandler(
    IOptionsMonitor<TicketFlowApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<TicketFlowApiKeyAuthenticationOptions>(options, logger, encoder)
{
    private const string AuthorizationHeaderName = "Authorization";
    private const string BearerPrefix = "Bearer ";

    public static bool HasApiKeyCredentials(
        HttpRequest request,
        string headerName = TicketFlowApiKeyAuthenticationOptions.DefaultHeaderName,
        bool allowBearerToken = true,
        string? requiredBearerPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(headerName) && request.Headers.TryGetValue(headerName, out var apiKeyValues))
            for (var i = 0; i < apiKeyValues.Count; i++)
                if (!string.IsNullOrWhiteSpace(apiKeyValues[i]))
                    return true;

        if (allowBearerToken && request.Headers.TryGetValue(AuthorizationHeaderName, out var authValues))
            for (var i = 0; i < authValues.Count; i++)
            {
                var value = authValues[i];
                if (value is null)
                    continue;

                var trimmed = value.AsSpan().TrimStart();
                if (!trimmed.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrEmpty(requiredBearerPrefix))
                    return true;

                var token = trimmed[BearerPrefix.Length..].Trim();
                if (token.StartsWith(requiredBearerPrefix, StringComparison.Ordinal))
                    return true;
            }

        return false;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (rawKey, extractionError) = ExtractApiKey(Request, Options.HeaderName, Options.AllowBearerToken);
        if (extractionError is not null)
            return AuthenticateResult.Fail(extractionError);

        if (rawKey is null)
            return AuthenticateResult.NoResult();

        var serviceAccounts = Context.RequestServices.GetService<IServiceAccountService>();
        if (serviceAccounts is null)
            return AuthenticateResult.Fail("Service account authentication is not configured.");

        var targetScope = await ResolveTargetScopeAsync().ConfigureAwait(false);
        if (targetScope is null)
            return AuthenticateResult.Fail("TicketFlow scope could not be resolved for API key authentication.");

        var result = await serviceAccounts.AuthenticateApiKeyAsync(rawKey, targetScope, Context.RequestAborted).ConfigureAwait(false);
        if (!result.IsAuthenticated || result.ServiceAccount is null || result.ApiKey is null)
            return AuthenticateResult.Fail(result.FailureReason ?? "Invalid API key.");

        var account = result.ServiceAccount.Account;
        var apiKey = result.ApiKey;
        var claims = new List<Claim>
        {
            new("client_id", account.ClientId.Value),
            new("amr", "api_key"),
            new("display_name", account.DisplayName),
            new("name", account.DisplayName),
            new(ClaimTypes.Name, account.DisplayName),
            new("api_key_id", apiKey.Id.Value),
            new("api_key_prefix", apiKey.KeyPrefix),
            new(TicketFlowClaimsScopeResolver.TenantClaimType, targetScope.TenantId),
            new(TicketFlowClaimsScopeResolver.ProjectClaimType, targetScope.ProjectId),
            new(TicketFlowClaimsScopeResolver.EnvironmentClaimType, targetScope.EnvironmentId),
            new(TicketFlowClaimsScopeResolver.AppClaimType, targetScope.AppId),
            new(TicketFlowClaimsScopeResolver.WorkspaceClaimType, targetScope.WorkspaceId.Value),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, "role");
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    private async ValueTask<TicketScope?> ResolveTargetScopeAsync()
    {
        if (Options.ResolveScope is not null)
        {
            var resolved = await Options.ResolveScope(Context).ConfigureAwait(false);
            if (resolved is not null)
                return resolved;
        }

        var baseScope = Options.DefaultScope;
        if (baseScope is null)
            return null;

        if (!string.IsNullOrWhiteSpace(Options.WorkspaceRouteParameter)
            && Context.GetRouteValue(Options.WorkspaceRouteParameter) is string routeWorkspace
            && TryCreateWorkspaceId(routeWorkspace, out var routeWsId))
            return AccessPartition.From(baseScope).InWorkspace(routeWsId);

        if (!string.IsNullOrWhiteSpace(Options.WorkspaceQueryParameter)
            && Request.Query.TryGetValue(Options.WorkspaceQueryParameter, out var queryValues))
        {
            var resolution = ResolveSingleValue(queryValues, splitCommas: false);
            if (!resolution.IsAmbiguous && resolution.Value is not null && TryCreateWorkspaceId(resolution.Value, out var queryWsId))
                return AccessPartition.From(baseScope).InWorkspace(queryWsId);
        }

        if (!string.IsNullOrWhiteSpace(Options.WorkspaceCookieName)
            && Request.Cookies.TryGetValue(Options.WorkspaceCookieName, out var cookieWorkspace)
            && TryCreateWorkspaceId(cookieWorkspace, out var cookieWsId))
            return AccessPartition.From(baseScope).InWorkspace(cookieWsId);

        return baseScope;
    }

    private static bool TryCreateWorkspaceId(string? value, out WorkspaceId workspaceId)
    {
        if (!string.IsNullOrWhiteSpace(value) && AccessText.IsValidText(value.Trim(), TicketScope.MaximumIdentifierLength))
        {
            workspaceId = new WorkspaceId(value.Trim());
            return true;
        }

        workspaceId = default;
        return false;
    }

    private static (string? RawKey, string? Error) ExtractApiKey(HttpRequest request, string? headerName, bool allowBearerToken)
    {
        if (!string.IsNullOrWhiteSpace(headerName) && request.Headers.TryGetValue(headerName, out var apiKeyValues))
        {
            var apiKeyResolution = ResolveSingleValue(apiKeyValues, splitCommas: true);
            if (apiKeyResolution.IsAmbiguous)
                return (null, "Multiple distinct API key headers are not permitted.");
            if (apiKeyResolution.Value is not null)
                return (apiKeyResolution.Value, null);
        }

        if (allowBearerToken && request.Headers.TryGetValue(AuthorizationHeaderName, out var authValues))
        {
            var authResolution = ResolveSingleValue(authValues, splitCommas: false);
            if (authResolution.IsAmbiguous)
                return (null, "Multiple distinct Authorization headers are not permitted.");

            if (authResolution.Value is { } authHeader)
            {
                if (authHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var token = authHeader[BearerPrefix.Length..].Trim();
                    return token.Length > 0 ? (token, null) : (null, "API key is empty.");
                }

                if (string.Equals(authHeader, "Bearer", StringComparison.OrdinalIgnoreCase))
                    return (null, "API key is empty.");
            }
        }

        return (null, null);
    }

    private static (string? Value, bool IsAmbiguous) ResolveSingleValue(StringValues values, bool splitCommas)
    {
        string? resolved = null;

        for (var i = 0; i < values.Count; i++)
        {
            var raw = values[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (splitCommas && raw.Contains(',', StringComparison.Ordinal))
            {
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var part in parts)
                    if (resolved is null)
                        resolved = part;
                    else if (!string.Equals(resolved, part, StringComparison.Ordinal))
                        return (null, true);
                continue;
            }

            var trimmed = raw.Trim();
            if (resolved is null)
                resolved = trimmed;
            else if (!string.Equals(resolved, trimmed, StringComparison.Ordinal))
                return (null, true);
        }

        return (resolved, false);
    }
}
