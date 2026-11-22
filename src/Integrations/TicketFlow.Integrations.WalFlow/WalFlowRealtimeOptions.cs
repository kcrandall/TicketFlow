using System.Text.RegularExpressions;
using TicketFlow.Conversations;

namespace TicketFlow.Integrations.WalFlow;

public sealed partial class WalFlowRealtimeOptions
{
    public const int DefaultDedupeWindowCapacity = 10_000;
    public const int MaximumDedupeWindowCapacity = 1_000_000;
    public const int DefaultPostgresMaximumEnvelopeBytes = 7_500;
    public const int MaximumEnvelopeBytesLimit = 1024 * 1024;

    /// <summary>PostgreSQL table name matched on incoming <c>DebeziumPayload.Source.Table</c> (default <c>tf_conversation_outbox</c>).</summary>
    public string TableName { get; set; } = ConversationOutboxRowCodec.TableName;

    /// <summary>Optional schema filter matched against <c>DebeziumPayload.Source.Schema</c> (<see langword="null"/> matches any schema).</summary>
    public string? SchemaName { get; set; }

    /// <summary>Bounded FIFO window of recently published outbox <c>entry_id</c>s used for idempotent deduplication (default 10,000).</summary>
    public int DedupeWindowCapacity { get; set; } = DefaultDedupeWindowCapacity;

    /// <summary>PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> channel identifier (lowercase ASCII letters, digits and underscores, max 63 chars).</summary>
    public string PostgresNotifyChannel { get; set; } = "ticketflow_realtime";

    /// <summary>Maximum UTF-8 bytes of one serialized backplane envelope (default 7,500 bytes for PostgreSQL <c>NOTIFY</c> safety).</summary>
    public int MaximumEnvelopeBytes { get; set; } = DefaultPostgresMaximumEnvelopeBytes;

    /// <summary>Reconnect delay when the PostgreSQL <c>LISTEN</c> connection drops (default 2 seconds).</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(TableName) || TableName.Length > 128)
            throw new InvalidOperationException("TableName is required.");
        if (SchemaName is not null && (string.IsNullOrWhiteSpace(SchemaName) || SchemaName.Length > 128))
            throw new InvalidOperationException("SchemaName must be null or a non-empty identifier.");
        if (DedupeWindowCapacity is < 1 or > MaximumDedupeWindowCapacity)
            throw new InvalidOperationException($"DedupeWindowCapacity must be between 1 and {MaximumDedupeWindowCapacity}.");
        if (string.IsNullOrWhiteSpace(PostgresNotifyChannel) || !PgIdentifier().IsMatch(PostgresNotifyChannel))
            throw new InvalidOperationException("PostgresNotifyChannel must be a valid lowercase PostgreSQL identifier (1-63 chars).");
        if (MaximumEnvelopeBytes is < 512 or > MaximumEnvelopeBytesLimit)
            throw new InvalidOperationException($"MaximumEnvelopeBytes must be between 512 and {MaximumEnvelopeBytesLimit}.");
        if (ReconnectDelay < TimeSpan.FromMilliseconds(50) || ReconnectDelay > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("ReconnectDelay must be between 50ms and 5 minutes.");
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex PgIdentifier();
}
