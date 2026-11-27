using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Core;

namespace TicketFlow.Integrations.IdentityFlow.SignalR;

public sealed class IdentityFlowVisitorTokenOptions
{
    /// <summary>The IdentityFlow (OpenIddict) issuer base URL; discovery is read from <c>{Authority}/.well-known/openid-configuration</c>.</summary>
    public string? Authority { get; set; }

    /// <summary>Explicit discovery document address (overrides <see cref="Authority"/>).</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Require HTTPS for discovery and JWKS (default true; disable only for local development).</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Accepted issuers; defaults to the discovery document's issuer.</summary>
    public IList<string> ValidIssuers { get; } = [];

    /// <summary>Accepted audiences (at least one, or an <see cref="AudienceResolver"/>): the client or resource the site requests visitor tokens for.</summary>
    public IList<string> Audiences { get; } = [];

    /// <summary>Optional per-widget audiences (scope, widget configuration id); wins over <see cref="Audiences"/> when it returns values.</summary>
    public Func<TicketScope, string, IReadOnlyCollection<string>>? AudienceResolver { get; set; }

    /// <summary>Static signing keys (offline validation or tests); when set, discovery is not used for keys.</summary>
    public IList<SecurityKey> IssuerSigningKeys { get; } = [];

    /// <summary>Accepted JWT <c>typ</c> headers (default <c>JWT</c> and <c>at+jwt</c>, the OpenIddict identity and access token types).</summary>
    public IList<string> ValidTokenTypes { get; } = ["JWT", "at+jwt"];

    /// <summary>The identity namespace reported to live chat (default <c>identityflow</c>).</summary>
    public string Namespace { get; set; } = "identityflow";

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Maximum accepted token length in characters (default 8192).</summary>
    public int MaximumTokenLength { get; set; } = 8192;

    /// <summary>How often discovery and JWKS are re-read even without unknown keys (default 12 hours).</summary>
    public TimeSpan AutomaticRefreshInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Minimum time between refreshes triggered by tokens naming unknown signing keys (default 5 minutes).</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Back-channel handler for discovery and JWKS requests (proxies, tests).</summary>
    public HttpMessageHandler? BackchannelHandler { get; set; }
}

