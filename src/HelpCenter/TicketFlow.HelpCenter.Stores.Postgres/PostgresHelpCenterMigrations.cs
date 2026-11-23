using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.HelpCenter.Stores.Postgres;

/// <summary>
/// Feature-owned, append-only migration history for the help center tables. Keys and indexes use fixed-size <c>tf_key_v1</c>
/// SHA-256 keys generated from the raw identifiers; stores also compare raw columns. The published read model carries a stored,
/// weighted <c>tsvector</c> computed with the row's <c>regconfig</c> (title A, tags and excerpt B, body C) and a GIN index. Never
/// edit an applied step; append a new one.
/// </summary>
public static class PostgresHelpCenterMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";
    private const string ScopeKey = "scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL";
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";

    private static string Key(string column) => $"tf_key_v1({ScopeColumns}, {column})";

    private static string OptionalKey(string column) => $"CASE WHEN {column} IS NULL THEN NULL ELSE tf_key_v1({ScopeColumns}, {column}) END";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.helpcenter", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_helpcenter_portals (
            {Scope}, portal_id text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            slug text NOT NULL, name text NOT NULL, default_locale text NOT NULL, supported_locales text[] NOT NULL,
            visibility smallint NOT NULL CHECK (visibility IN (1, 2)), custom_domain text NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            portal_key bytea GENERATED ALWAYS AS ({Key("portal_id")}) STORED NOT NULL,
            {ScopeKey},
            PRIMARY KEY (portal_key));
        CREATE UNIQUE INDEX tf_helpcenter_portals_slug ON tf_helpcenter_portals (scope_key, slug);

        CREATE TABLE tf_helpcenter_collections (
            {Scope}, collection_id text NOT NULL, portal_id text NOT NULL, parent_id text NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            depth smallint NOT NULL CHECK (depth BETWEEN 1 AND 3), position integer NOT NULL CHECK (position BETWEEN 0 AND 1000000),
            translations jsonb NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            collection_key bytea GENERATED ALWAYS AS ({Key("collection_id")}) STORED NOT NULL,
            portal_key bytea GENERATED ALWAYS AS ({Key("portal_id")}) STORED NOT NULL REFERENCES tf_helpcenter_portals (portal_key),
            parent_key bytea GENERATED ALWAYS AS ({OptionalKey("parent_id")}) STORED NULL REFERENCES tf_helpcenter_collections (collection_key),
            {ScopeKey},
            PRIMARY KEY (collection_key));
        CREATE INDEX tf_helpcenter_collections_portal ON tf_helpcenter_collections (portal_key);
        CREATE INDEX tf_helpcenter_collections_parent ON tf_helpcenter_collections (parent_key);

        CREATE TABLE tf_helpcenter_articles (
            {Scope}, article_id text NOT NULL, portal_id text NOT NULL, collection_id text NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            position integer NOT NULL CHECK (position BETWEEN 0 AND 1000000),
            state smallint NOT NULL CHECK (state IN (1, 2, 3)), published_revision integer NULL CHECK (published_revision BETWEEN 1 AND 1000),
            translations jsonb NOT NULL,
            created_by_kind smallint NOT NULL, created_by_id text NOT NULL, created_at timestamptz NOT NULL,
            updated_by_kind smallint NOT NULL, updated_by_id text NOT NULL, updated_at timestamptz NOT NULL,
            revision bigint NOT NULL CHECK (revision > 0),
            article_key bytea GENERATED ALWAYS AS ({Key("article_id")}) STORED NOT NULL,
            portal_key bytea GENERATED ALWAYS AS ({Key("portal_id")}) STORED NOT NULL REFERENCES tf_helpcenter_portals (portal_key),
            collection_key bytea GENERATED ALWAYS AS ({OptionalKey("collection_id")}) STORED NULL REFERENCES tf_helpcenter_collections (collection_key),
            {ScopeKey},
            CHECK (state <> 2 OR published_revision IS NOT NULL),
            PRIMARY KEY (article_key));
        CREATE INDEX tf_helpcenter_articles_portal ON tf_helpcenter_articles (portal_key, article_id COLLATE "C");
        CREATE INDEX tf_helpcenter_articles_collection ON tf_helpcenter_articles (collection_key);

        CREATE TABLE tf_helpcenter_revisions (
            {Scope}, article_id text NOT NULL, number integer NOT NULL CHECK (number BETWEEN 1 AND 1000),
            format_version smallint NOT NULL CHECK (format_version = 1),
            translations jsonb NOT NULL,
            published_by_kind smallint NOT NULL, published_by_id text NOT NULL, published_at timestamptz NOT NULL,
            article_key bytea GENERATED ALWAYS AS ({Key("article_id")}) STORED NOT NULL REFERENCES tf_helpcenter_articles (article_key) ON DELETE CASCADE,
            PRIMARY KEY (article_key, number));

        CREATE TABLE tf_helpcenter_published (
            {Scope}, article_id text NOT NULL, portal_id text NOT NULL, collection_id text NULL,
            locale text NOT NULL, slug text NOT NULL, position integer NOT NULL, revision_number integer NOT NULL, published_at timestamptz NOT NULL,
            title text NOT NULL, excerpt text NULL, tags text[] NOT NULL, tags_text text NOT NULL, body text NOT NULL, plain_text text NOT NULL,
            ts_config regconfig NOT NULL,
            document tsvector GENERATED ALWAYS AS (
                setweight(to_tsvector(ts_config, title), 'A')
                || setweight(to_tsvector(ts_config, tags_text || ' ' || coalesce(excerpt, '')), 'B')
                || setweight(to_tsvector(ts_config, plain_text), 'C')) STORED,
            article_key bytea GENERATED ALWAYS AS ({Key("article_id")}) STORED NOT NULL REFERENCES tf_helpcenter_articles (article_key) ON DELETE CASCADE,
            portal_key bytea GENERATED ALWAYS AS ({Key("portal_id")}) STORED NOT NULL,
            PRIMARY KEY (article_key, locale));
        CREATE UNIQUE INDEX tf_helpcenter_published_slug ON tf_helpcenter_published (portal_key, locale, slug);
        CREATE INDEX tf_helpcenter_published_order ON tf_helpcenter_published (portal_key, locale, position, article_id COLLATE "C");
        CREATE INDEX tf_helpcenter_published_document ON tf_helpcenter_published USING gin (document);

        CREATE TABLE tf_helpcenter_feedback (
            {Scope}, article_id text NOT NULL,
            views bigint NOT NULL CHECK (views >= 0), helpful bigint NOT NULL CHECK (helpful >= 0), not_helpful bigint NOT NULL CHECK (not_helpful >= 0),
            article_key bytea GENERATED ALWAYS AS ({Key("article_id")}) STORED NOT NULL REFERENCES tf_helpcenter_articles (article_key) ON DELETE CASCADE,
            PRIMARY KEY (article_key));

        CREATE TABLE tf_helpcenter_votes (
            article_key bytea NOT NULL REFERENCES tf_helpcenter_articles (article_key) ON DELETE CASCADE,
            visitor_hash bytea NOT NULL CHECK (length(visitor_hash) = 32), helpful boolean NOT NULL,
            PRIMARY KEY (article_key, visitor_hash));

        CREATE TABLE tf_helpcenter_views (
            article_key bytea NOT NULL REFERENCES tf_helpcenter_articles (article_key) ON DELETE CASCADE,
            visitor_hash bytea NOT NULL CHECK (length(visitor_hash) = 32), day date NOT NULL, scope_key bytea NOT NULL,
            PRIMARY KEY (article_key, visitor_hash, day));
        CREATE INDEX tf_helpcenter_views_purge ON tf_helpcenter_views (scope_key, day);
        """),
        new(2, "perf-indexes", """
        CREATE INDEX tf_helpcenter_published_collection_order ON tf_helpcenter_published (portal_key, locale, collection_id, position, article_id COLLATE "C");
        CREATE INDEX tf_helpcenter_articles_portal_state ON tf_helpcenter_articles (portal_key, state, article_id COLLATE "C");
        """)]);
}
