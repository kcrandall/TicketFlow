# TicketFlow.Access

The TicketFlow access engine.

- `AccessEvaluator` decides point requests: the principal's scope must equal the resource scope; grants inherit downward
  (tenant → workspace → team/inbox → resources); any applicable deny overrides every allow; validity windows use the registered
  `TimeProvider`; unknown permissions, unsupported principals (automation/system) and missing attributes fail closed.
- Conditions are evaluated with three-valued logic (allow needs true; deny applies unless false).
- Field policies return `AccessMasked` with an ordered field list; `RevealAsync` requires the policy's reveal permission and is
  always audited without the field value.
- `AccessQueryPlanner` produces an `AccessFilter` whose matches equal point checks at the planning instant.
- `AccessAdministrator` authorizes (standard grants only), validates, fences and audits role, grant and field-policy changes,
  prevents role and grant escalation, requires tenant-scoped authority for tenant grants, and activates break-glass grants
  (justification required, bounded duration, auto-expiry, audited activation and use, including masking changes).

```csharp
services.AddTicketFlow()
    .AddAccessControl(options => options.MaximumBreakGlassDuration = TimeSpan.FromHours(1))
    .AddAccessPermissions(new PermissionDefinition("macro:run"))
    .AddInMemoryAccessStores(); // development only
```

Registrations use TryAdd and preserve application services; the evaluator, planner and administrator are scoped. The in-memory stores are bounded, process-local development examples
with no durability; use `TicketFlow.Access.Stores.Postgres` or an application-owned implementation in production.
