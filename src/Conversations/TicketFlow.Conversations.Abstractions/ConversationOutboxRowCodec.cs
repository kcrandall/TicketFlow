using System.Globalization;
using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

/// <summary>
/// Published row contract for PostgreSQL <c>tf_conversation_outbox</c> rows consumed by change-data-capture adapters (such as
/// <c>TicketFlow.Integrations.WalFlow</c>). Tolerates both typed CLR values (snapshot reads) and string/<see cref="JsonElement"/>
/// representations (logical-replication streams), and validates every decoded value through <see cref="ConversationJsonCodec"/>.
/// </summary>
public static class ConversationOutboxRowCodec
{
    /// <summary>Default PostgreSQL table name for conversation outbox entries.</summary>
    public const string TableName = "tf_conversation_outbox";

    /// <summary>Supported row <c>format_version</c>.</summary>
    public const int FormatVersion = ConversationJsonCodec.FormatVersion;

    public const string SequenceColumn = "sequence";
    public const string EntryIdColumn = "entry_id";
    public const string TenantIdColumn = "tenant_id";
    public const string ProjectIdColumn = "project_id";
    public const string EnvironmentIdColumn = "environment_id";
    public const string AppIdColumn = "app_id";
    public const string WorkspaceIdColumn = "workspace_id";
    public const string ConversationIdColumn = "conversation_id";
    public const string InboxIdColumn = "inbox_id";
    public const string FormatVersionColumn = "format_version";
    public const string KindColumn = "kind";
    public const string RevisionColumn = "revision";
    public const string PayloadColumn = "payload";
    public const string OccurredAtColumn = "occurred_at";

    /// <summary>Decodes one <c>tf_conversation_outbox</c> row into a validated <see cref="ConversationOutboxEntry"/>.</summary>
    /// <exception cref="FormatException">A required column is missing, invalid or carries an unsupported format version.</exception>
    public static ConversationOutboxEntry Decode(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        try
        {
            var version = ReadInt32(row, FormatVersionColumn);
            if (version != FormatVersion)
                throw new FormatException($"Unsupported outbox row format version {version}.");
            var sequence = ReadInt64(row, SequenceColumn);
            if (sequence < 1)
                throw new FormatException("Outbox sequence must be positive.");
            var entryId = ReadGuid(row, EntryIdColumn);
            var scope = new TicketScope(
                ReadString(row, TenantIdColumn),
                ReadString(row, ProjectIdColumn),
                ReadString(row, EnvironmentIdColumn),
                ReadString(row, AppIdColumn),
                new WorkspaceId(ReadString(row, WorkspaceIdColumn)));
            var conversation = new ConversationId(ReadString(row, ConversationIdColumn));
            var inbox = new InboxId(ReadString(row, InboxIdColumn));
            var kindValue = ReadInt32(row, KindColumn);
            var kind = (ConversationChangeKind)kindValue;
            if (!Enum.IsDefined(kind))
                throw new FormatException($"Unknown conversation change kind {kindValue}.");
            var revision = ReadInt64(row, RevisionColumn);
            var payload = ReadString(row, PayloadColumn);
            var occurredAt = ReadInstant(row, OccurredAtColumn);
            var change = ConversationJsonCodec.DeserializeChange(payload, entryId, scope, conversation, inbox, revision, kind, occurredAt);
            return new ConversationOutboxEntry(sequence, change);
        }
        catch (FormatException)
        {
            throw;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException or JsonException)
        {
            throw new FormatException("The conversation outbox row is invalid.", error);
        }
    }

    /// <summary>Attempts to decode one <c>tf_conversation_outbox</c> row without throwing on malformed input.</summary>
    public static bool TryDecode(IReadOnlyDictionary<string, object?> row, out ConversationOutboxEntry? entry, out string? error)
    {
        ArgumentNullException.ThrowIfNull(row);
        try
        {
            entry = Decode(row);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            entry = null;
            error = ex.Message;
            return false;
        }
    }

    private static object Require(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
            throw new FormatException($"Missing required outbox column '{column}'.");
        if (value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
            throw new FormatException($"Missing required outbox column '{column}'.");
        return value;
    }

    private static string ReadString(IReadOnlyDictionary<string, object?> row, string column) => Require(row, column) switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
        _ => throw new FormatException($"Outbox column '{column}' must be a string."),
    };

    private static Guid ReadGuid(IReadOnlyDictionary<string, object?> row, string column) => Require(row, column) switch
    {
        Guid guid => guid,
        string text when Guid.TryParse(text, out var guid) => guid,
        JsonElement { ValueKind: JsonValueKind.String } element when element.TryGetGuid(out var guid) => guid,
        JsonElement { ValueKind: JsonValueKind.String } element when Guid.TryParse(element.GetString(), out var guid) => guid,
        _ => throw new FormatException($"Outbox column '{column}' must be a UUID."),
    };

    private static int ReadInt32(IReadOnlyDictionary<string, object?> row, string column)
    {
        var value = ReadInt64(row, column);
        return value is < int.MinValue or > int.MaxValue
            ? throw new FormatException($"Outbox column '{column}' is out of 32-bit range.")
            : (int)value;
    }

    private static long ReadInt64(IReadOnlyDictionary<string, object?> row, string column) => Require(row, column) switch
    {
        byte number => number,
        sbyte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        decimal number when decimal.Truncate(number) == number && number is >= long.MinValue and <= long.MaxValue => (long)number,
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var parsed) => parsed,
        JsonElement { ValueKind: JsonValueKind.String } element when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new FormatException($"Outbox column '{column}' must be an integer."),
    };

    private static DateTimeOffset ReadInstant(IReadOnlyDictionary<string, object?> row, string column) => Require(row, column) switch
    {
        DateTimeOffset instant => instant.ToUniversalTime(),
        DateTime dateTime => dateTime.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc))
            : new DateTimeOffset(dateTime.ToUniversalTime()),
        string text => ParseInstantString(text, column),
        long epoch => FromEpoch(epoch, column),
        int epoch => FromEpoch(epoch, column),
        JsonElement { ValueKind: JsonValueKind.String } element when element.TryGetDateTimeOffset(out var dt) => dt.ToUniversalTime(),
        JsonElement { ValueKind: JsonValueKind.String } element => ParseInstantString(element.GetString()!, column),
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var epoch) => FromEpoch(epoch, column),
        _ => throw new FormatException($"Outbox column '{column}' must be a timestamp."),
    };

    private static DateTimeOffset ParseInstantString(string text, string column)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            return FromEpoch(epoch, column);
        throw new FormatException($"Outbox column '{column}' is not a valid timestamp.");
    }

    private static DateTimeOffset FromEpoch(long epoch, string column)
    {
        // Debezium/WalFlow PostgreSQL timestamptz values may arrive as Unix microseconds (16+ digits), milliseconds (13 digits) or seconds.
        try
        {
            var abs = Math.Abs(epoch);
            if (abs >= 100_000_000_000_000L)
                return DateTimeOffset.UnixEpoch.AddTicks(checked(epoch * 10));
            if (abs >= 100_000_000_000L)
                return DateTimeOffset.FromUnixTimeMilliseconds(epoch);
            return DateTimeOffset.FromUnixTimeSeconds(epoch);
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
        {
            throw new FormatException($"Outbox column '{column}' timestamp is out of range.", error);
        }
    }
}
