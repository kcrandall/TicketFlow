# IdentityFlow integration

IdentityFlow authenticates; TicketFlow authorizes. The adapter turns an authenticated IdentityFlow user into a TicketFlow
`PrincipalRef`; what that principal may do is decided by TicketFlow Access grants.

| Package | Contents |
| --- | --- |
| `TicketFlow.Integrations.IdentityFlow` | `IdentityFlowClaimsMapper`, `IdentityFlowPrincipalResolver` (`ITicketFlowPrincipalResolver` for endpoints, the Blazor UI and MCP), `AddIdentityFlowPrincipals()`. References `TicketFlow.Hosting.AspNetCore`, Workspaces and Identity abstractions and the published `IdentityFlow.Hosting.AspNetCore.Authentication` (central pin `0.1.0-alpha.20260826115343`) for IdentityFlow's claim types. |
| `TicketFlow.Integrations.IdentityFlow.SignalR` | `IdentityFlowRealtimePrincipalResolver` (agent hub), `IdentityFlowVisitorTokenValidator` (live-chat visitor JWTs), `AddIdentityFlowRealtimePrincipals()`, `AddIdentityFlowVisitorTokens()`. Adds the SignalR, live-chat and `Microsoft.IdentityModel` dependencies only for hosts that need them. |

No IdentityFlow changes were needed.

## Principals

```csharp
// IdentityFlow authentication (host-owned), for example:
services.AddIdentityFlowHybridAuthentication();           // cookies + JWT bearer (IdentityFlow.Hosting.AspNetCore.JwtBearer)
services.AddAuthentication().AddIdentityFlowApiKey();      // optional API keys, likewise PATs

services.AddTicketFlow()
    /* features, stores */
    .AddStaffIdentity().AddPostgresStaffIdentityStores(dataSource)      // optional: staff identity links (E16)
    .AddTicketFlowEndpoints(options => options.Partition = partition)   // or an ITicketFlowPartitionResolver
    .AddTicketFlowSignalR(options => options.VisitorPartition = partition)
    .AddIdentityFlowRealtimePrincipals(options =>                       // AddIdentityFlowPrincipals() without SignalR
    {
        options.RequiredScopes.Add("ticketflow");                         // tokens, API keys and PATs must carry it
    });

app.MapTicketFlowEndpoints();
app.MapTicketFlowRealtime().Agent.RequireAuthorization();        // agent hub: /ticketflow/realtime/agent?workspace={workspaceId}
```

The registration methods replace only the typed option fallbacks (`OptionsTicketFlowPrincipalResolver`,
`OptionsRealtimePrincipalResolver`); resolvers the application registered itself win. The realtime resolver takes the workspace from the
`workspace` query parameter and the partition from `ITicketFlowPartitionResolver`, then resolves exactly like the REST resolver.

| IdentityFlow authentication | Claims read | TicketFlow principal |
| --- | --- | --- |
| Browser session | authentication type `IdentityFlow.Cookie`, or `identityflow:session_id` with no client id and no authentication method | `UserPrincipal` (`sub`) |
| OpenIddict bearer token with a subject | `sub`, `identityflow:client_id`, `scope` (may also carry `identityflow:session_id`) | `UserPrincipal` |
| Personal access token | `identityflow_auth_method=PersonalAccessToken`, identity in `NameIdentifier` | `UserPrincipal` |
| API key | `identityflow_auth_method=ApiKey`, identity in `NameIdentifier`, key in `identityflow_method_id` | `ApiClientPrincipal` `idf:{identity}` (or `idf:{key}` with `ApiKeyClients = Method`; `UserPrincipal` with `ApiKeys = User`) |
| Client-credentials token without a subject | `identityflow:client_id` / `client_id` | `ApiClientPrincipal` `idf:{client}` |

- **Scopes**: every principal that is not a browser session must carry one of `RequiredScopes` (when set). OpenIddict access tokens
  issued from a browser session carry `identityflow:session_id` too, so the session test also requires the absence of a client id and of an
  authentication method; only real cookie sessions skip the scope check.
