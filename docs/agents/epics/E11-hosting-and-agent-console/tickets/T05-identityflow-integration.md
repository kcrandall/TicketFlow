# T05 — IdentityFlow integration

Epic: [E11 — Hosting and agent console](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.Integrations.IdentityFlow` referencing published IdentityFlow packages (pinned central version): a claims mapper and
`IdentityFlowPrincipalResolver` (both `ITicketFlowPrincipalResolver` and SignalR `IRealtimePrincipalResolver`) mapping IdentityFlow
browser sessions and bearer tokens (`sub`), personal access tokens (users) and API keys / OAuth client tokens (API clients by default)
to `UserPrincipal` (team snapshot from `ITeamMembershipResolver`) or `ApiClientPrincipal`; and `IdentityFlowVisitorTokenValidator`
(`IVisitorTokenValidator`) validating IdentityFlow/OpenIddict-issued JWTs (issuer discovery/JWKS, audience, lifetime, signature).

## Dependencies

E11 T01, E7.

## Acceptance criteria

- Unauthenticated users and unmappable claims resolve to no principal; subjects are normalized (GUID "D" form).
- The validator never logs tokens and rejects expired, wrong-issuer, wrong-audience and unsigned tokens.

## Verification

- `dotnet test tests/Integrations/TicketFlow.Integrations.IdentityFlow.Tests -c Release`.

## Risks and boundaries

IdentityFlow itself is not changed unless the published API lacks something essential.

## Results

Implemented `TicketFlow.Integrations.IdentityFlow` over the published `IdentityFlow.Hosting.AspNetCore.Authentication`
`0.1.0-alpha.20260826115343` (latest alpha on nuget.org): `IdentityFlowClaimsMapper`, `IdentityFlowPrincipalResolver` (REST and SignalR),
`AddIdentityFlowPrincipals()` (replaces only the option fallbacks), `IdentityFlowVisitorTokenValidator` (discovery/JWKS or static keys,
issuer, audience incl. per-widget resolver, type, lifetime) and `AddIdentityFlowVisitorTokens()`. IdentityFlow itself was not changed.

Verification (2026-11-22):

- `dotnet test tests/Integrations/TicketFlow.Integrations.IdentityFlow.Tests -c Release` — 11 passed, 0 failed.
- `dotnet pack src/Integrations/TicketFlow.Integrations.IdentityFlow -c Release -p:Version=0.1.0-alpha.1` — package created (stable versions
  fail NU5104 on the prerelease dependency, as for the EnvelopeFlow adapter).

Review fixes (2026-11-22): split into `TicketFlow.Integrations.IdentityFlow` (endpoints/UI/MCP resolver) and
`TicketFlow.Integrations.IdentityFlow.SignalR` (`IdentityFlowRealtimePrincipalResolver`, visitor tokens); fallbacks are replaced by type.
Browser sessions are recognized only by the cookie authentication type or a session id without client id/authentication method, so
session-derived bearer tokens must satisfy `RequiredScopes`; `ApiClientIdPrefix` defaults to `idf:` and `ReservedApiClientIds` are
refused; `ApiKeyClients = Method` maps per key; `IStaffIdentityResolver` is consulted first (`UnmappedStaff` fallback or refuse); the
visitor validator uses `TokenValidationParameters.ConfigurationManager` and retries after a refresh on unknown keys (rotation). Tests:
`BearerTokensCarryingASessionIdStillNeedTheRequiredScope`, `MappedCallersNeverReceiveReservedWorkerIdentities`,
`StaffIdentityLinksWinAndUnmappedUsersFollowTheOption`, `RotatedIssuerKeysArePickedUpWithoutRestart`.
`dotnet test tests/Integrations/TicketFlow.Integrations.IdentityFlow.Tests -c Release` — 15 passed.
