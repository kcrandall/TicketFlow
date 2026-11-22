# T07 — Blazor management UI (replaces the React/TanStack console)

Epic: [E11 — Hosting and agent console](../PLAN.md)

Status: **Completed**

## Goal

`src/UI/TicketFlow.UI.Blazor`: an optional Razor Class Library that host apps import to get a complete support console — a shadcn-style
dashboard, dark by default and themeable — rendered with **static SSR** (Blazor Web App, .NET 11) with enhanced navigation and forms.
It replaces the planned React/TanStack SPA: the UI calls the feature services directly (no HTTP hop, no generated client), inherits
the host's authentication and gates every section with TicketFlow Access.

Scope:

- `services.AddTicketFlowBlazorUI(options => { BasePath, ProductName, Workspace/ResolveWorkspace, LoginPath, LogoutPath, DefaultTheme,
  Themes, Stylesheets, ShowSidebar, ShowDisabledSections, EnableRealtime, ... })`, `app.MapTicketFlowBlazorUI(pages: true)` (GET pages,
  POST form actions, CSV export) and the embeddable `<TicketFlowUI Path="..." />` for hosts with their own Blazor layout.
- Authentication: the host's `HttpContext.User`, mapped by the registered `ITicketFlowPrincipalResolver` within the
  `ITicketFlowPartitionResolver` partition (no parallel mechanism); configurable sign-in/sign-out URLs; the UI never signs users in.
- Navigation (sidebar like shadcn dashboard-01) gated by a workspace-level `IAccessEvaluator.LoadDecisionContextAsync` snapshot;
  sections of unregistered features are hidden.
- Pages: dashboard, conversations (list + detail with timeline, composer, side panel, SLA, macros, assist), contacts (list, detail,
  identities, audited reveal, merge), inboxes, teams, business hours, channels and live-chat widgets, routing, SLA policies and risk list,
  automation (rules editor with dry run, macros, canned responses), help center (portals, collections, article editor with preview),
  surveys, reports (overview, leaderboard, trends, CSV export), access (roles, grants, field policies, break-glass), settings and theme.
- Forms: SSR posts with antiforgery, expected-revision fields, union results mapped to friendly outcomes, post/redirect/get.
- Theming: shadcn CSS custom-property tokens, built-in themes, cookie-persisted switcher (no flash), host themes via options and CSS.
- Components: BlazorBlueprint.Components (4.2.0) and BlazorBlueprint.Icons.Lucide (2.0.2); native SSR markup where interactivity
  would be needed.
- Live conversation updates (timeline refresh, typing) over the existing SignalR agent hub; SSR works without it.
- Sample host wiring at `/support` with development sign-in and realistic seeded demo data; screenshots in `docs/ui/screenshots/`.

## Dependencies

E11 T01–T06, E3–E10, E12–E14.

## Acceptance criteria

- Every page renders through static SSR for an authorized user; unauthenticated users are redirected to the host sign-in; users without
  the section's permission get 403 and do not see it in navigation.
- Writes use the feature services with expected revisions; conflicts, forbidden and validation outcomes render as alerts and keep the
  submitted values; successful writes redirect with a one-time flash.
- Revealed values, AI drafts and dry-run results are rendered once with `Cache-Control: no-store` and never stored in cookies or URLs.
- Dark mode is the default; the theme cookie drives the server-rendered theme; hosts can add themes.
- bUnit component tests and TestServer smoke tests pass; screenshots captured at 1440×900.

## Verification

- `dotnet test tests/UI/TicketFlow.UI.Blazor.Tests -c Release`
- `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` (PostgreSQL case with `TICKETFLOW_TEST_POSTGRES`).
- `dotnet run --project samples/TicketFlow.SampleHost` and open `/support`.

## Risks and boundaries

The UI is optional and replaceable; the REST endpoints remain the integration surface for other clients. Static SSR keeps no
per-user server state; the only script-driven island is the realtime timeline. Access audit browsing needs an audit reader contract
that Access does not expose yet.

## Results

Completed 2026-11-22.

- Package `TicketFlow.UI.Blazor` (Razor SDK, packable; in `scripts/nuget-projects.tsv`): `TicketFlowBlazorUIOptions`,
  `AddTicketFlowBlazorUI` (service collection and `TicketFlowBuilder`), `MapTicketFlowBlazorUI(bool pages = true)`, `TicketFlowUI`,
  `TicketFlowTheme`/`TicketFlowThemes`/`TicketFlowThemePreference`, `ITicketFlowUserDirectory`, `TicketFlowUISession`, `UiOutcome`/`UiOutcomes`,
  28 routed pages, `tf-themes.css`, `tf-ui.css`, `tf-ui.js`, `tf-live.js`. Central versions added: `BlazorBlueprint.Components` 4.2.0,
  `BlazorBlueprint.Icons.Lucide` 2.0.2, `bunit` 2.14.0.
