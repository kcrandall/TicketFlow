# Access control

`TicketFlow.Access` is a provider-neutral authorization engine for support data. It ships as three packages:

| Package | Contents |
| --- | --- |
| `TicketFlow.Access.Abstractions` | Permissions and catalog, roles, grants, conditions, decisions, masking policies, query filters, audit events, store and service contracts. |
| `TicketFlow.Access` | `AccessEvaluator`, `AccessQueryPlanner`, `AccessAdministrator`, `AccessGrantValidator`, DI registration and development in-memory stores. |
| `TicketFlow.Access.Stores.Postgres` | `PostgresAccessStore` (roles, grants, field policies, audit), `PostgresAccessMigrations` and `PostgresAccessFilterTranslator`. |

```csharp
services.AddTicketFlow()
    .AddAccessControl(options => options.MaximumBreakGlassDuration = TimeSpan.FromHours(1))
    .AddAccessPermissions(new PermissionDefinition("macro:run"))
    .AddPostgresAccessStores(dataSource);   // or .AddInMemoryAccessStores() for development
await new PostgresAccessStore(dataSource).InitializeAsync();   // explicit schema creation
```

All registrations use TryAdd: application-supplied services registered earlier win, and repeated calls are safe.
`AddAccessControl` selects no store. The catalog is a singleton; the evaluator, planner and administrator are scoped, so they may
depend on scoped application stores. `TicketFlowBuilder` lives in `TicketFlow.Core`; feature packages never reference the
`TicketFlow` meta-package.

## Model

- **Permission**: `Permission` is a validated `resource:action` value (each segment 1-64 characters of `a-z 0-9 - _ .`,
  starting with a letter). `default(Permission)` is uninitialized and never granted. Built-in permissions live in
  `AccessPermissions` (`conversation:read|reply|assign|resolve|manage|delete`, `note:read|write`,
  `contact:read|update|export|merge|reveal`, `report:view`, `inbox:read|manage`, `team:read|manage`, `workspace:read|manage`,
  `calendar:manage`, `access:manage`, `access:manage-tenant`, `access:audit-read`, `access:break-glass`).
- **Catalog**: `PermissionCatalog` holds the built-ins plus feature registrations (`services.AddAccessPermissions(...)`).
  Registering an identical `PermissionDefinition` twice is idempotent; a conflicting definition throws, naming the first
  conflicting permission in ordinal order. Definitions flagged `IsSensitive` audit every decision. Permissions missing from the
  catalog are always denied (`AccessDenialReason.UnknownPermission`), even for Owners.
- **Roles**: `BuiltInRoles` — Owner (every catalog permission, including feature permissions), Admin (every built-in permission
  except `access:manage-tenant` and `workspace:manage`), Supervisor, Agent, LightAgent (conversation read plus notes), Viewer. Every built-in role holds `workspace:read`, `inbox:read` and `team:read`. Built-ins
  are immutable and resolved without a store; stores reject writes to built-in IDs. Custom `AccessRole`s are workspace-scoped,
  bounded (512 permissions) and revisioned by `IAccessRoleStore`; they cannot reuse built-in IDs.
- **Grant**: `AccessGrant` = `GrantId` + `AccessPartition` (tenant → project → environment → app) + `GrantSubject`
  (`public union GrantSubject(UserId, TeamId, ApiClientId);`) + `RoleId` + `GrantScope` (closed: `TenantGrantScope`,
  `WorkspaceGrantScope`, `TeamGrantScope`, `InboxGrantScope`) + `GrantEffect` (Allow/Deny) + optional `AccessCondition` +
  optional validity window `[ValidFrom, ValidUntil)` + optional justification (≤ 1024 chars) + `GrantKind` (Standard/BreakGlass).
  Team subjects apply to users whose `UserPrincipal.Teams` snapshot contains the team. Automation and system principals cannot
  hold grants and are denied (`UnsupportedPrincipal`).
- **Inheritance**: tenant grants apply in every workspace of the partition (built-in roles only); workspace grants apply to every
  resource in the workspace; team/inbox grants apply to resources whose `TeamId`/`InboxId` matches.
- **Scope**: the principal's `TicketScope` must equal the resource's (`ScopeMismatch` otherwise). Grants are loaded only for the
  principal's subjects from its partition, tenant-wide or naming its workspace, so grants never cross a scope level.
- **Deny**: any applicable deny grant whose role includes the permission overrides every allow — at the same, a broader or a
  narrower scope. A narrower allow never punches through a broader deny. A deny grant whose role cannot be resolved (deleted
  custom role) denies every permission.
- **Validity**: evaluated with the registered `TimeProvider`; grants outside their window are ignored. An expired deny stops
  denying immediately.
