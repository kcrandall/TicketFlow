using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Surveys.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the survey tables. Keys use fixed-size <c>tf_key_v1</c> SHA-256 keys over the
/// raw identifiers; token hashes are unique; responses cascade with their invitation. Never edit an applied step; append a new one.
/// </summary>
public static class PostgresSurveyMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeKey = $"scope_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns})) STORED NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.surveys", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_survey_definitions (
            {Scope}, survey_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            name text NOT NULL, kind smallint NOT NULL CHECK (kind IN (1, 2)), default_locale text NOT NULL, questions jsonb NOT NULL,
            trigger smallint NOT NULL CHECK (trigger = 1), delay_seconds bigint NOT NULL, cooldown_seconds bigint NOT NULL, lifetime_seconds bigint NOT NULL,
            inbox_id text NULL, enabled boolean NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            survey_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, survey_id)) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (survey_key));
        CREATE INDEX tf_survey_definitions_scope ON tf_survey_definitions (scope_key);

        CREATE TABLE tf_survey_invitations (
            {Scope}, invitation_id uuid NOT NULL, survey_id text NOT NULL, kind smallint NOT NULL CHECK (kind IN (1, 2)),
            conversation_id text NOT NULL, contact_id text NOT NULL, inbox_id text NULL, team_id text NULL, agent_id text NULL, locale text NOT NULL,
            created_at timestamptz NOT NULL, send_after timestamptz NOT NULL,
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 5), attempts integer NOT NULL CHECK (attempts BETWEEN 0 AND 20),
            token_hash bytea NULL CHECK (token_hash IS NULL OR length(token_hash) = 32), sent_at timestamptz NULL, expires_at timestamptz NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            revision bigint NOT NULL CHECK (revision > 0),
            invitation_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, invitation_id::text)) STORED NOT NULL,
            conversation_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, survey_id, conversation_id)) STORED NOT NULL,
            contact_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, survey_id, contact_id)) STORED NOT NULL,
            survey_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, survey_id)) STORED NOT NULL,
            {ScopeKey},
            CHECK ((state IN (2, 3)) = (token_hash IS NOT NULL AND sent_at IS NOT NULL AND expires_at IS NOT NULL)),
            PRIMARY KEY (invitation_key));
        CREATE UNIQUE INDEX tf_survey_invitations_conversation ON tf_survey_invitations (conversation_key);
        CREATE UNIQUE INDEX tf_survey_invitations_token ON tf_survey_invitations (token_hash);
        CREATE INDEX tf_survey_invitations_contact ON tf_survey_invitations (contact_key, created_at);
        CREATE INDEX tf_survey_invitations_survey ON tf_survey_invitations (survey_key);
        CREATE INDEX tf_survey_invitations_due ON tf_survey_invitations (scope_key, send_after, invitation_id) WHERE state = 1;

        CREATE TABLE tf_survey_responses (
            {Scope}, invitation_id uuid NOT NULL, survey_id text NOT NULL, kind smallint NOT NULL CHECK (kind IN (1, 2)),
            score smallint NOT NULL CHECK (score BETWEEN 0 AND 10), comment text NULL, responded_at timestamptz NOT NULL,
            conversation_id text NOT NULL, contact_id text NOT NULL, inbox_id text NULL, team_id text NULL, agent_id text NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            invitation_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, invitation_id::text)) STORED NOT NULL
                REFERENCES tf_survey_invitations (invitation_key) ON DELETE CASCADE,
            {ScopeKey},
            PRIMARY KEY (invitation_key));
        CREATE INDEX tf_survey_responses_time ON tf_survey_responses (scope_key, responded_at DESC, invitation_id DESC);
        """),
        // Version 2: dispatch claims with leases (state 6 = Sending), tokens kept across retries (issued_at replaces sent_at).
        new(2, "dispatch-leases", """
        ALTER TABLE tf_survey_invitations DROP CONSTRAINT tf_survey_invitations_state_check;
        ALTER TABLE tf_survey_invitations DROP CONSTRAINT tf_survey_invitations_check;
        ALTER TABLE tf_survey_invitations RENAME COLUMN sent_at TO issued_at;
        ALTER TABLE tf_survey_invitations ADD COLUMN lease_until timestamptz NULL;
        ALTER TABLE tf_survey_invitations ADD CONSTRAINT tf_survey_invitations_state_v2 CHECK (state BETWEEN 1 AND 6);
        ALTER TABLE tf_survey_invitations ADD CONSTRAINT tf_survey_invitations_token_v2 CHECK (
            (token_hash IS NULL) = (issued_at IS NULL) AND (token_hash IS NULL) = (expires_at IS NULL) AND (state NOT IN (2, 3) OR token_hash IS NOT NULL));
        ALTER TABLE tf_survey_invitations ADD CONSTRAINT tf_survey_invitations_lease_v2 CHECK ((state = 6) = (lease_until IS NOT NULL));
        CREATE INDEX tf_survey_invitations_leases ON tf_survey_invitations (scope_key, lease_until) WHERE state = 6;
        """),
        new(3, "perf-indexes", """
        CREATE INDEX tf_survey_responses_aggregate ON tf_survey_responses (scope_key, kind, responded_at DESC) INCLUDE (survey_id, score, inbox_id, team_id, agent_id);
        """)]);
}