/// <summary>
/// <see cref="IVisitorTokenValidator"/> for IdentityFlow-issued JWTs (OpenIddict identity tokens, or access tokens issued without encryption):
/// signature against the issuer's JWKS (discovery through <see cref="TokenValidationParameters.ConfigurationManager"/>: cached, refreshed
/// periodically and on unknown key identifiers, so issuer key rotation is picked up) or static keys, issuer,
/// audience, token type and lifetime (expiry required). The subject (<c>sub</c>, GUIDs normalized to "D") is the verified visitor identity;
/// <c>display_name</c> or <c>name</c> is the optional display name. Tokens and validation details are never logged.
/// </summary>
public sealed class IdentityFlowVisitorTokenValidator : IVisitorTokenValidator, IDisposable
{
    private readonly IdentityFlowVisitorTokenOptions options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? configuration;
    private readonly HttpClient? backchannel;
    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false };
    private readonly string[]? validTypes;
    private readonly string[]? validIssuers;
    private readonly SecurityKey[]? signingKeys;
    private readonly string[] defaultAudiences;
    private readonly TokenValidationParameters? frozenParameters;
    private long lastForcedRefreshTicks;

    public IdentityFlowVisitorTokenValidator(IOptions<IdentityFlowVisitorTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        if (this.options.Audiences.Count == 0 && this.options.AudienceResolver is null)
            throw new InvalidOperationException("IdentityFlowVisitorTokenOptions requires at least one audience or an audience resolver.");
        if (this.options.MaximumTokenLength is < 256 or > 65_536)
            throw new InvalidOperationException("IdentityFlowVisitorTokenOptions.MaximumTokenLength must be 256-65536.");
        _ = new VisitorTokenAccepted(this.options.Namespace, "probe");
        validTypes = this.options.ValidTokenTypes.Count > 0 ? [.. this.options.ValidTokenTypes] : null;
        validIssuers = this.options.ValidIssuers.Count > 0 ? [.. this.options.ValidIssuers] : null;
        defaultAudiences = [.. this.options.Audiences];
        if (this.options.Authority is not null)
            ValidateEndpointUri(this.options.Authority, nameof(IdentityFlowVisitorTokenOptions.Authority), this.options.RequireHttpsMetadata, allowQuery: false);
        if (this.options.MetadataAddress is not null)
            ValidateEndpointUri(this.options.MetadataAddress, nameof(IdentityFlowVisitorTokenOptions.MetadataAddress), this.options.RequireHttpsMetadata, allowQuery: true);
        if (this.options.IssuerSigningKeys.Count > 0)
        {
            if (validIssuers is null)
                throw new InvalidOperationException("Static signing keys require explicit ValidIssuers.");
            signingKeys = [.. this.options.IssuerSigningKeys];
        }
        else
        {
            var metadata = this.options.MetadataAddress ?? (this.options.Authority is { } authority ? authority.TrimEnd('/') + "/.well-known/openid-configuration" : null)
                ?? throw new InvalidOperationException("IdentityFlowVisitorTokenOptions requires an Authority, a MetadataAddress or static signing keys.");
            backchannel = new HttpClient(this.options.BackchannelHandler ?? CreateDefaultBackchannelHandler(), disposeHandler: this.options.BackchannelHandler is null) { Timeout = TimeSpan.FromSeconds(30) };
            configuration = new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(backchannel) { RequireHttps = this.options.RequireHttpsMetadata })
            {
                AutomaticRefreshInterval = this.options.AutomaticRefreshInterval,
                RefreshInterval = this.options.RefreshInterval,
            };
        }
        if (this.options.AudienceResolver is null)
            frozenParameters = BuildParameters(defaultAudiences);
    }

    private TokenValidationParameters BuildParameters(IReadOnlyCollection<string> audiences)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = options.ClockSkew,
            ValidTypes = validTypes,
        };
        if (configuration is null)
        {
            parameters.ValidIssuers = validIssuers;
            parameters.IssuerSigningKeys = signingKeys;
        }
        else
        {
            parameters.ConfigurationManager = configuration;
            if (validIssuers is not null)
                parameters.ValidIssuers = validIssuers;
        }
        return parameters;
    }

    public async Task<VisitorTokenValidation> ValidateAsync(TicketScope scope, string configurationId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrEmpty(token) || token.Length > options.MaximumTokenLength)
            return VisitorTokenRejected.Instance;
        TokenValidationParameters parameters;
        if (frozenParameters is not null)
            parameters = frozenParameters;
        else
        {
            var audiences = options.AudienceResolver?.Invoke(scope, configurationId) is { Count: > 0 } resolved ? resolved : defaultAudiences;
            parameters = BuildParameters(audiences);
        }
        TokenValidationResult result;
        try
        {
            result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
            // An unknown key identifier may mean the issuer rotated keys: trigger at most one rate-limited refresh per RefreshInterval and retry once.
            if (configuration is not null && !result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException && TryBeginForcedRefresh())
            {
                var before = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                configuration.RequestRefresh();
                var current = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                // ConfigurationManager enforces a minimum 1-second cooldown after its initial fetch before
                // honoring RequestRefresh(), so poll up to ~2 seconds while returning immediately once refreshed.
                for (var attempt = 0; ReferenceEquals(current, before) && attempt < 22; attempt++)
                {
                    await Task.Delay(attempt < 2 ? 10 : attempt < 4 ? 50 : 100, cancellationToken).ConfigureAwait(false);
                    configuration.RequestRefresh();
                    current = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                }
                if (!ReferenceEquals(current, before))
                    result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is ArgumentException or SecurityTokenException)
        {
            return VisitorTokenRejected.Instance;
        }
        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt || !jwt.TryGetPayloadValue<string>("sub", out var subject) || string.IsNullOrWhiteSpace(subject))
            return VisitorTokenRejected.Instance;
        var displayName = jwt.TryGetPayloadValue<string>("display_name", out var display) && !string.IsNullOrWhiteSpace(display) ? display
            : jwt.TryGetPayloadValue<string>("name", out var name) && !string.IsNullOrWhiteSpace(name) ? name : null;
        try
        {
            return new VisitorTokenAccepted(options.Namespace, Guid.TryParse(subject, out var guid) ? guid.ToString("D") : subject,
                displayName is { Length: > 256 } ? displayName[..256] : displayName);
        }
        catch (ArgumentException)
        {
            return VisitorTokenRejected.Instance;
        }
    }

    private bool TryBeginForcedRefresh()
    {
        var now = DateTime.UtcNow.Ticks;
        var minInterval = options.RefreshInterval.Ticks;
        while (true)
        {
            var previous = Volatile.Read(ref lastForcedRefreshTicks);
            if (previous != 0 && now - previous < minInterval)
                return false;
            if (Interlocked.CompareExchange(ref lastForcedRefreshTicks, now, previous) == previous)
                return true;
        }
    }

    internal static SocketsHttpHandler CreateDefaultBackchannelHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    private static void ValidateEndpointUri(string value, string optionName, bool requireHttps, bool allowQuery)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"IdentityFlowVisitorTokenOptions.{optionName} must be an absolute URI.");
        var validScheme = requireHttps
            ? string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            : string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!validScheme)
            throw new InvalidOperationException(requireHttps
                ? $"IdentityFlowVisitorTokenOptions.{optionName} must use the https scheme when RequireHttpsMetadata is true."
                : $"IdentityFlowVisitorTokenOptions.{optionName} must use the http or https scheme.");
        if (uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || (!allowQuery && uri.Query.Length != 0))
            throw new InvalidOperationException($"IdentityFlowVisitorTokenOptions.{optionName} must not contain user info, a fragment{(allowQuery ? "" : ", or a query")}.");
    }

    public void Dispose() => backchannel?.Dispose();
}
