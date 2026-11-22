using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Assist.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the assistant tables. Keys use fixed-size <c>tf_key_v1</c> SHA-256 keys over the
/// raw identifiers. Usage rows hold identifiers and token counts only. Version 2 adds per-workspace, per-UTC-day counters of
/// counted tokens (so quota checks never scan a day of usage rows) and the <c>estimated</c> settlement flag. Never edit an applied step; append a new one.
/// </summary>
public static class PostgresAssistMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeKey = $"scope_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns})) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.assist", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_assist_settings (
            {Scope},
            format_version smallint NOT NULL CHECK (format_version = 1),
            settings jsonb NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            {ScopeKey},
            PRIMARY KEY (scope_key));

        CREATE TABLE tf_assist_usage (
            {Scope}, usage_id uuid NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            feature smallint NOT NULL CHECK (feature BETWEEN 1 AND 6), model text NOT NULL,
            actor_kind smallint NOT NULL CHECK (actor_kind BETWEEN 1 AND 4), actor_id text NOT NULL, conversation_id text NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 3),
            reserved_tokens bigint NOT NULL CHECK (reserved_tokens >= 0),
            input_tokens bigint NOT NULL CHECK (input_tokens >= 0), output_tokens bigint NOT NULL CHECK (output_tokens >= 0),
            cache_read_tokens bigint NOT NULL CHECK (cache_read_tokens >= 0), cache_write_tokens bigint NOT NULL CHECK (cache_write_tokens >= 0),
            created_at timestamptz NOT NULL, completed_at timestamptz NULL,
            CHECK ((state = 1) = (completed_at IS NULL)),
            usage_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, usage_id::text)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (usage_key));
        CREATE INDEX tf_assist_usage_time ON tf_assist_usage (scope_key, created_at);
        """),
        new(2, "day-counters-and-estimates", $"""
        ALTER TABLE tf_assist_usage ADD COLUMN estimated boolean NOT NULL DEFAULT false;
        ALTER TABLE tf_assist_usage ADD CONSTRAINT tf_assist_usage_estimated_settled CHECK (NOT estimated OR state <> 1);
        CREATE INDEX tf_assist_usage_reserved ON tf_assist_usage (created_at) WHERE state = 1;

        CREATE TABLE tf_assist_usage_days (
            {Scope}, day date NOT NULL,
            tokens bigint NOT NULL CHECK (tokens >= 0),
            day_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, (day - DATE '1970-01-01')::text)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (day_key));

        INSERT INTO tf_assist_usage_days ({ScopeColumns}, day, tokens)
        SELECT {ScopeColumns}, (created_at AT TIME ZONE 'UTC')::date,
               SUM(CASE WHEN state = 1 THEN reserved_tokens ELSE input_tokens + output_tokens + cache_read_tokens + cache_write_tokens END)
        FROM tf_assist_usage GROUP BY {ScopeColumns}, (created_at AT TIME ZONE 'UTC')::date;
        """)]);
}
