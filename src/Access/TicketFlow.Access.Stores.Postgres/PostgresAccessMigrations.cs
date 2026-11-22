using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Access.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the access tables. Every key and index uses fixed-size
/// <c>tf_key_v1</c> SHA-256 keys generated from the raw identifiers (each up to 256 UTF-16 units), so B-tree tuples stay bounded;
/// stores also compare the raw columns for exact identity. Never edit an applied step; append a new one.
/// </summary>
public static class PostgresAccessMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.access", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_access_roles (
            {Scope}, workspace_id text NOT NULL, role_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1), name text NOT NULL, permissions text[] NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            role_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, role_id)) STORED NOT NULL,
            scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL,
            PRIMARY KEY (role_key));
        CREATE INDEX tf_access_roles_scope ON tf_access_roles (scope_key);

        CREATE TABLE tf_access_grants (
            {Scope}, grant_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            subject_kind smallint NOT NULL CHECK (subject_kind BETWEEN 1 AND 3), subject_id text NOT NULL,
            role_id text NOT NULL,
            scope_level smallint NOT NULL CHECK (scope_level BETWEEN 1 AND 4),
            workspace_id text NULL, team_id text NULL, inbox_id text NULL,
            effect smallint NOT NULL CHECK (effect IN (1, 2)), grant_kind smallint NOT NULL CHECK (grant_kind IN (1, 2)),
            condition jsonb NULL, valid_from timestamptz NULL, valid_until timestamptz NULL, justification text NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            grant_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, grant_id)) STORED NOT NULL,
            subject_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, subject_kind::text, subject_id)) STORED NOT NULL,
            PRIMARY KEY (grant_key),
            CHECK ((scope_level = 1) = (workspace_id IS NULL)),
            CHECK ((scope_level = 3) = (team_id IS NOT NULL)),
            CHECK ((scope_level = 4) = (inbox_id IS NOT NULL)),
            CHECK (valid_until IS NULL OR valid_from IS NULL OR valid_until > valid_from),
            CHECK (grant_kind = 1 OR (effect = 1 AND valid_until IS NOT NULL AND justification IS NOT NULL)));
        CREATE INDEX tf_access_grants_subject ON tf_access_grants (subject_key);

        CREATE TABLE tf_access_field_policies (
            {Scope}, workspace_id text NOT NULL, field text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1), applies_to text[] NOT NULL, unmasked_roles text[] NOT NULL,
            reveal_permission text NULL, revision bigint NOT NULL CHECK (revision > 0),
            policy_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, field)) STORED NOT NULL,
            scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL,
            PRIMARY KEY (policy_key));
        CREATE INDEX tf_access_field_policies_scope ON tf_access_field_policies (scope_key);

        CREATE TABLE tf_access_audit (
            event_id uuid PRIMARY KEY, {Scope}, workspace_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1), occurred_at timestamptz NOT NULL,
            kind smallint NOT NULL, outcome smallint NOT NULL, actor_kind smallint NOT NULL, actor_id text NOT NULL,
            permission text NULL, resource_id text NULL, team_id text NULL, inbox_id text NULL, grant_id text NULL,
            role_id text NULL, field text NULL, used_break_glass boolean NOT NULL, denial_reason smallint NULL, justification text NULL,
            scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL);
        CREATE INDEX tf_access_audit_scope ON tf_access_audit (scope_key, occurred_at, event_id);
        """)]);
}