- **Store trust**: if a grant store returns a grant outside the requested partition, workspace or subjects, the engine throws
  instead of deciding.

## Conditions

`AccessCondition` is a `closed` record AST: `AndCondition`, `OrCondition`, `NotCondition`, `AssigneeIsSelfCondition`,
`TeamInPrincipalTeamsCondition`, `AttributeEqualsCondition`, `AttributeInCondition` (1-64 values), `HasLabelCondition`,
`TimeWindowCondition` (UTC, wraps midnight). Trees are bounded at construction: depth ≤ 8, ≤ 64 nodes.

Evaluation is three-valued (Kleene) over the `AccessResource` snapshot. `TeamId`, `InboxId` and `AssigneeId` are authoritative
(`null` means none). An attribute key missing from `Attributes`, `Labels == null`, or a self/team reference for a non-user
principal is **unknown**. An allow grant applies only when its condition is true; a deny grant applies unless its condition is
false. Missing data therefore fails closed in both directions.

Durable stores persist conditions with `AccessConditionCodec`: `{"v":1,"c":node}` with an explicit `"op"` discriminator per
node. Decoding enforces the same bounds and rejects unknown versions, discriminators and properties.

## Decisions

```csharp
public union AccessDecision(AccessAllowed, AccessDenied, AccessMasked);

public interface IAccessEvaluator
{
    Task<AccessDecision> AuthorizeAsync(AccessRequest request, CancellationToken cancellationToken = default);
    Task<RevealResult> RevealAsync(RevealRequest request, CancellationToken cancellationToken = default);
}
```

`AccessRequest(PrincipalRef, Permission, AccessResource)`. `AccessDenied` carries an `AccessDenialReason` (`UnknownPermission`,
`ScopeMismatch`, `UnsupportedPrincipal`, `NoMatchingGrant`, `ExplicitDeny`). `AccessMasked` permits the action but lists
`MaskedField`s to redact. `AccessDecision.IsPermitted` is true for allowed and masked. Callers switch exhaustively without a
discard arm. Other closed outcomes are unions too: `RevealResult(RevealGranted, RevealRefused)`,
`GrantValidationResult(GrantValid, GrantInvalid)`, `AccessWriteResult<T>(AccessWriteApplied<T>, AccessWriteNotFound,
AccessWriteConflict, AccessWriteRejected)` and `AccessChangeResult<T>(AccessChangeApplied<T>, AccessChangeRefused)`. Unions are
never persisted directly.

## Masking

`AccessFieldPolicy(FieldPath field, appliesTo, unmaskedRoles, revealPermission)` is stored per workspace. When a permission in
`AppliesTo` is allowed, the field is masked unless an applicable allow grant for that permission uses one of `UnmaskedRoles`.
`AccessMasked.Fields` is ordered by path (ordinal); `MaskedField.Revealable` reports whether the principal holds the policy's
reveal permission. `RevealAsync` reveals one field only when the read permission is allowed, a policy applies and the reveal
permission is allowed. Every reveal call, granted or refused, records one audit event containing the field name — never the value.
Masking is applied server-side by feature packages in reads, exports, search and assistant context.

## Query filters

```csharp
public interface IAccessQueryPlanner
{
    Task<AccessFilter> PlanAsync(AccessQuery query, CancellationToken cancellationToken = default);
}
```

`AccessFilter` is a `closed` hierarchy: `AllowAllFilter`, `DenyAllFilter`, `UnknownFilter`, `ScopeFilter`, `AndFilter`, `OrFilter`,
`NotFilter`, `TeamInFilter`, `InboxInFilter`, `AssigneeIsFilter`, `HasLabelFilter`, `AttributeEqualsFilter`, `AttributeInFilter`
(≤ 4096 nodes). Unconditional team/inbox membership siblings are coalesced (`team∈A ∨ team∈B` → `team∈A∪B`, likewise for
negated memberships). A plan that would exceed the node limit fails closed: the planner returns `DenyAllFilter` and records a
`FilterPlanned` audit event with `AccessDenialReason.FilterLimitExceeded`. The plan is `scope ∧ (allow₁ ∨ … ∨ allowₙ) ∧ ¬deny₁ ∧ … ∧ ¬denyₘ`, with self/team references bound to the
principal and validity and time windows evaluated at plan time. Semantics are Kleene logic like SQL: a row is included only when
the filter is true. `AccessFilterEvaluator.Matches(filter, resource)` is the reference in-memory translator, and for every
resource it equals `AuthorizeAsync(...).IsPermitted` at the same instant (verified over seeded randomized fixtures). Any
precheck failure yields `DenyAllFilter`.

