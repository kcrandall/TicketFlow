using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Channels.Mailbox.Stores.Postgres;

/// <summary>Feature-owned, append-only PostgreSQL migrations for IMAP mailbox checkpoints and Message-Id deduplication.</summary>
public static class PostgresMailboxMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeParts = "tenant_id, project_id, environment_id, app_id, workspace_id";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.channels.mailbox", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_mailbox_checkpoints (
            {Scope}, configuration_id text COLLATE "C" NOT NULL, folder text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            uid_validity bigint NOT NULL CHECK (uid_validity >= 0 AND uid_validity <= 4294967295),
            last_uid bigint NOT NULL CHECK (last_uid >= 0 AND last_uid <= 4294967295),
            updated_at timestamptz NOT NULL,
            mailbox_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, configuration_id, folder)) STORED NOT NULL,
            PRIMARY KEY (mailbox_key));

        CREATE TABLE tf_mailbox_seen_messages (
            {Scope}, configuration_id text COLLATE "C" NOT NULL, folder text COLLATE "C" NOT NULL,
            message_id text COLLATE "C" NOT NULL,
            seen_at timestamptz NOT NULL,
            seen_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeParts}, configuration_id, folder, message_id)) STORED NOT NULL,
            PRIMARY KEY (seen_key));
        CREATE INDEX tf_mailbox_seen_messages_seen_at ON tf_mailbox_seen_messages (seen_at);
        """)]);
}
