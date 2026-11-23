using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Conversations.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the conversation tables. Keys use fixed-size <c>tf_key_v1</c> SHA-256 keys over
/// the raw identifiers (each up to 256 UTF-16 units); stores also compare the raw columns. List indexes lead with the scope key and
/// end with <c>(last_activity_at DESC, conversation_id DESC)</c> (identifiers use the deterministic <c>"C"</c> collation) so keyset
/// pages are index-backed per criterion. Version 2 adds the <c>tf_conversation_labels</c> side table (one row per conversation label,
/// maintained in every write transaction) for index-backed label lists, compound <c>(criterion, status, activity)</c> list indexes and an
/// outbox availability index. Never edit an applied step; append one.
/// </summary>
public static class PostgresConversationMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeKey = "scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL";
    private const string ConversationKey = "conversation_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id)) STORED NOT NULL";
    private const string Activity = "last_activity_at DESC, conversation_id DESC";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.conversations", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_conversations (
            {Scope}, conversation_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            inbox_id text COLLATE "C" NOT NULL, contact_id text COLLATE "C" NOT NULL,
            status smallint NOT NULL CHECK (status BETWEEN 1 AND 5), snoozed_until timestamptz NULL,
            priority smallint NOT NULL CHECK (priority BETWEEN 1 AND 5),
            assignee_id text COLLATE "C" NULL, team_id text COLLATE "C" NULL,
            labels text[] NOT NULL, attributes jsonb NOT NULL, access_attributes jsonb NOT NULL, participants text[] NOT NULL,
            channel_kind text NULL, channel_thread_id text NULL,
            created_at timestamptz NOT NULL, last_activity_at timestamptz NOT NULL,
            waiting_since timestamptz NULL, first_reply_at timestamptz NULL, last_incoming_at timestamptz NULL, last_outgoing_at timestamptz NULL,
            last_sequence bigint NOT NULL CHECK (last_sequence >= 0), contact_redirects jsonb NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            {ConversationKey},
            {ScopeKey},
            thread_key bytea GENERATED ALWAYS AS (CASE WHEN channel_thread_id IS NULL THEN NULL
                ELSE tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id, channel_kind, channel_thread_id) END) STORED,
            PRIMARY KEY (conversation_key),
            CHECK ((status = 3) = (snoozed_until IS NOT NULL)),
            CHECK ((channel_kind IS NULL) = (channel_thread_id IS NULL)));
        CREATE INDEX tf_conversations_activity ON tf_conversations (scope_key, {Activity});
        CREATE INDEX tf_conversations_inbox ON tf_conversations (scope_key, inbox_id, {Activity});
        CREATE INDEX tf_conversations_status ON tf_conversations (scope_key, status, {Activity});
        CREATE INDEX tf_conversations_assignee ON tf_conversations (scope_key, assignee_id, {Activity});
        CREATE INDEX tf_conversations_team ON tf_conversations (scope_key, team_id, {Activity});
        CREATE INDEX tf_conversations_contact ON tf_conversations (scope_key, contact_id, {Activity});
        CREATE INDEX tf_conversations_labels ON tf_conversations USING gin (labels);
        CREATE INDEX tf_conversations_thread ON tf_conversations (thread_key, created_at DESC, conversation_id DESC) WHERE thread_key IS NOT NULL;

        CREATE TABLE tf_conversation_events (
            {Scope}, conversation_id text NOT NULL, sequence bigint NOT NULL CHECK (sequence > 0),
            event_id uuid NOT NULL, format_version smallint NOT NULL CHECK (format_version = 1),
            kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 12), message_id text NULL, occurred_at timestamptz NOT NULL,
            actor_kind smallint NOT NULL CHECK (actor_kind BETWEEN 1 AND 4), actor_id text NOT NULL,
            payload text NOT NULL,
            {ConversationKey},
            message_key bytea GENERATED ALWAYS AS (CASE WHEN message_id IS NULL THEN NULL
                ELSE tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, message_id) END) STORED,
            PRIMARY KEY (conversation_key, sequence),
            FOREIGN KEY (conversation_key) REFERENCES tf_conversations (conversation_key) ON DELETE CASCADE,
            CHECK ((kind IN (1, 2)) = (message_id IS NOT NULL)));

        CREATE TABLE tf_conversation_messages (
            {Scope}, conversation_id text NOT NULL, message_id text NOT NULL, inbox_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            sequence bigint NOT NULL CHECK (sequence > 0), direction smallint NOT NULL CHECK (direction BETWEEN 1 AND 2),
            external_id text NULL, delivery_state smallint NOT NULL CHECK (delivery_state BETWEEN 1 AND 6), delivery_failure text NULL,
            delivery_updated_at timestamptz NOT NULL, delivery_revision bigint NOT NULL CHECK (delivery_revision > 0),
            message_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, conversation_id, message_id)) STORED NOT NULL,
            {ConversationKey},
            external_key bytea GENERATED ALWAYS AS (CASE WHEN external_id IS NULL THEN NULL
                ELSE tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id, external_id) END) STORED,
            PRIMARY KEY (message_key),
            FOREIGN KEY (conversation_key) REFERENCES tf_conversations (conversation_key) ON DELETE CASCADE);
        CREATE UNIQUE INDEX tf_conversation_messages_external ON tf_conversation_messages (external_key) WHERE external_key IS NOT NULL;
        CREATE INDEX tf_conversation_messages_conversation ON tf_conversation_messages (conversation_key);

        CREATE TABLE tf_conversation_attribute_definitions (
            {Scope}, attribute_key text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            attribute_type smallint NOT NULL CHECK (attribute_type BETWEEN 1 AND 5), sensitive boolean NOT NULL, display_name text NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            definition_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, attribute_key)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (definition_key));
        CREATE INDEX tf_conversation_attribute_definitions_scope ON tf_conversation_attribute_definitions (scope_key);

        CREATE TABLE tf_conversation_outbox (
            sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, entry_id uuid NOT NULL UNIQUE,
            {Scope}, conversation_id text NOT NULL, inbox_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 4), revision bigint NOT NULL CHECK (revision > 0),
            payload text NOT NULL, occurred_at timestamptz NOT NULL,
            {ConversationKey});
        CREATE INDEX tf_conversation_outbox_occurred ON tf_conversation_outbox (occurred_at, sequence);

        CREATE TABLE tf_conversation_outbox_consumers (
            consumer text COLLATE "C" PRIMARY KEY, registered_at timestamptz NOT NULL);

        CREATE TABLE tf_conversation_outbox_deliveries (
            consumer text COLLATE "C" NOT NULL REFERENCES tf_conversation_outbox_consumers (consumer) ON DELETE CASCADE,
            sequence bigint NOT NULL REFERENCES tf_conversation_outbox (sequence) ON DELETE CASCADE,
            conversation_key bytea NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 0 AND 2),
            fence bigint NOT NULL CHECK (fence >= 0), attempts integer NOT NULL CHECK (attempts >= 0),
            available_at timestamptz NOT NULL, lease_until timestamptz NULL, completed_at timestamptz NULL,
            PRIMARY KEY (consumer, sequence));
        CREATE INDEX tf_conversation_outbox_deliveries_pending ON tf_conversation_outbox_deliveries (consumer, sequence) WHERE state = 0;
        CREATE INDEX tf_conversation_outbox_deliveries_order ON tf_conversation_outbox_deliveries (consumer, conversation_key, sequence) WHERE state = 0;
        CREATE INDEX tf_conversation_outbox_deliveries_sequence ON tf_conversation_outbox_deliveries (sequence);
        """),
        new(2, "label-index-compound-lists-outbox-availability", $"""
        CREATE TABLE tf_conversation_labels (
            scope_key bytea NOT NULL,
            conversation_key bytea NOT NULL REFERENCES tf_conversations (conversation_key) ON DELETE CASCADE,
            label text COLLATE "C" NOT NULL, conversation_id text COLLATE "C" NOT NULL, last_activity_at timestamptz NOT NULL,
            PRIMARY KEY (conversation_key, label));
        CREATE INDEX tf_conversation_labels_activity ON tf_conversation_labels (scope_key, label, {Activity});
        INSERT INTO tf_conversation_labels (scope_key, conversation_key, label, conversation_id, last_activity_at)
            SELECT c.scope_key, c.conversation_key, l.label, c.conversation_id, c.last_activity_at
            FROM tf_conversations c CROSS JOIN LATERAL unnest(c.labels) AS l(label);
        CREATE INDEX tf_conversations_inbox_status ON tf_conversations (scope_key, inbox_id, status, {Activity});
        CREATE INDEX tf_conversations_assignee_status ON tf_conversations (scope_key, assignee_id, status, {Activity});
        CREATE INDEX tf_conversations_team_status ON tf_conversations (scope_key, team_id, status, {Activity});
        CREATE INDEX tf_conversation_outbox_deliveries_available ON tf_conversation_outbox_deliveries (consumer, available_at, sequence) WHERE state = 0;
        """),
        new(3, "perf-partial-indexes", """
        CREATE INDEX tf_conversations_thread_open ON tf_conversations (thread_key, created_at DESC, conversation_id DESC) WHERE thread_key IS NOT NULL AND status <> 5;
        CREATE INDEX tf_conversation_outbox_deliveries_seq_pending ON tf_conversation_outbox_deliveries (sequence) WHERE state = 0;
        CREATE INDEX tf_conversation_attribute_definitions_scope_key ON tf_conversation_attribute_definitions (scope_key, attribute_key);
        """)]);
}
