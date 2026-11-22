using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Events.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the event stream tables. Keys are fixed-size <c>tf_key_v1</c> SHA-256 keys over the raw
/// identifiers. Never edit an applied step; append a new one. Version 2 adds the replay generation and suspension columns, restricts the due
/// index to claimable rows, gives head rows free space for HOT updates (fillfactor 50) and replaces the recorded-at B-tree with a BRIN index
/// (append-ordered log).
/// </summary>
public static class PostgresEventMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeKey = $"scope_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns})) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.events", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_event_heads (
            {Scope}, last_sequence bigint NOT NULL CHECK (last_sequence >= 0),
            {ScopeKey},
            PRIMARY KEY (scope_key));

        CREATE TABLE tf_events (
            {Scope}, sequence bigint NOT NULL CHECK (sequence > 0), event_id uuid NOT NULL, type text NOT NULL CHECK (length(type) <= 128),
            occurred_at timestamptz NOT NULL, recorded_at timestamptz NOT NULL, actor_kind text NULL, actor_id text NULL,
            conversation_id text NULL, inbox_id text NULL, team_id text NULL, assignee_id text NULL, contact_id text NULL,
            data text NOT NULL CHECK (length(data) <= 65536),
            format_version smallint NOT NULL CHECK (format_version = 1),
            {ScopeKey},
            PRIMARY KEY (scope_key, sequence));
        CREATE UNIQUE INDEX tf_events_id ON tf_events (scope_key, event_id);
        CREATE INDEX tf_events_retention ON tf_events (recorded_at);

        CREATE TABLE tf_event_checkpoints (
            {Scope}, name text NOT NULL CHECK (length(name) BETWEEN 1 AND 64), value bigint NOT NULL,
            {ScopeKey},
            PRIMARY KEY (scope_key, name));

        CREATE TABLE tf_event_subscriptions (
            {Scope}, subscription_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            name text NOT NULL, sink_kind text NOT NULL, sink_target text NOT NULL, filter text NOT NULL, principal_id text NOT NULL,
            privacy smallint NOT NULL CHECK (privacy BETWEEN 1 AND 3), enabled boolean NOT NULL, batch_size integer NOT NULL CHECK (batch_size BETWEEN 1 AND 100),
            revision bigint NOT NULL CHECK (revision > 0), created_at timestamptz NOT NULL,
            delivery_cursor bigint NOT NULL CHECK (delivery_cursor >= 0), attempts integer NOT NULL CHECK (attempts >= 0), available_at timestamptz NOT NULL,
            last_error text NULL, last_delivered_at timestamptz NULL, fence bigint NOT NULL CHECK (fence >= 0), lease_until timestamptz NULL,
            subscription_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, subscription_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (subscription_key));
        CREATE INDEX tf_event_subscriptions_scope ON tf_event_subscriptions (scope_key);
        CREATE INDEX tf_event_subscriptions_due ON tf_event_subscriptions (available_at) WHERE enabled;

        CREATE TABLE tf_event_dead_letters (
            {Scope}, subscription_id text NOT NULL, sequence bigint NOT NULL CHECK (sequence > 0), event_id uuid NOT NULL, type text NOT NULL,
            attempts integer NOT NULL CHECK (attempts >= 0), error text NOT NULL, dead_lettered_at timestamptz NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            subscription_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, subscription_id)) STORED NOT NULL,
            PRIMARY KEY (subscription_key, sequence));
        CREATE INDEX tf_event_dead_letters_retention ON tf_event_dead_letters (dead_lettered_at);
        """), new(2, "delivery-generation-suspension-and-append-tuning", """
        ALTER TABLE tf_event_subscriptions ADD COLUMN generation bigint NOT NULL DEFAULT 0 CHECK (generation >= 0);
        ALTER TABLE tf_event_subscriptions ADD COLUMN suspended boolean NOT NULL DEFAULT false;
        DROP INDEX tf_event_subscriptions_due;
        CREATE INDEX tf_event_subscriptions_due ON tf_event_subscriptions (available_at) WHERE enabled AND NOT suspended;
        ALTER TABLE tf_event_heads SET (fillfactor = 50);
        DROP INDEX tf_events_retention;
        CREATE INDEX tf_events_recorded_brin ON tf_events USING brin (recorded_at);
        """)]);
}
