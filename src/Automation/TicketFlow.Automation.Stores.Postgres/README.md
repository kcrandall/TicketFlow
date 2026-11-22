# TicketFlow.Automation.Stores.Postgres

Durable PostgreSQL storage for TicketFlow automation: `PostgresAutomationStore` implements `IAutomationRuleStore`,
`IAutomationExecutionStore`, `IMacroStore` and `ICannedResponseStore`.

```csharp
services.AddTicketFlow().AddAutomation().AddPostgresAutomationStores(dataSource);
await new PostgresAutomationStore(dataSource).InitializeAsync();   // explicit schema creation
```

- Tables `tf_automation_rules` (with schedule timer columns), `tf_automation_executions`, `tf_automation_causes`,
  `tf_automation_checkpoints`, `tf_automation_macros` and `tf_automation_canned_responses`, created only by `InitializeAsync`
  through the append-only `ticketflow.automation` migration catalog; `tf_key_v1` keys plus raw-column comparison; `format_version`
  on every row; definitions and execution state stored as versioned `AutomationCodec` JSON.
- Execution records are unique on a hashed (rule, conversation, trigger key) natural key; updates write the cause index in the same
  transaction. Schedule timers are claimed with `FOR UPDATE SKIP LOCKED` plus a lease and fence. Capacities are checked under
  workspace advisory locks; canned-response shortcuts are unique per workspace.