- Sample host: `/support` mounted with `LoginPath=/dev/login`; the development handler also accepts the `tf-dev-user` cookie set by
  `/dev/login`; `AddReporting()` with in-memory/PostgreSQL stores; `SampleUsers` directory; `SampleClock`; `SampleDemoData` (inboxes,
  teams, staff, contacts, 13 days of history, 15 current conversations, SLA/routing policies, automation, help center, CSAT responses);
  `RequiresAspNetWebAssets` for `blazor.web.js`; static assets mapped only when the manifest exists (test hosts).
- `dotnet test tests/UI/TicketFlow.UI.Blazor.Tests -c Release` — 52 passed (18 bUnit/unit tests: navigation gating, theme cookie
  rendering, outcome mapping and alerts, flash cookie, return-URL safety, sensitive-permission cache; 34 TestServer smoke tests over 26
  pages, sign-in redirect, 403s, antiforgery, PRG flash, revision conflict, forbidden write, theme post, CSV export).
- With `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55931;...` and `TICKETFLOW_REQUIRE_POSTGRES=1` against an isolated `postgres:18`
  container (`tf-pg-e11ui`): `dotnet test tests/Hosting/TicketFlow.SampleHost.Tests -c Release` — 4 passed (demo data seeds twice on
  PostgreSQL); the screenshots were taken from the PostgreSQL-backed sample host.
- `dotnet test tests/Hosting/TicketFlow.Hosting.AspNetCore.Tests -c Release` — 18 passed; `dotnet build TicketFlow.slnx -c Release` —
  0 warnings, 0 errors; `dotnet pack src/UI/TicketFlow.UI.Blazor -c Release` succeeded.
- Browser checks (Playwright, Chromium headless): enhanced navigation, enhanced form post with flash, live typing indicator and timeline
  refresh between two signed-in agents.
- Screenshots: `docs/ui/screenshots/*.png` (17 full-page captures at 1440×900).

Review fixes (2026-11-22):

- P1 authorization: GET and POST share one gate (`UiNavigation.Allows`) decided from the section alone; a POST to a section the viewer
  cannot open renders "Access denied" (403) without running the action, and actions run only on their own section (400 otherwise).
  Access pages list through `IAccessAdministrator.ListRolesAsync/ListGrantsAsync/ListFieldPoliciesAsync` (fresh `access:manage`).
- P2 scoped agents: new additive Access API `IAccessQueryPlanner.HeldAnywhereAsync(principal, scope, permissions)` (default interface
  method planning each permission; `AccessQueryPlanner` overrides it with one grant load) — shared with the MCP fixer. The navigation
  snapshot uses it, so team- and inbox-scoped grants count.
- P1 N+1: additive `IContactService.GetManyAsync` (default loop; `ContactService` uses one decision context and one masking load),
  `ITicketFlowUserDirectory.FindManyAsync`, SLA badges from one risk-list read, row summaries from the list projection (no timeline
  reads), dashboard/inbox/team counts from reporting's open backlog, no per-team member reads on the conversation page.
- P3: conversation commands require `_revision`; contact merge is a two-step review/confirm fenced on both revisions; return URLs with
  control characters are rejected; reveal, AI draft and dry-run responses are verified `no-store`.
- Per-request grant caching was not added: grants are loaded inside the scoped Access evaluator used by every host, and a UI-only
  decorator would change store lifetimes; services remain the per-call authority.
- `dotnet test tests/UI/TicketFlow.UI.Blazor.Tests -c Release` — 60 passed (adds: POST route sweep for a viewer over 17 section pages,
  cross-section action 400, team-scoped agent conversations/dashboard, `HeldAnywhereAsync` with team grant and workspace deny, query
  count for the conversations page (grant loads constant per page), missing revision 422, merge source fencing 409, no-store on reveal /
  AI draft / dry run, control characters in return URLs).
- `TicketFlow.SampleHost.Tests` against an isolated `postgres:18` container (`tf-pg-e11ui`): 4 passed; `dotnet build TicketFlow.slnx -c Release`
  — 0 warnings, 0 errors. Screenshots re-captured from the PostgreSQL-backed sample host (same 17 names).

