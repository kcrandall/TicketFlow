# TicketFlow.Access.Abstractions

Provider-neutral contracts for TicketFlow access control:

- `Permission` (`resource:action`, bounded and validated), `PermissionDefinition`, `PermissionCatalog` and
  `services.AddAccessPermissions(...)` for feature-owned permissions. Unknown permissions are always denied.
- Built-in immutable roles (`BuiltInRoles`: Owner, Admin, Supervisor, Agent, LightAgent, Viewer) and workspace-scoped custom
  `AccessRole` definitions with revisions.
- `AccessGrant`: subject (`GrantSubject` union of user, team or API client), role, `GrantScope` (tenant, workspace, team, inbox),
  allow/deny effect, optional bounded `AccessCondition` AST, validity window and justification, standard or break-glass kind.
- `AccessDecision` union (`AccessAllowed`, `AccessDenied`, `AccessMasked`), `RevealResult`, `AccessWriteResult<T>` and
  `AccessChangeResult<T>` unions. Switch over every case without a discard arm.
- `AccessFieldPolicy` masking, the `AccessFilter` closed hierarchy with the reference `AccessFilterEvaluator`, and the
  versioned `AccessConditionCodec` used by durable stores (unions are never persisted directly).
- Store contracts `IAccessRoleStore`, `IAccessGrantStore`, `IAccessFieldPolicyStore`, `IAccessAuditSink` with documented scope,
  revision-fencing and atomic capacity rules, plus `IAccessEvaluator`, `IAccessQueryPlanner` and `IAccessAdministrator`.

Behavior lives in `TicketFlow.Access`; durable storage in `TicketFlow.Access.Stores.Postgres`.
