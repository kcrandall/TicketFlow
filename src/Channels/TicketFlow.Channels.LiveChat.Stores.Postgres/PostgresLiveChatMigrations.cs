using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Channels.LiveChat.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the live-chat tables. Keys are fixed-size <c>tf_key_v1</c> SHA-256 keys over the
/// raw identifiers; stores also compare the raw columns. Session keys cover the partition (tenant, project, environment, app) so
/// session ids are unique per partition; links cascade with their session. Never edit an applied step; append one.
/// </summary>
public static class PostgresLiveChatMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeParts = "tenant_id, project_id, environment_id, app_id, workspace_id";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.livechat", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_livechat_widgets (
            {Scope}, configuration_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            inbox_id text COLLATE "C" NOT NULL, config text NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            widget_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, configuration_id)) STORED NOT NULL,
            scope_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts})) STORED NOT NULL,
            PRIMARY KEY (widget_key));
        CREATE INDEX tf_livechat_widgets_scope ON tf_livechat_widgets (scope_key);

        CREATE TABLE tf_livechat_sessions (
            {Scope}, session_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            configuration_id text COLLATE "C" NOT NULL, inbox_id text COLLATE "C" NOT NULL, visitor_id text COLLATE "C" NOT NULL,
            token_hash bytea NOT NULL CHECK (octet_length(token_hash) = 32),
            previous_token_hash bytea NULL CHECK (previous_token_hash IS NULL OR octet_length(previous_token_hash) = 32),
            previous_valid_until timestamptz NULL,
            created_at timestamptz NOT NULL, token_issued_at timestamptz NOT NULL, expires_at timestamptz NOT NULL,
            identity_namespace text NULL, identity_value text NULL, identity_method smallint NULL CHECK (identity_method IS NULL OR identity_method IN (1, 2)),
            identity_verified_at timestamptz NULL,
            display_name text NULL, prechat text NOT NULL, revoked boolean NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            session_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, session_id)) STORED NOT NULL,
            PRIMARY KEY (session_key),
            CHECK ((previous_token_hash IS NULL) = (previous_valid_until IS NULL)),
            CHECK ((identity_value IS NULL) = (identity_namespace IS NULL) AND (identity_value IS NULL) = (identity_method IS NULL)
                AND (identity_value IS NULL) = (identity_verified_at IS NULL)));
        CREATE INDEX tf_livechat_sessions_expiry ON tf_livechat_sessions (expires_at);

        CREATE TABLE tf_livechat_links (
            {Scope}, session_id text COLLATE "C" NOT NULL, conversation_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            linked_at timestamptz NOT NULL, read_sequence bigint NOT NULL CHECK (read_sequence >= 0),
            session_ref bytea NOT NULL REFERENCES tf_livechat_sessions (session_key) ON DELETE CASCADE,
            link_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, session_id, conversation_id)) STORED NOT NULL,
            session_scope_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, session_id)) STORED NOT NULL,
            conversation_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, conversation_id)) STORED NOT NULL,
            PRIMARY KEY (link_key));
        CREATE INDEX tf_livechat_links_session ON tf_livechat_links (session_scope_key, linked_at DESC);
        CREATE INDEX tf_livechat_links_conversation ON tf_livechat_links (conversation_key, linked_at DESC);
        CREATE INDEX tf_livechat_links_session_ref ON tf_livechat_links (session_ref);
        """),
        new(2, "perf-link-sort-indexes", """
        CREATE INDEX tf_livechat_links_session_v2 ON tf_livechat_links (session_scope_key, linked_at DESC, conversation_id DESC);
        CREATE INDEX tf_livechat_links_conversation_v2 ON tf_livechat_links (conversation_key, linked_at DESC, session_id);
        """)]);
}
