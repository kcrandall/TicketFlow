namespace TicketFlow.Stores.Migrations.Postgres;

/// <summary>
/// Frozen version-one SQL for database-generated, JSON-framed SHA-256 keys. Feature schemas index and key on these fixed-size
/// <c>bytea</c> values instead of raw composite identifiers (up to 256 UTF-16 units each), which could exceed PostgreSQL's B-tree
/// tuple limit. Never change this SQL: feature migration checksums and stored keys depend on it.
/// </summary>
public static class PostgresBoundedKeys
{
    // Only text arrays are accepted. JSON framing distinguishes components, nulls and delimiters;
    // explicit UTF-8 removes client/server encoding settings from the digest representation.
    public const string Version1Sql = """
        CREATE OR REPLACE FUNCTION tf_key_v1(VARIADIC parts text[]) RETURNS bytea
        LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE SET search_path=pg_catalog
        AS $$ SELECT sha256(convert_to(array_to_json(parts)::text,'UTF8')) $$;
        """;
}