- **Staff identity links**: when an `IStaffIdentityResolver` is registered (`AddStaffIdentity()`), users are resolved through
  `ResolveUserAsync(scope, new StaffIdentityRef(StaffIdentityProvider, subject))` first (provider default `identityflow`, the normalized
  subject). Linked identities become the linked `UserId`; unlinked ones fall back to `UserIdPrefix + subject` or, with
  `UnmappedStaff = Refuse`, get no principal (401) until an administrator links them.
- **API clients** are prefixed with `ApiClientIdPrefix` (default `idf:`), and identifiers in `ReservedApiClientIds` are refused, so an
  IdentityFlow caller can never act as one of TicketFlow's own worker clients (`sla-worker`, `automation-worker`, `ticketflow-routing`,
  `channels` by default; hosts add configured ones, for example from `TicketFlowServiceIdentities.Collect` in TicketFlow.Hosting.Worker,
  as the sample host does). By default every API key of one IdentityFlow identity is one API client (grants per identity); set
  `ApiKeyClients = Method` to make each key its own client with its own grants.
- GUID subjects are normalized to the "D" format (IdentityFlow writes `D` in sessions and tokens but `N` in API-key and PAT principals).
- `UserPrincipal`s carry the workspace's current team snapshot from `ITeamMembershipResolver`, taken per request (REST) or per connection
  (SignalR).
- Unauthenticated users, missing or ambiguous subjects, API keys without an identity, unmet scopes and reserved identifiers resolve to no
  principal (401).

## Customers signing in

Staff and customers are different principals. When a customer signs in to the application with IdentityFlow, the application (not this
adapter) calls `IContactLinkingService.ResolveCustomerAsync` with its own linking principal and an `ExternalUser` identity
(namespace = the IdentityFlow issuer, value = the subject) to find or create the customer's contact, then uses that contact for widget
identity or portal features. Customer sessions must never be passed to `IdentityFlowPrincipalResolver`; give the customer-facing site
its own authentication scheme or route set.

## Visitor identity tokens

Logged-in sites can identify live-chat visitors with an IdentityFlow-issued JWT instead of an HMAC signature
(`widget.client.identify("token", jwt)`):

```csharp
services.AddTicketFlow()
    .AddLiveChat()
    .AddIdentityFlowVisitorTokens(options =>                   // TicketFlow.Integrations.IdentityFlow.SignalR
    {
        options.Authority = "https://id.example.com";        // discovery: {Authority}/.well-known/openid-configuration
        options.Audiences.Add("shop-widget");                 // the client/resource the site requests the token for
        // options.AudienceResolver = (scope, widgetConfigurationId) => [...];   // per-widget audiences
    });
```

`IdentityFlowVisitorTokenValidator` validates the signature against the issuer's JWKS through
`TokenValidationParameters.ConfigurationManager` (discovery cached for `AutomaticRefreshInterval`, default 12 hours; a token naming an
unknown key triggers a refresh, at most every `RefreshInterval`, default 5 minutes, so issuer key rotation is picked up without a restart)
or static `IssuerSigningKeys`, plus the issuer (discovery issuer or `ValidIssuers`), audience, token type (`JWT` identity tokens or
`at+jwt` access tokens) and lifetime (expiry required, one minute clock skew). Unsigned, tampered, expired, foreign-issuer and
foreign-audience tokens, tokens over 8192 characters and an unreachable issuer all yield `VisitorTokenRejected`. Accepted tokens report
namespace `identityflow` (configurable), the `sub` claim (GUIDs normalized) and `display_name`/`name` as display name. OpenIddict encrypts
access tokens by default; use identity tokens or issue the widget's access tokens with encryption disabled. Tokens and validation details
are never logged.

## Verification

- `dotnet test tests/Integrations/TicketFlow.Integrations.IdentityFlow.Tests -c Release` — claim mapping (sessions, session-derived
  bearer tokens with unmet scopes, PATs, API keys by identity or key, client tokens, reserved identifiers), staff identity links and the
  unmapped-staff option, team snapshots, realtime workspace resolution, registration precedence, and token validation (valid, expired,
  wrong audience/issuer, wrong key, unsigned, tampered, oversized, discovery and JWKS fetch, key rotation, unreachable issuer).
- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` — the sample host maps its development identities through the same
  resolvers (API keys become `idf:` clients).
