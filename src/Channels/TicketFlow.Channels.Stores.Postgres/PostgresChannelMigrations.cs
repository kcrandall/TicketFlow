using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Channels.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the channel state tables. Keys are fixed-size <c>tf_key_v1</c> SHA-256 keys over
/// the raw identifiers; stores also compare the raw columns. Endpoint addresses are unique per partition (tenant, project,
/// environment, app) and kind through a partial unique index. Never edit an applied step; append one.
/// </summary>
public static class PostgresChannelMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeParts = "tenant_id, project_id, environment_id, app_id, workspace_id";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.channels", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_channel_endpoints (
            {Scope}, channel_kind text COLLATE "C" NOT NULL, configuration_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            inbox_id text COLLATE "C" NOT NULL, address text COLLATE "C" NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            endpoint_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, channel_kind, configuration_id)) STORED NOT NULL,
            scope_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts})) STORED NOT NULL,
            address_key bytea GENERATED ALWAYS AS (CASE WHEN address IS NULL THEN NULL
                ELSE tf_key_v1(tenant_id, project_id, environment_id, app_id, channel_kind, address) END) STORED,
            PRIMARY KEY (endpoint_key));
        CREATE UNIQUE INDEX tf_channel_endpoints_address ON tf_channel_endpoints (address_key) WHERE address_key IS NOT NULL;
        CREATE INDEX tf_channel_endpoints_scope ON tf_channel_endpoints (scope_key);

        CREATE TABLE tf_channel_conversation_states (
            {Scope}, conversation_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            inbox_id text COLLATE "C" NOT NULL, channel_kind text COLLATE "C" NOT NULL,
            recipient_kind smallint NOT NULL CHECK (recipient_kind BETWEEN 1 AND 6), recipient_namespace text NULL, recipient_value text NOT NULL,
            external_thread_id text NULL, subject text NULL, last_incoming_at timestamptz NULL, last_incoming_external_id text NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            state_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, conversation_id)) STORED NOT NULL,
            PRIMARY KEY (state_key));

        CREATE TABLE tf_channel_references (
            {Scope}, inbox_id text COLLATE "C" NOT NULL, conversation_id text COLLATE "C" NOT NULL, reference text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            direction smallint NOT NULL CHECK (direction IN (1, 2)), message_id text NULL,
            sequence bigint GENERATED ALWAYS AS IDENTITY, recorded_at timestamptz NOT NULL,
            reference_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, inbox_id, reference)) STORED NOT NULL,
            conversation_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, conversation_id)) STORED NOT NULL,
            PRIMARY KEY (reference_key));
        CREATE INDEX tf_channel_references_conversation ON tf_channel_references (conversation_key, sequence);

        CREATE TABLE tf_channel_dispatches (
            {Scope}, conversation_id text COLLATE "C" NOT NULL, message_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            inbox_id text COLLATE "C" NOT NULL, channel_kind text COLLATE "C" NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 6), attempts integer NOT NULL CHECK (attempts BETWEEN 0 AND 1000),
            updated_at timestamptz NOT NULL, next_attempt_at timestamptz NULL, external_message_id text NULL, code text NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            dispatch_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, conversation_id, message_id)) STORED NOT NULL,
            PRIMARY KEY (dispatch_key),
            CHECK ((state = 2) = (next_attempt_at IS NOT NULL)));
        """),
        new(2, "conversation-state-contact-thread", $"""
        ALTER TABLE tf_channel_conversation_states ADD COLUMN contact_id text COLLATE "C" NULL;
        ALTER TABLE tf_channel_conversation_states ADD COLUMN thread_key bytea GENERATED ALWAYS AS (CASE WHEN external_thread_id IS NULL OR contact_id IS NULL THEN NULL
            ELSE tf_key_v1({ScopeParts}, inbox_id, channel_kind, external_thread_id, contact_id) END) STORED;
        CREATE INDEX tf_channel_conversation_states_thread ON tf_channel_conversation_states (thread_key, conversation_id DESC) WHERE thread_key IS NOT NULL;
        CREATE INDEX tf_channel_endpoints_address_prefix ON tf_channel_endpoints (tenant_id, project_id, environment_id, app_id, channel_kind, address)
            WHERE address IS NOT NULL;
        """)]);
}
