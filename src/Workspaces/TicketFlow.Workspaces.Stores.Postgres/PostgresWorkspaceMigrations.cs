using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Workspaces.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the workspace tables. Every key and index uses fixed-size <c>tf_key_v1</c>
/// SHA-256 keys generated from the raw identifiers (each up to 256 UTF-16 units); stores also compare the raw columns for exact
/// identity. Never edit an applied step; append a new one.
/// </summary>
public static class PostgresWorkspaceMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeKey = "scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.workspaces", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_workspace_settings (
            {Scope},
            format_version smallint NOT NULL CHECK (format_version = 1),
            name text NOT NULL, default_locale text NOT NULL, time_zone text NOT NULL,
            enabled_features text[] NOT NULL, disabled_features text[] NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            {ScopeKey},
            PRIMARY KEY (scope_key));

        CREATE TABLE tf_workspace_inboxes (
            {Scope}, inbox_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            name text NOT NULL, channel_kind text NOT NULL, channel_config_id text NOT NULL,
            calendar_id text NULL, sla_policy_id text NULL, routing_policy_id text NULL, enabled boolean NOT NULL,
            greeting_message text NULL, away_message text NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            inbox_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, inbox_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (inbox_key));
        CREATE INDEX tf_workspace_inboxes_scope ON tf_workspace_inboxes (scope_key);

        CREATE TABLE tf_workspace_teams (
            {Scope}, team_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            name text NOT NULL, description text NULL,
            member_count integer NOT NULL CHECK (member_count BETWEEN 0 AND 1000),
            revision bigint NOT NULL CHECK (revision > 0),
            team_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, team_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (team_key));
        CREATE INDEX tf_workspace_teams_scope ON tf_workspace_teams (scope_key);

        CREATE TABLE tf_workspace_team_members (
            {Scope}, team_id text NOT NULL, user_id text NOT NULL,
            member_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, team_id, user_id)) STORED NOT NULL,
            team_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, team_id)) STORED NOT NULL
                REFERENCES tf_workspace_teams (team_key) ON DELETE CASCADE,
            user_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, user_id)) STORED NOT NULL,
            PRIMARY KEY (member_key));
        CREATE INDEX tf_workspace_team_members_team ON tf_workspace_team_members (team_key);
        CREATE INDEX tf_workspace_team_members_user ON tf_workspace_team_members (user_key);

        CREATE TABLE tf_workspace_calendars (
            {Scope}, calendar_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            name text NOT NULL, time_zone text NOT NULL, schedule text NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            calendar_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, calendar_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (calendar_key));
        CREATE INDEX tf_workspace_calendars_scope ON tf_workspace_calendars (scope_key);
        """)]);
}
