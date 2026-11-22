# IdentityFlow integration

`TicketFlow.Integrations.IdentityFlow` connects IdentityFlow authentication to TicketFlow. IdentityFlow authenticates; TicketFlow
authorizes. The adapter only turns an authenticated IdentityFlow user into a TicketFlow `PrincipalRef`; what that principal may do is
decided by TicketFlow Access grants.

It references the published `IdentityFlow.Hosting.AspNetCore.Authentication` package (central pin
`0.1.0-alpha.20260826115343`, the latest alpha at the time of writing) for IdentityFlow's claim types, plus
`Microsoft.IdentityModel.JsonWebTokens` and `Microsoft.IdentityModel.Protocols.OpenIdConnect` for visitor-token validation. No
IdentityFlow changes were needed.

## Principals

```csharp
// IdentityFlow authentication (host-owned), for example:
services.AddIdentityFlowHybridAuthentication();           // cookies + JWT bearer (IdentityFlow.Hosting.AspNetCore.JwtBearer)
services.AddAuthentication().AddIdentityFlowApiKey();      // optional API keys, likewise PATs

services.AddTicketFlow()
    /* features, stores */
    .AddTicketFlowEndpoints(options => options.Partition = partition)   // or an ITicketFlowPartitionResolver
    .AddTicketFlowSignalR(options => options.VisitorPartition = partition)
    .AddIdentityFlowPrincipals(options =>
    {
        options.ApiKeys = IdentityFlowApiKeyMapping.ApiClient;   // default; or User
        options.RequiredScopes.Add("ticketflow");                // optional, for tokens/keys/PATs
    });

app.MapTicketFlowEndpoints();
app.MapTicketFlowRealtime().Agent.RequireAuthorization();        // agent hub: /ticketflow/realtime/agent?workspace={workspaceId}
```

`IdentityFlowPrincipalResolver` implements both `ITicketFlowPrincipalResolver` (REST endpoints; workspace from the route) and
`IRealtimePrincipalResolver` (agent hub; workspace from the `workspace` query parameter, partition from `ITicketFlowPartitionResolver`).
`AddIdentityFlowPrincipals()` replaces only the options-based fallbacks of `AddTicketFlowEndpoints()`/`AddTicketFlowSignalR()`; resolvers
the application registered itself win.

| IdentityFlow authentication | Claims read | TicketFlow principal |
| --- | --- | --- |
| Browser session (cookie) | `sub` (`identityflow:session_id` present) | `UserPrincipal` |
| OpenIddict bearer token with a subject | `sub` (scopes in `scope`) | `UserPrincipal` |
| Personal access token | `identityflow_auth_method=PersonalAccessToken`, identity in `NameIdentifier` | `UserPrincipal` |
| API key | `identityflow_auth_method=ApiKey`, identity in `NameIdentifier` | `ApiClientPrincipal` (or `UserPrincipal` with `ApiKeys = User`) |
| Client-credentials token without a subject | `identityflow:client_id` / `client_id` | `ApiClientPrincipal` |

- GUID subjects are normalized to the "D" format, because IdentityFlow writes `D` in sessions and tokens but `N` in API-key and PAT
  principals; one identity therefore maps to one `UserId`. `UserIdPrefix` and `ApiClientIdPrefix` namespace the identifiers when
  several identity systems share a workspace.
- `UserPrincipal`s carry the workspace's current team snapshot from `ITeamMembershipResolver`, so team grants and team conditions
  apply. The snapshot is taken per request (REST) or per connection (SignalR).
- `RequiredScopes` (any of) applies to tokens, API keys and PATs; browser sessions carry no scopes and are accepted.
- Unauthenticated users, missing or ambiguous subjects (several distinct `sub` values), API keys without an identity and unmet scopes
  resolve to no principal (401).
- Grant the resulting principals roles with TicketFlow Access (`/access/grants` or `IAccessAdministrator`). Use
  `ApiKeys = ApiClient` for integrations so their grants are separate from any human user's.

## Visitor identity tokens

Logged-in sites can identify live-chat visitors with an IdentityFlow-issued JWT instead of an HMAC signature
(`widget.client.identify("token", jwt)`):

```csharp
services.AddTicketFlow()
    .AddLiveChat()
    .AddIdentityFlowVisitorTokens(options =>
    {
        options.Authority = "https://id.example.com";        // discovery: {Authority}/.well-known/openid-configuration
        options.Audiences.Add("shop-widget");                 // the client/resource the site requests the token for
        // options.AudienceResolver = (scope, widgetConfigurationId) => [...];   // per-widget audiences
    });
```

`IdentityFlowVisitorTokenValidator` validates the signature against the issuer's JWKS (discovered and cached by
`ConfigurationManager<OpenIdConnectConfiguration>`, HTTPS required by default) or static `IssuerSigningKeys`, the issuer (discovery
issuer or `ValidIssuers`), the audience, the token type (`JWT` identity tokens or `at+jwt` access tokens) and the lifetime (expiry
required, one minute clock skew). Unsigned, tampered, expired, foreign-issuer and foreign-audience tokens, tokens over 8192 characters
and an unreachable issuer all yield `VisitorTokenRejected`. Accepted tokens report namespace `identityflow` (configurable), the `sub`
claim (GUIDs normalized) and `display_name`/`name` as display name. OpenIddict encrypts access tokens by default; use identity tokens or
issue the widget's access tokens with encryption disabled. Tokens and validation details are never logged.

## Verification

- `dotnet test tests/Integrations/TicketFlow.Integrations.IdentityFlow.Tests -c Release` — claim mapping for sessions, bearer tokens,
  PATs, API keys and client tokens, team snapshots, realtime workspace resolution, registration precedence, and token validation
  (valid, expired, wrong audience/issuer, wrong key, unsigned, tampered, oversized, discovery and JWKS fetch, unreachable issuer).
- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` — the sample host maps its development identities through the same
  resolver.
