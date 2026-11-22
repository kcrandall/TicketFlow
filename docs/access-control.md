# Access control

`TicketFlow.Access` is a provider-neutral authorization engine for support data.

## Model

- **Permission**: `resource:action` (e.g. `conversation:reply`, `conversation:assign`, `contact:export`, `note:read`, `report:view`). Feature packages register their permissions in a catalog; unknown permissions are rejected.
- **Role**: named set of permissions. Built-ins: Owner, Admin, Supervisor, Agent, LightAgent (notes only), Viewer. Workspaces may define custom roles.
- **Grant**: principal (user, team or API client) + role + resource scope (tenant, workspace, team, inbox) + optional conditions + optional validity window + optional justification. Grants inherit downward (workspace → team/inbox → conversation).
- **Deny**: explicit deny grants override any allow at the same or a broader scope.
- **Conditions**: attribute predicates over the resource and principal (`assignee == self`, `team in principal.teams`, `label != vip`, `within shift`). Conditions compile to both an in-memory evaluator and a store-neutral filter expression.
- **Masking**: field policies redact PII (contact email/phone, attributes marked sensitive) per role. Masking is applied server-side in reads, exports, search and assistant context.
- **Break-glass**: time-bound elevated grants require a reason and always emit a sensitive audit event.
- **Audit**: sensitive decisions (exports, merges, masked-field reveal, grant changes, break-glass) are recorded.

## Decisions

```csharp
public union AccessDecision(AccessAllowed, AccessDenied, AccessMasked);
```

`AccessMasked` allows the action but lists fields to redact. Callers switch exhaustively.

## Query filters

`IAccessQueryPlanner` turns a principal + permission + resource type into an `AccessFilter` expression tree (`closed` hierarchy). Stores translate it (PostgreSQL → SQL predicates over indexed scope/team/inbox/assignee columns). A filter that cannot be translated fails closed.
