using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Core;

namespace TicketFlow.Integrations.IdentityFlow;

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

    /// <summary>Back-channel handler for discovery and JWKS requests (proxies, tests).</summary>
    public HttpMessageHandler? BackchannelHandler { get; set; }
}

/// <summary>
/// <see cref="IVisitorTokenValidator"/> for IdentityFlow-issued JWTs (OpenIddict identity tokens, or access tokens issued without encryption):
/// signature against the issuer's JWKS (discovery, cached and refreshed by <see cref="ConfigurationManager{T}"/>) or static keys, issuer,
/// audience, token type and lifetime (expiry required). The subject (<c>sub</c>, GUIDs normalized to "D") is the verified visitor identity;
/// <c>display_name</c> or <c>name</c> is the optional display name. Tokens and validation details are never logged.
/// </summary>
public sealed class IdentityFlowVisitorTokenValidator : IVisitorTokenValidator, IDisposable
{
    private readonly IdentityFlowVisitorTokenOptions options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? configuration;
    private readonly HttpClient? backchannel;
    private readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false };

    public IdentityFlowVisitorTokenValidator(IOptions<IdentityFlowVisitorTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        if (this.options.Audiences.Count == 0 && this.options.AudienceResolver is null)
            throw new InvalidOperationException("IdentityFlowVisitorTokenOptions requires at least one audience or an audience resolver.");
        if (this.options.MaximumTokenLength is < 256 or > 65_536)
            throw new InvalidOperationException("IdentityFlowVisitorTokenOptions.MaximumTokenLength must be 256-65536.");
        _ = new VisitorTokenAccepted(this.options.Namespace, "probe");
        if (this.options.IssuerSigningKeys.Count > 0)
        {
            if (this.options.ValidIssuers.Count == 0)
                throw new InvalidOperationException("Static signing keys require explicit ValidIssuers.");
            return;
        }
        var metadata = this.options.MetadataAddress ?? (this.options.Authority is { } authority ? authority.TrimEnd('/') + "/.well-known/openid-configuration" : null)
            ?? throw new InvalidOperationException("IdentityFlowVisitorTokenOptions requires an Authority, a MetadataAddress or static signing keys.");
        backchannel = new HttpClient(this.options.BackchannelHandler ?? new HttpClientHandler(), disposeHandler: this.options.BackchannelHandler is null) { Timeout = TimeSpan.FromSeconds(30) };
        configuration = new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(backchannel) { RequireHttps = this.options.RequireHttpsMetadata });
    }

    public async Task<VisitorTokenValidation> ValidateAsync(TicketScope scope, string configurationId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (string.IsNullOrEmpty(token) || token.Length > options.MaximumTokenLength)
            return VisitorTokenRejected.Instance;
        var audiences = options.AudienceResolver?.Invoke(scope, configurationId) is { Count: > 0 } resolved ? resolved : [.. options.Audiences];
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
            ValidTypes = options.ValidTokenTypes.Count > 0 ? [.. options.ValidTokenTypes] : null,
        };
        if (configuration is null)
        {
            parameters.ValidIssuers = [.. options.ValidIssuers];
            parameters.IssuerSigningKeys = [.. options.IssuerSigningKeys];
        }
        else
        {
            OpenIdConnectConfiguration discovery;
            try
            {
                discovery = await configuration.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException)
            {
                // The issuer is unreachable: fail closed without echoing details.
                return VisitorTokenRejected.Instance;
            }
            parameters.ValidIssuers = options.ValidIssuers.Count > 0 ? [.. options.ValidIssuers] : [discovery.Issuer];
            parameters.IssuerSigningKeys = discovery.SigningKeys;
        }
        TokenValidationResult result;
        try
        {
            result = await handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
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

    public void Dispose() => backchannel?.Dispose();
}
