using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TicketFlow.SampleHost;

/// <summary>
/// DEVELOPMENT ONLY. Authenticates whoever names a user in the <c>X-Dev-User</c> header, the <c>dev_user</c> query parameter (browser
/// SignalR connections) or the <c>tf-dev-user</c> cookie set by <c>/dev/login</c> (the support UI) and emits IdentityFlow-shaped session claims (<c>sub</c>, <c>identityflow:session_id</c>) so the
/// IdentityFlow principal mapping runs unchanged. It performs no verification at all and refuses to authenticate outside the
/// Development environment. Replace it with IdentityFlow authentication (cookies, JWT bearer, API keys, PATs) in any real deployment.
/// </summary>
public sealed class DevelopmentAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TicketFlowDevelopment";
    public const string Header = "X-Dev-User";
    public const string QueryParameter = "dev_user";
    public const string CookieName = "tf-dev-user";

    /// <summary>Names an API client (for example an MCP agent); emits IdentityFlow API-key claims, mapped to an <c>ApiClientPrincipal</c>.</summary>
    public const string ApiKeyHeader = "X-Dev-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!environment.IsDevelopment())
            return Task.FromResult(AuthenticateResult.Fail("Development authentication is disabled outside Development."));
        if (Request.Headers[ApiKeyHeader].ToString() is { Length: > 0 } client)
        {
            if (client.Length > 64 || !client.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
                return Task.FromResult(AuthenticateResult.NoResult());
            var apiKey = new ClaimsIdentity([new Claim("sub", client), new Claim("identityflow_auth_method", "ApiKey")], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(apiKey), SchemeName)));
        }
        var user = Request.Headers[Header].ToString() is { Length: > 0 } header ? header
            : Request.Query[QueryParameter].ToString() is { Length: > 0 } query ? query
            : Request.Cookies[CookieName] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(user) || user.Length > 64 || !user.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity(
            [new Claim("sub", user), new Claim(ClaimTypes.NameIdentifier, user), new Claim("identityflow:session_id", "development"),
                new Claim("name", SampleUsers.Find(user)?.DisplayName ?? user), new Claim("email", SampleUsers.Find(user)?.Email ?? $"{user}@example.com")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
