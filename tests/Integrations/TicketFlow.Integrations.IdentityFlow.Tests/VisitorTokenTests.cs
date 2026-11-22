using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Testing;

using TicketFlow.Integrations.IdentityFlow.SignalR;

namespace TicketFlow.Integrations.IdentityFlow.Tests;

[TestClass]
public sealed class VisitorTokenTests
{
    private const string Issuer = "https://id.example.test/";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "k1" };

    [TestMethod]
    public async Task ValidTokensYieldTheNormalizedSubjectAndDisplayName()
    {
        var subject = Guid.NewGuid();
        var validator = Static();
        var result = await validator.ValidateAsync(TestScopes.Create(), "cfg-chat", Issue(subject.ToString("N"), extra: new() { ["display_name"] = "Ada" }));
        var accepted = Assert.IsInstanceOfType<VisitorTokenAccepted>(result.Value);
        Assert.AreEqual("identityflow", accepted.Namespace);
        Assert.AreEqual(subject.ToString("D"), accepted.Subject);
        Assert.AreEqual("Ada", accepted.DisplayName);
    }

    [TestMethod]
    public async Task ExpiredForeignUnsignedAndTamperedTokensAreRejected()
    {
        var validator = Static();
        var scope = TestScopes.Create();
        await Rejected(validator.ValidateAsync(scope, "cfg", Issue("u1", expires: DateTime.UtcNow.AddMinutes(-10), notBefore: DateTime.UtcNow.AddMinutes(-20))));
        await Rejected(validator.ValidateAsync(scope, "cfg", Issue("u1", audience: "someone-else")));
        await Rejected(validator.ValidateAsync(scope, "cfg", Issue("u1", issuer: "https://evil.example.test/")));
        await Rejected(validator.ValidateAsync(scope, "cfg", Issue("u1", key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" })));
        var unsigned = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "widget",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "u1" },
        });
        await Rejected(validator.ValidateAsync(scope, "cfg", unsigned));
        var valid = Issue("u1");
        var parts = valid.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])).Replace("u1", "u2", StringComparison.Ordinal);
        await Rejected(validator.ValidateAsync(scope, "cfg", $"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}"));
        await Rejected(validator.ValidateAsync(scope, "cfg", "not-a-token"));
        await Rejected(validator.ValidateAsync(scope, "cfg", new string('a', 9000)));
        await Rejected(validator.ValidateAsync(scope, "cfg", Issue(" ")));
    }

    [TestMethod]
    public async Task PerWidgetAudiencesWinOverDefaults()
    {
        var options = Options("widget");
        options.AudienceResolver = (_, configuration) => configuration == "cfg-shop" ? ["shop-widget"] : [];
        using var validator = new IdentityFlowVisitorTokenValidator(Microsoft.Extensions.Options.Options.Create(options));
        Assert.IsInstanceOfType<VisitorTokenAccepted>((await validator.ValidateAsync(TestScopes.Create(), "cfg-shop", Issue("u1", audience: "shop-widget"))).Value);
        await Rejected(validator.ValidateAsync(TestScopes.Create(), "cfg-shop", Issue("u1")));
        Assert.IsInstanceOfType<VisitorTokenAccepted>((await validator.ValidateAsync(TestScopes.Create(), "cfg-other", Issue("u1"))).Value);
    }

    [TestMethod]
    public async Task DiscoveryAndJwksAreFetchedFromTheAuthority()
    {
        var handler = new DiscoveryHandler();
        var options = new IdentityFlowVisitorTokenOptions { Authority = "https://id.example.test", BackchannelHandler = handler };
        options.Audiences.Add("widget");
        using var validator = new IdentityFlowVisitorTokenValidator(Microsoft.Extensions.Options.Options.Create(options));
        Assert.IsInstanceOfType<VisitorTokenAccepted>((await validator.ValidateAsync(TestScopes.Create(), "cfg", Issue("u1"))).Value);
        CollectionAssert.Contains(handler.Requests, "/.well-known/openid-configuration");
        CollectionAssert.Contains(handler.Requests, "/.well-known/jwks");

        var unreachable = new IdentityFlowVisitorTokenOptions { Authority = "https://down.example.test", BackchannelHandler = new FailingHandler() };
        unreachable.Audiences.Add("widget");
        using var failing = new IdentityFlowVisitorTokenValidator(Microsoft.Extensions.Options.Options.Create(unreachable));
        await Rejected(failing.ValidateAsync(TestScopes.Create(), "cfg", Issue("u1")));
    }

    [TestMethod]
    public async Task RotatedIssuerKeysArePickedUpWithoutRestart()
    {
        var rotated = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k2" };
        var handler = new DiscoveryHandler();
        var options = new IdentityFlowVisitorTokenOptions { Authority = "https://id.example.test", BackchannelHandler = handler };
        options.Audiences.Add("widget");
        using var validator = new IdentityFlowVisitorTokenValidator(Microsoft.Extensions.Options.Options.Create(options));
        Assert.IsInstanceOfType<VisitorTokenAccepted>((await validator.ValidateAsync(TestScopes.Create(), "cfg", Issue("u1"))).Value);
        handler.Key = rotated;
        Assert.IsInstanceOfType<VisitorTokenAccepted>((await validator.ValidateAsync(TestScopes.Create(), "cfg", Issue("u1", key: rotated))).Value,
            "A token signed with a new key triggers a JWKS refresh.");
    }

    [TestMethod]
    public void ConfigurationIsValidatedUpFront()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new IdentityFlowVisitorTokenValidator(Microsoft.Extensions.Options.Options.Create(new IdentityFlowVisitorTokenOptions { Authority = Issuer })));
        var noIssuer = new IdentityFlowVisitorTokenOptions();
        noIssuer.Audiences.Add("widget");
        noIssuer.IssuerSigningKeys.Add(Key);
        Assert.ThrowsExactly<InvalidOperationException>(() => new IdentityFlowVisitorTokenValidator(Microsoft.Extensions.Options.Options.Create(noIssuer)));
    }

    private static IdentityFlowVisitorTokenOptions Options(string audience)
    {
        var options = new IdentityFlowVisitorTokenOptions();
        options.Audiences.Add(audience);
        options.ValidIssuers.Add(Issuer);
        options.IssuerSigningKeys.Add(Key);
        return options;
    }

    private static IdentityFlowVisitorTokenValidator Static() => new(Microsoft.Extensions.Options.Options.Create(Options("widget")));

    private static string Issue(string subject, string audience = "widget", string issuer = Issuer, DateTime? expires = null, DateTime? notBefore = null, SecurityKey? key = null,
        Dictionary<string, object>? extra = null)
    {
        var claims = new Dictionary<string, object>(extra ?? []) { ["sub"] = subject };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            IssuedAt = notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }

    private static async Task Rejected(Task<VisitorTokenValidation> validation) => Assert.IsInstanceOfType<VisitorTokenRejected>((await validation).Value);

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public RsaSecurityKey Key { get; set; } = VisitorTokenTests.Key;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            var body = request.RequestUri.AbsolutePath switch
            {
                "/.well-known/openid-configuration" => JsonSerializer.Serialize(new { issuer = Issuer, jwks_uri = "https://id.example.test/.well-known/jwks" }),
                "/.well-known/jwks" => JsonSerializer.Serialize(new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(Key.Rsa.ExportParameters(false)) { KeyId = Key.KeyId }) } },
                    new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
                _ => null,
            };
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("unreachable");
    }
}
