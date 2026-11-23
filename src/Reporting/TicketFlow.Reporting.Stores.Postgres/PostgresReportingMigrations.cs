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
        """),
        // Version 2: labels are no longer rolled up (one rollup row per fact instead of 1 + labels); label reports read the facts
        // through a partial index. The retention row tracks the fact horizon (rebuilds before it are refused); it is backfilled
        // conservatively: when rollups predate every remaining fact, facts were pruned up to the oldest remaining fact.
        new(2, "label-rollups-from-facts", """
        CREATE TABLE tf_report_retention (
            id smallint NOT NULL PRIMARY KEY CHECK (id = 1), fact_horizon timestamptz NULL,
            format_version smallint NOT NULL CHECK (format_version = 1));
        INSERT INTO tf_report_retention (id, fact_horizon, format_version)
        SELECT 1, CASE
            WHEN NOT EXISTS (SELECT 1 FROM tf_report_rollups) THEN NULL
            WHEN NOT EXISTS (SELECT 1 FROM tf_report_facts) THEN to_timestamp(((SELECT max(cell_us) FROM tf_report_rollups) + 900000000) / 1000000.0)
            WHEN (SELECT min(cell_us) FROM tf_report_rollups) < (SELECT min(cell_us) FROM tf_report_facts) THEN (SELECT min(occurred_at) FROM tf_report_facts)
            ELSE NULL END, 1;
        DELETE FROM tf_report_rollups WHERE label <> '';
        ALTER TABLE tf_report_rollups ADD CONSTRAINT tf_report_rollups_unlabelled CHECK (label = '');
        DROP INDEX tf_report_rollups_query;
        CREATE INDEX tf_report_rollups_query_v2 ON tf_report_rollups (scope_key, metric, cell_us) WHERE label = '';
        CREATE INDEX tf_report_facts_labelled ON tf_report_facts (scope_key, metric, cell_us) WHERE cardinality(labels) > 0;
        """),
        new(3, "perf-indexes", """
        CREATE INDEX tf_report_rollups_scope_cell ON tf_report_rollups (scope_key, cell_us) WHERE label = '';
        CREATE INDEX tf_report_conversations_open_v2 ON tf_report_conversations (scope_key) INCLUDE (inbox_id, team_id, assignee_id, channel, priority) WHERE status NOT IN (4, 5);
        """)]);
}
