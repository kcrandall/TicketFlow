using Npgsql;

namespace TicketFlow;

/// <summary>Internal C# 14 extension members for reading nullable columns from <see cref="NpgsqlDataReader"/>.</summary>
internal static class PostgresReaderExtensions
{
    extension(NpgsqlDataReader reader)
    {
        public string? NullableText(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        public DateTimeOffset? NullableInstant(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

        public long? NullableInt64(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}
