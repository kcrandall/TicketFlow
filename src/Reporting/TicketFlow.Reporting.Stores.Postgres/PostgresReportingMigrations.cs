using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Reporting.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the reporting tables. Keys are fixed-size <c>tf_key_v1</c> SHA-256 keys over the
/// raw identifiers; cells are stored as microseconds since the Unix epoch (<c>cell_us</c>) so keys stay immutable expressions.
/// Never edit an applied step; append a new one.
/// </summary>
public static class PostgresReportingMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeKey = $"scope_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns})) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.reporting", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_report_conversations (
            {Scope}, conversation_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            inbox_id text NOT NULL, channel text NULL, created_at timestamptz NOT NULL, status smallint NOT NULL CHECK (status BETWEEN 1 AND 5),
            assignee_id text NULL, team_id text NULL, priority smallint NOT NULL CHECK (priority BETWEEN 1 AND 5), labels text[] NOT NULL,
            waiting_since timestamptz NULL, first_response_at timestamptz NULL, resolutions integer NOT NULL CHECK (resolutions >= 0),
            last_sequence bigint NOT NULL CHECK (last_sequence >= 0),
            revision bigint NOT NULL CHECK (revision > 0),
            conversation_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, conversation_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (conversation_key));
        CREATE INDEX tf_report_conversations_open ON tf_report_conversations (scope_key) WHERE status NOT IN (4, 5);

        CREATE TABLE tf_report_facts (
            {Scope}, source_id uuid NOT NULL, metric smallint NOT NULL CHECK (metric BETWEEN 1 AND 16),
            conversation_id text NOT NULL, occurred_at timestamptz NOT NULL, cell_us bigint NOT NULL,
            inbox_id text NOT NULL, team_id text NULL, agent_id text NULL, channel text NULL, priority smallint NOT NULL CHECK (priority BETWEEN 1 AND 5),
            labels text[] NOT NULL, value bigint NOT NULL CHECK (value >= 0), bin smallint NOT NULL CHECK (bin >= 0),
            format_version smallint NOT NULL CHECK (format_version = 1),
            fact_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, source_id::text, metric::text)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (fact_key));
        CREATE INDEX tf_report_facts_time ON tf_report_facts (scope_key, occurred_at, source_id, metric);
        CREATE INDEX tf_report_facts_cell ON tf_report_facts (scope_key, cell_us);
        CREATE INDEX tf_report_facts_retention ON tf_report_facts (occurred_at);

        CREATE TABLE tf_report_rollups (
            {Scope}, metric smallint NOT NULL CHECK (metric BETWEEN 1 AND 16), cell_us bigint NOT NULL,
            inbox_id text NOT NULL, team_id text NULL, agent_id text NULL, channel text NULL, priority smallint NOT NULL CHECK (priority BETWEEN 1 AND 5),
            label text NOT NULL, bin smallint NOT NULL CHECK (bin >= 0),
            count bigint NOT NULL CHECK (count > 0), total bigint NOT NULL, minimum bigint NOT NULL, maximum bigint NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            cell_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, metric::text, cell_us::text, inbox_id, team_id, agent_id, channel, priority::text, label, bin::text)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (cell_key));
        CREATE INDEX tf_report_rollups_query ON tf_report_rollups (scope_key, metric, cell_us);
        CREATE INDEX tf_report_rollups_retention ON tf_report_rollups (cell_us);
        """)]);
}
