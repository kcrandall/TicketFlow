using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Routing.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the routing tables. Keys use fixed-size <c>tf_key_v1</c> SHA-256 keys over the raw
/// identifiers; stores also compare the raw columns. Every fence comes from one sequence, so fences never repeat when rows are deleted
/// and recreated. Never edit an applied step; append one.
/// </summary>
public static class PostgresRoutingMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeKey = "scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL";
    private const string ConversationKey = "conversation_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id)) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.routing", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE SEQUENCE tf_routing_fences AS bigint;

        CREATE TABLE tf_routing_policies (
            {Scope}, policy_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            document text NOT NULL, revision bigint NOT NULL CHECK (revision > 0),
            policy_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, policy_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (policy_key));
        CREATE INDEX tf_routing_policies_scope ON tf_routing_policies (scope_key, policy_id);

        CREATE TABLE tf_routing_agents (
            {Scope}, agent_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            default_capacity integer NOT NULL CHECK (default_capacity BETWEEN 0 AND 1000), settings text NOT NULL,
            status smallint NOT NULL CHECK (status BETWEEN 1 AND 4), changed_at timestamptz NOT NULL,
            last_heartbeat_at timestamptz NULL, expires_at timestamptz NULL,
            reassign_pending boolean NOT NULL, reassign_fence bigint NOT NULL, reassign_lease_until timestamptz NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            agent_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, agent_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (agent_key),
            CHECK ((status = 4) = (expires_at IS NULL)));
        CREATE INDEX tf_routing_agents_scope ON tf_routing_agents (scope_key, agent_id);
        CREATE INDEX tf_routing_agents_expiry ON tf_routing_agents (expires_at) WHERE status <> 4;
        CREATE INDEX tf_routing_agents_reassign ON tf_routing_agents (agent_key) WHERE reassign_pending;

        CREATE TABLE tf_routing_assignments (
            {Scope}, conversation_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            agent_id text COLLATE "C" NOT NULL, inbox_id text NOT NULL, contact_id text NOT NULL, channel_kind text NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 2), reserved_until timestamptz NULL,
            conversation_revision bigint NOT NULL CHECK (conversation_revision > 0), updated_at timestamptz NOT NULL,
            {ConversationKey},
            {ScopeKey},
            PRIMARY KEY (conversation_key),
            CHECK ((state = 1) = (reserved_until IS NOT NULL)));
        CREATE INDEX tf_routing_assignments_agent ON tf_routing_assignments (scope_key, agent_id, channel_kind);
        CREATE INDEX tf_routing_assignments_agent_list ON tf_routing_assignments (scope_key, agent_id, conversation_id);

        CREATE TABLE tf_routing_load_anchors (anchor_key bytea PRIMARY KEY);

        CREATE TABLE tf_routing_cursors (
            {Scope}, cursor_name text NOT NULL, last_agent_id text NULL,
            cursor_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, cursor_name)) STORED NOT NULL,
            PRIMARY KEY (cursor_key));

        CREATE TABLE tf_routing_sticky_owners (
            {Scope}, contact_id text NOT NULL, agent_id text NOT NULL, assigned_at timestamptz NOT NULL,
            contact_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, contact_id)) STORED NOT NULL,
            PRIMARY KEY (contact_key));

        CREATE TABLE tf_routing_queue (
            {Scope}, conversation_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            inbox_id text COLLATE "C" NOT NULL, team_id text COLLATE "C" NULL, assignee_id text NULL CHECK (assignee_id IS NULL),
            contact_id text NOT NULL, labels text[] NOT NULL, access_attributes jsonb NOT NULL,
            waiting_since timestamptz NOT NULL, conversation_revision bigint NOT NULL CHECK (conversation_revision > 0),
            target_inbox_id text COLLATE "C" NOT NULL, queued_at timestamptz NOT NULL,
            overflow_stage integer NOT NULL CHECK (overflow_stage BETWEEN 0 AND 8), wait_exceeded boolean NOT NULL,
            next_attempt_at timestamptz NOT NULL, attempts integer NOT NULL CHECK (attempts >= 0),
            fence bigint NOT NULL, lease_until timestamptz NULL,
            {ConversationKey},
            {ScopeKey},
            PRIMARY KEY (conversation_key));
        CREATE INDEX tf_routing_queue_waiting ON tf_routing_queue (scope_key, waiting_since, conversation_id);
        CREATE INDEX tf_routing_queue_target ON tf_routing_queue (scope_key, target_inbox_id, waiting_since, conversation_id);
        CREATE INDEX tf_routing_queue_due ON tf_routing_queue (next_attempt_at);
        CREATE INDEX tf_routing_queue_labels ON tf_routing_queue USING gin (labels);

        CREATE TABLE tf_routing_presence (
            {Scope}, conversation_id text NOT NULL, user_id text COLLATE "C" NOT NULL,
            activity smallint NOT NULL CHECK (activity BETWEEN 1 AND 2), since timestamptz NOT NULL, expires_at timestamptz NOT NULL,
            {ConversationKey},
            presence_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, user_id)) STORED NOT NULL,
            PRIMARY KEY (presence_key));
        CREATE INDEX tf_routing_presence_conversation ON tf_routing_presence (conversation_key, user_id);
        CREATE INDEX tf_routing_presence_expiry ON tf_routing_presence (expires_at);

        CREATE TABLE tf_routing_reply_locks (
            {Scope}, conversation_id text NOT NULL, holder_id text NOT NULL, fence bigint NOT NULL,
            acquired_at timestamptz NOT NULL, expires_at timestamptz NOT NULL,
            {ConversationKey},
            PRIMARY KEY (conversation_key));
        CREATE INDEX tf_routing_reply_locks_expiry ON tf_routing_reply_locks (expires_at);
        """),
        new(2, "queue-due-claim-order", """
        CREATE INDEX tf_routing_queue_due_v2 ON tf_routing_queue (next_attempt_at, conversation_key);
        DROP INDEX tf_routing_queue_due;
        """),
        new(3, "perf-indexes", """
        CREATE INDEX tf_routing_agents_online ON tf_routing_agents (scope_key, agent_id) WHERE status = 1;
        CREATE INDEX tf_routing_agents_reassign_v2 ON tf_routing_agents (agent_key) INCLUDE (reassign_lease_until) WHERE reassign_pending;
        CREATE INDEX tf_routing_assignments_agent_load ON tf_routing_assignments (scope_key, agent_id, channel_kind) INCLUDE (conversation_id, state, reserved_until);
        CREATE INDEX tf_routing_queue_team ON tf_routing_queue (scope_key, team_id, waiting_since, conversation_id) WHERE team_id IS NOT NULL;
        """)]);
}
