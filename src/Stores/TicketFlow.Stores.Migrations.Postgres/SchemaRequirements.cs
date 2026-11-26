using System.Buffers;
using System.Text.Json;

namespace TicketFlow.Stores.Migrations.Postgres;

public sealed record PostgresColumnRequirement(string Name, string DataType, bool Nullable = false);
public sealed record PostgresForeignKeyRequirement(IReadOnlyList<string> Columns, string ReferencedTable, IReadOnlyList<string> ReferencedColumns, bool CascadeDelete = false);
public sealed record PostgresTableRequirement(string Name, IReadOnlyList<PostgresColumnRequirement> Columns, IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<IReadOnlyList<string>>? UniqueKeys = null, IReadOnlyList<PostgresForeignKeyRequirement>? ForeignKeys = null);

/// <summary>Feature-owned schema requirements. Checks existing column types/nullability and key order without executing historical SQL or rewriting data.</summary>
public static class PostgresSchemaRequirements
{
    public static PostgresSchemaBaseline Baseline(int version, IEnumerable<PostgresTableRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var tables = new List<object>();
        foreach (var table in requirements)
        {
            if (table is null || tables.Count == 100 || table.Columns.Count is < 1 or > 100 || table.PrimaryKey.Count is < 1 or > 32 || table.UniqueKeys?.Count > 32 || table.ForeignKeys?.Count > 32)
                throw new ArgumentException("Schema requirements must be bounded and include columns and a primary key.", nameof(requirements));
            Identifier(table.Name);
            foreach (var column in table.Columns)
            {
                Identifier(column.Name);
                if (column.DataType is not ("text" or "integer" or "bigint" or "boolean" or "uuid" or "bytea" or "jsonb" or "timestamp with time zone"))
                    throw new ArgumentException("Unsupported baseline column type.", nameof(requirements));
            }
            foreach (var key in new[] { table.PrimaryKey }.Concat(table.UniqueKeys ?? []))
            {
                if (key.Count is < 1 or > 32) throw new ArgumentException("Baseline keys must be bounded and nonempty.", nameof(requirements));
                foreach (var name in key)
                {
                    Identifier(name);
                    if (!table.Columns.Any(column => string.Equals(column.Name, name, StringComparison.Ordinal)))
                        throw new ArgumentException("Baseline keys must reference declared columns.", nameof(requirements));
                }
            }
            foreach (var foreignKey in table.ForeignKeys ?? [])
            {
                Identifier(foreignKey.ReferencedTable);
                if (foreignKey.Columns.Count is < 1 or > 32 || foreignKey.Columns.Count != foreignKey.ReferencedColumns.Count)
                    throw new ArgumentException("Baseline foreign keys must have bounded matching columns.", nameof(requirements));
                foreach (var name in foreignKey.Columns.Concat(foreignKey.ReferencedColumns)) Identifier(name);
                if (foreignKey.Columns.Any(name => !table.Columns.Any(column => string.Equals(column.Name, name, StringComparison.Ordinal))))
                    throw new ArgumentException("Baseline foreign keys must reference declared columns.", nameof(requirements));
            }
            tables.Add(new { name = table.Name,
                columns = table.Columns.Select(column => new { name = column.Name, type = column.DataType, nullable = column.Nullable }),
                keys = new[] { table.PrimaryKey }.Concat(table.UniqueKeys ?? []).Select((key, index) => new { columns = key, primary = index == 0 }),
                foreign_keys = (table.ForeignKeys ?? []).Select(key => new { columns = key.Columns, referenced_table = key.ReferencedTable, referenced_columns = key.ReferencedColumns, cascade = key.CascadeDelete }) });
        }
        if (tables.Count == 0)
            throw new ArgumentException("A baseline requires table requirements.", nameof(requirements));
        // Names/types use a fixed restricted alphabet, so this generated JSON literal cannot contain an SQL quote.
        var json = JsonSerializer.Serialize(tables);
        var sql = """
            WITH required AS (SELECT jsonb_array_elements('
            """.TrimEnd() + json + """
            '::jsonb) AS spec)
            SELECT bool_and(
              to_regclass(format('%I.%I',current_schema(),spec->>'name')) IS NOT NULL
              AND NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements(spec->'columns') col
                WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c
                  WHERE c.table_schema=current_schema() AND c.table_name=spec->>'name' AND c.column_name=col->>'name'
                    AND c.data_type=col->>'type' AND (c.is_nullable='YES')=(col->>'nullable')::boolean
                    AND (col->>'type'<>'text' OR EXISTS (
                      SELECT 1 FROM pg_attribute a JOIN pg_collation text_collation ON text_collation.oid=a.attcollation
                      WHERE a.attrelid=to_regclass(format('%I.%I',current_schema(),spec->>'name'))
                        AND a.attname=col->>'name' AND text_collation.collisdeterministic))))
              AND NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements(spec->'keys') required_key
                WHERE NOT EXISTS (SELECT 1 FROM pg_constraint k
                  WHERE k.conrelid=to_regclass(format('%I.%I',current_schema(),spec->>'name'))
                    AND k.convalidated AND NOT k.condeferrable
                    AND k.contype=CASE WHEN (required_key->>'primary')::boolean THEN 'p'::"char" ELSE 'u'::"char" END
                    AND (SELECT jsonb_agg(a.attname ORDER BY key.ordinality)
                      FROM unnest(k.conkey) WITH ORDINALITY key(attnum,ordinality)
                      JOIN pg_attribute a ON a.attrelid=k.conrelid AND a.attnum=key.attnum)=required_key->'columns'))
              AND NOT EXISTS (
                SELECT 1 FROM jsonb_array_elements(spec->'foreign_keys') required_fk
                WHERE NOT EXISTS (SELECT 1 FROM pg_constraint k
                  WHERE k.conrelid=to_regclass(format('%I.%I',current_schema(),spec->>'name')) AND k.contype='f'
                    AND k.confrelid=to_regclass(format('%I.%I',current_schema(),required_fk->>'referenced_table'))
                    AND k.convalidated AND NOT k.condeferrable
                    AND k.confdeltype=CASE WHEN (required_fk->>'cascade')::boolean THEN 'c'::"char" ELSE 'a'::"char" END
                    AND (SELECT jsonb_agg(a.attname ORDER BY key.ordinality) FROM unnest(k.conkey) WITH ORDINALITY key(attnum,ordinality)
                      JOIN pg_attribute a ON a.attrelid=k.conrelid AND a.attnum=key.attnum)=required_fk->'columns'
                    AND (SELECT jsonb_agg(a.attname ORDER BY key.ordinality) FROM unnest(k.confkey) WITH ORDINALITY key(attnum,ordinality)
                      JOIN pg_attribute a ON a.attrelid=k.confrelid AND a.attnum=key.attnum)=required_fk->'referenced_columns'))) FROM required
            """;
        return new(version, sql);
    }
    private static readonly SearchValues<char> IdentifierChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789_");

    private static void Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 63 || value.AsSpan().ContainsAnyExcept(IdentifierChars))
            throw new ArgumentException("Baseline identifiers require 1–63 lowercase ASCII letters, digits or underscores.", nameof(value));
    }
}
