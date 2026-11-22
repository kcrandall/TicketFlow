using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Contacts.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the contact tables. Keys and indexes use fixed-size <c>tf_key_v1</c> SHA-256
/// keys over the raw identifiers (each up to 256 UTF-16 units); stores also compare the raw columns. Sort and search columns use the
/// deterministic <c>"C"</c> collation so keyset paging and prefix ranges are index-backed. Never edit an applied step; append one.
/// </summary>
public static class PostgresContactMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeKey = "scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.contacts", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_contacts (
            {Scope}, contact_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            display_name text NULL, name_folded text COLLATE "C" NOT NULL,
            avatar_ref text NULL, locale text NULL, time_zone text NULL, company text NULL,
            attributes jsonb NOT NULL, labels text[] NOT NULL, blocked boolean NOT NULL,
            merged_into text COLLATE "C" NULL,
            created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            contact_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, contact_id)) STORED NOT NULL,
            {ScopeKey},
            merged_key bytea GENERATED ALWAYS AS (CASE WHEN merged_into IS NULL THEN NULL
                ELSE tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, merged_into) END) STORED,
            PRIMARY KEY (contact_key),
            CHECK (merged_into IS NULL OR merged_into <> contact_id));
        CREATE INDEX tf_contacts_name ON tf_contacts (scope_key, name_folded, contact_id) WHERE merged_into IS NULL;
        CREATE INDEX tf_contacts_merged ON tf_contacts (merged_key) WHERE merged_key IS NOT NULL;

        CREATE TABLE tf_contact_identities (
            {Scope}, contact_id text COLLATE "C" NOT NULL,
            kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 6), namespace text COLLATE "C" NOT NULL, value text COLLATE "C" NOT NULL,
            verified boolean NOT NULL, source text NOT NULL, verified_at timestamptz NOT NULL,
            contact_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, contact_id)) STORED NOT NULL,
            identity_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, kind::text, namespace, value)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (contact_key, identity_key),
            FOREIGN KEY (contact_key) REFERENCES tf_contacts (contact_key) ON DELETE CASCADE);
        CREATE UNIQUE INDEX tf_contact_identities_verified ON tf_contact_identities (identity_key) WHERE verified;
        CREATE INDEX tf_contact_identities_lookup ON tf_contact_identities (identity_key);
        CREATE INDEX tf_contact_identities_search ON tf_contact_identities (scope_key, kind, value, contact_id);

        CREATE TABLE tf_contact_attribute_definitions (
            {Scope}, attribute_key text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            attribute_type smallint NOT NULL CHECK (attribute_type BETWEEN 1 AND 5), sensitive boolean NOT NULL, display_name text NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            definition_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, attribute_key)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (definition_key));
        CREATE INDEX tf_contact_attribute_definitions_scope ON tf_contact_attribute_definitions (scope_key);

        CREATE TABLE tf_contact_merges (
            {Scope}, merge_id uuid NOT NULL, target_id text NOT NULL, source_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            merged_at timestamptz NOT NULL, actor_kind smallint NOT NULL CHECK (actor_kind BETWEEN 1 AND 4), actor_id text NOT NULL,
            contributions jsonb NOT NULL,
            merge_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, merge_id::text)) STORED NOT NULL,
            target_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, target_id)) STORED NOT NULL,
            source_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, source_id)) STORED NOT NULL,
            PRIMARY KEY (merge_key));
        CREATE INDEX tf_contact_merges_target ON tf_contact_merges (target_key);
        CREATE INDEX tf_contact_merges_source ON tf_contact_merges (source_key);

        CREATE TABLE tf_contact_change_heads (
            {Scope}, last_sequence bigint NOT NULL CHECK (last_sequence >= 0),
            {ScopeKey},
            PRIMARY KEY (scope_key));

        CREATE TABLE tf_contact_changes (
            {Scope}, sequence bigint NOT NULL CHECK (sequence > 0), event_id uuid NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 5), contact_id text NOT NULL, related_id text NULL, merge_id uuid NULL,
            occurred_at timestamptz NOT NULL,
            {ScopeKey},
            PRIMARY KEY (scope_key, sequence));

        CREATE TABLE tf_contact_audit (
            event_id uuid PRIMARY KEY, {Scope},
            format_version smallint NOT NULL CHECK (format_version = 1), occurred_at timestamptz NOT NULL,
            kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 4), actor_kind smallint NOT NULL CHECK (actor_kind BETWEEN 1 AND 4), actor_id text NOT NULL,
            contact_id text NULL, related_id text NULL, merge_id uuid NULL, count integer NULL, justification text NULL,
            {ScopeKey});
        CREATE INDEX tf_contact_audit_scope ON tf_contact_audit (scope_key, occurred_at, event_id);
        """)]);
}