`PostgresAccessFilterTranslator.Translate(filter, columns)` emits a parameterized predicate over the application's indexed
scope/team/inbox/assignee columns and optional `text[]` labels and `jsonb` attributes. Values are always bound parameters and
column names are validated identifiers. A node whose column is unmapped makes the whole result
`PostgresAccessFilterUntranslatable`; `OrDenyAll()` substitutes `FALSE`. A filter is never weakened to allow-all.

## Administration, break-glass and audit

`IAccessAdministrator` authorizes, validates, stores and audits changes to roles, grants and field policies (one audit event per
call). Every authorization uses standard grants only; break-glass grants never confer administrative authority.

- Roles and field policies are managed in the actor's workspace and require `access:manage`.
- No role escalation: creating a role requires holding every permission in it at workspace scope; updating requires holding every
  permission of both the current and the new definition, and deleting requires holding every current permission, so the actor
  could have created every grant that references the role.
- Grants require `access:manage` on their target (team/inbox/workspace). Tenant grants require `access:manage-tenant` from a
  **tenant-scoped** allow grant (a workspace-scoped Owner cannot manage tenant grants), and their escalation check also counts only
  tenant-scoped allows.
- No grant escalation: an allow grant requires the actor to hold every permission of the granted role on the target.
- Updates, revokes and role changes are fenced first: a missing record is `NotFound` and a revision mismatch is `Conflict` (with
  the current revision) before any authorization decision is made on the stored record.
- Audit for administrative changes is recorded after the store write (not in one transaction); if the sink throws, the change is
  applied and the call throws.

`ActivateBreakGlassAsync(BreakGlassRequest)` creates a `GrantKind.BreakGlass` allow grant for the requester itself. It requires
`access:break-glass` on the target (standard grants only), a justification of at least `MinimumBreakGlassJustificationLength`
(default 10) characters, a duration within `MaximumBreakGlassDuration` (default 4 h, ceiling 24 h), a workspace/team/inbox scope
in the requester's workspace, and a role without `access:manage`, `access:manage-tenant` or `access:break-glass` (so not Owner or
Admin). The grant expires automatically through its validity window and cannot be updated, only revoked; `CreateGrantAsync`
rejects `GrantKind.BreakGlass`. Activation and every decision or plan that relies on a break-glass grant are audited. Masking and
reveal are decided from standard grants first; when a break-glass grant unmasks a field, makes it revealable or authorizes a
reveal, the decision is flagged `UsedBreakGlass` and audited.

`IAccessAuditSink` records `AccessAuditEvent`s: decisions and plans for sensitive permissions, reveals, break-glass activation and
use, and every administrative change. Events carry identifiers, permission and field names, outcome, denial reason and the
operator justification — never message bodies, contact addresses, attribute or masked values, credentials or tokens. Sinks must
persist before returning and throw on failure; the engine then fails the operation rather than return an unaudited decision.

## Store contracts

| Contract | Key and scope | Guarantees |
| --- | --- | --- |
| `IAccessRoleStore` | `TicketScope` + `RoleId` | Built-in IDs rejected; create at revision 1; compare-and-set update/delete; ≤ 128 roles per workspace, checked atomically. |
| `IAccessGrantStore` | `AccessPartition` + `GrantId` | Lists tenant grants plus grants naming the workspace, for the requested subjects (≤ 257) only; compare-and-set update/delete; ≤ 256 grants per subject, checked atomically. |
| `IAccessFieldPolicyStore` | `TicketScope` + `FieldPath` | Compare-and-set; ≤ 256 policies per workspace. |
| `IAccessAuditSink` | event ID | Durable before returning; failures throw. |

The shared contract fixture `tests/Access/TicketFlow.Access.StoreContracts` verifies these against the in-memory stores and
PostgreSQL. In-memory stores (`InMemoryAccessRoleStore`, `InMemoryAccessGrantStore`, `InMemoryAccessFieldPolicyStore`,
`InMemoryAccessAuditSink`) are bounded, process-local development examples with no durability.

`PostgresAccessStore.InitializeAsync` explicitly applies `PostgresAccessMigrations.Catalog` with the
`TicketFlow.Stores.Migrations.Postgres` runner (append-only, checksummed history in `tf_store_migrations`, advisory lock; unknown
future versions, edited steps and downgrades fail). Identifiers may be 256 UTF-16 units each, so every primary key and index uses
fixed-size `tf_key_v1` SHA-256 keys generated from the raw columns (role, grant, policy, scope and subject keys); stores also compare
the raw columns. Rows carry explicit `format_version` and numeric discriminators (subject kind, scope level, effect, grant kind);
every statement is parameterized; rows that fail to decode throw.

PostgreSQL tests are inconclusive without `TICKETFLOW_TEST_POSTGRES`; `TICKETFLOW_REQUIRE_POSTGRES=1` (set in CI and
`scripts/test-postgres.sh`) turns that into a failure.
