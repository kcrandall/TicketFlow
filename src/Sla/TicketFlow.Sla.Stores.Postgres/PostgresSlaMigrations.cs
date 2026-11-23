using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Sla.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the SLA tables. Keys use fixed-size <c>tf_key_v1</c> SHA-256 keys over the raw
/// identifiers; stores also compare the raw columns. The timer table is indexed by <c>due_at</c> for leased claims; risk lists are
/// index-backed by <c>(scope_key, [inbox_id,] urgency_at, conversation_id)</c>. Never edit an applied step; append one.
/// </summary>
public static class PostgresSlaMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeKey = "scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.sla", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_sla_policies (
            {Scope}, policy_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            definition text NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            policy_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, policy_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (policy_key));
        CREATE INDEX tf_sla_policies_scope ON tf_sla_policies (scope_key, policy_id);

        CREATE TABLE tf_sla_trackers (
            {Scope}, conversation_id text COLLATE "C" NOT NULL, inbox_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            state text NOT NULL,
            team_id text COLLATE "C" NULL, assignee_id text COLLATE "C" NULL, labels text[] NOT NULL, access_attributes jsonb NOT NULL,
            due_at timestamptz NULL, at_risk_at timestamptz NULL, breached boolean NOT NULL, urgency_at timestamptz NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            tracker_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (tracker_key),
            CHECK ((urgency_at IS NULL) = (NOT breached AND due_at IS NULL)));
        CREATE INDEX tf_sla_trackers_urgency ON tf_sla_trackers (scope_key, urgency_at, conversation_id) WHERE urgency_at IS NOT NULL;
        CREATE INDEX tf_sla_trackers_inbox_urgency ON tf_sla_trackers (scope_key, inbox_id, urgency_at, conversation_id) WHERE urgency_at IS NOT NULL;

        CREATE TABLE tf_sla_timers (
            tracker_key bytea PRIMARY KEY REFERENCES tf_sla_trackers (tracker_key) ON DELETE CASCADE,
            {Scope}, conversation_id text NOT NULL,
            due_at timestamptz NULL, lease_until timestamptz NULL, fence bigint NOT NULL CHECK (fence >= 0));
        CREATE INDEX tf_sla_timers_due ON tf_sla_timers (due_at, tracker_key) WHERE due_at IS NOT NULL;
        """), new(2, "at-risk-index", """
        CREATE INDEX tf_sla_trackers_at_risk ON tf_sla_trackers (scope_key, at_risk_at) WHERE at_risk_at IS NOT NULL AND NOT breached;
        """), new(3, "perf-indexes", """
        CREATE INDEX tf_sla_trackers_labels ON tf_sla_trackers USING gin (labels);
        CREATE INDEX tf_sla_timers_due_v2 ON tf_sla_timers (due_at, tracker_key) INCLUDE (lease_until) WHERE due_at IS NOT NULL;
        CREATE INDEX tf_sla_trackers_urgency_v2 ON tf_sla_trackers (scope_key, urgency_at, conversation_id) INCLUDE (inbox_id, team_id, assignee_id, breached, at_risk_at) WHERE urgency_at IS NOT NULL;
        """)]);
}
