using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Automation.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the automation tables. Keys use fixed-size <c>tf_key_v1</c> SHA-256 keys over the
/// raw identifiers; stores also compare the raw columns. Never edit an applied step; append one.
/// </summary>
public static class PostgresAutomationMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeKey = "scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL";
    private const string ScopeParts = "tenant_id, project_id, environment_id, app_id, workspace_id";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.automation", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_automation_rules (
            {Scope}, rule_id text COLLATE "C" NOT NULL, priority integer NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            definition text NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            next_run_at timestamptz NULL, lease_until timestamptz NULL, fence bigint NOT NULL CHECK (fence >= 0),
            rule_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, rule_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (rule_key));
        CREATE INDEX tf_automation_rules_scope ON tf_automation_rules (scope_key, priority, rule_id);
        CREATE INDEX tf_automation_rules_due ON tf_automation_rules (next_run_at, rule_key) WHERE next_run_at IS NOT NULL;

        CREATE TABLE tf_automation_executions (
            {Scope}, execution_id uuid NOT NULL, rule_id text COLLATE "C" NOT NULL, conversation_id text COLLATE "C" NULL, trigger_key text COLLATE "C" NOT NULL,
            started_at timestamptz NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            state text NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            execution_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, execution_id::text)) STORED NOT NULL,
            natural_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, rule_id, COALESCE(conversation_id, ''), trigger_key)) STORED NOT NULL,
            conversation_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, COALESCE(conversation_id, ''))) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (execution_key),
            UNIQUE (natural_key));
        CREATE INDEX tf_automation_executions_log ON tf_automation_executions (scope_key, started_at DESC, execution_id DESC);
        CREATE INDEX tf_automation_executions_conversation ON tf_automation_executions (conversation_key, started_at DESC, execution_id DESC);

        CREATE TABLE tf_automation_causes (
            {Scope}, conversation_id text COLLATE "C" NOT NULL, sequence bigint NOT NULL CHECK (sequence > 0),
            execution_id uuid NOT NULL, rule_id text COLLATE "C" NOT NULL, depth integer NOT NULL CHECK (depth > 0),
            cause_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, conversation_id, sequence::text)) STORED NOT NULL,
            PRIMARY KEY (cause_key));

        CREATE TABLE tf_automation_checkpoints (
            {Scope}, name text COLLATE "C" NOT NULL, value bigint NOT NULL CHECK (value >= 0),
            checkpoint_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, name)) STORED NOT NULL,
            PRIMARY KEY (checkpoint_key));

        CREATE TABLE tf_automation_macros (
            {Scope}, macro_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            definition text NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            macro_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, macro_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (macro_key));
        CREATE INDEX tf_automation_macros_scope ON tf_automation_macros (scope_key, macro_id);

        CREATE TABLE tf_automation_canned_responses (
            {Scope}, response_id text COLLATE "C" NOT NULL, shortcut text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            definition text NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            response_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, response_id)) STORED NOT NULL,
            shortcut_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, shortcut)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (response_key),
            CONSTRAINT tf_automation_canned_responses_shortcut UNIQUE (shortcut_key));
        CREATE INDEX tf_automation_canned_responses_scope ON tf_automation_canned_responses (scope_key, shortcut);
        """)]);
}
