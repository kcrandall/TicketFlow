using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Reporting;

namespace TicketFlow.Integrations.InsightFlow;

/// <summary>
/// Encodes TicketFlow <see cref="ReportFact"/>s into InsightFlow's <c>ClientTrackBatchRequest</c> camel-case JSON wire contract.
/// Every event carries a deterministic RFC 9562 UUIDv8 <c>eventId</c> derived from the fact key so retries deduplicate; properties
/// carry identifiers and numeric metrics only (never PII); <c>app</c> scope comes strictly from <see cref="InsightFlowExportOptions"/>.
/// </summary>
public static class InsightFlowClientEventEncoder
{
    private static ReadOnlySpan<byte> FactIdPrefix => "tf-insightflow-fact-v1\u001F"u8;

    /// <summary>Deterministic RFC 9562 UUIDv8 event id for one fact within <paramref name="scope"/>.</summary>
    public static Guid EventId(TicketScope scope, ReportFact fact)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fact);
        var workspace = scope.WorkspaceId.Value;
        var maxBytes = FactIdPrefix.Length
            + Encoding.UTF8.GetMaxByteCount(scope.TenantId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(scope.ProjectId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(scope.EnvironmentId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(scope.AppId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(workspace.Length) + 1
            + 36 + 1 + 4;
        byte[]? rented = null;
        var buffer = maxBytes <= 512 ? stackalloc byte[maxBytes] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes)).AsSpan(0, maxBytes);
        try
        {
            var pos = 0;
            FactIdPrefix.CopyTo(buffer);
            pos += FactIdPrefix.Length;
            pos += Encoding.UTF8.GetBytes(scope.TenantId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(scope.ProjectId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(scope.EnvironmentId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(scope.AppId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(workspace, buffer[pos..]);
            buffer[pos++] = 0x1F;
            fact.SourceId.TryFormat(buffer[pos..], out var guidBytes, "D");
            pos += guidBytes;
            buffer[pos++] = 0x1F;
            ((int)fact.Metric).TryFormat(buffer[pos..], out var metricBytes, provider: CultureInfo.InvariantCulture);
            pos += metricBytes;
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(buffer[..pos], hash);
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            return new Guid(hash[..16], bigEndian: true);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static string MetricName(ReportMetric metric) => metric switch
    {
        ReportMetric.ConversationCreated => "conversation_created",
        ReportMetric.ConversationResolved => "conversation_resolved",
        ReportMetric.ConversationReopened => "conversation_reopened",
        ReportMetric.MessageReceived => "message_received",
        ReportMetric.MessageSent => "message_sent",
        ReportMetric.FirstResponseTime => "first_response_time",
        ReportMetric.FirstResponseBusinessTime => "first_response_business_time",
        ReportMetric.ResponseTime => "response_time",
        ReportMetric.ResponseBusinessTime => "response_business_time",
        ReportMetric.ResolutionTime => "resolution_time",
        ReportMetric.ResolutionBusinessTime => "resolution_business_time",
        ReportMetric.Assignment => "assignment",
        ReportMetric.Handoff => "handoff",
        ReportMetric.TeamTransfer => "team_transfer",
        ReportMetric.SlaAchieved => "sla_achieved",
        ReportMetric.SlaBreached => "sla_breached",
        _ => throw new ArgumentOutOfRangeException(nameof(metric)),
    };

    /// <summary>Encodes <paramref name="facts"/> as a UTF-8 <c>ClientTrackBatchRequest</c> JSON document.</summary>
    public static byte[] EncodeBatch(TicketScope scope, IReadOnlyList<ReportFact> facts, InsightFlowExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return EncodeBatch(scope, facts, 0, facts.Count, options);
    }

    /// <summary>Encodes a slice of <paramref name="facts"/> as a UTF-8 <c>ClientTrackBatchRequest</c> JSON document.</summary>
    internal static byte[] EncodeBatch(TicketScope scope, IReadOnlyList<ReportFact> facts, int offset, int count, InsightFlowExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var buffer = new ArrayBufferWriter<byte>(Math.Min(256 + count * 384, 65_536));
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("app"u8);
            writer.WriteString("tenantId"u8, options.TenantId);
            writer.WriteString("projectId"u8, options.ProjectId);
            writer.WriteString("environment"u8, options.Environment);
            writer.WriteString("appName"u8, options.AppName);
            writer.WriteString("source"u8, options.AppSource);
            if (!string.IsNullOrWhiteSpace(options.AppVersion))
                writer.WriteString("version"u8, options.AppVersion);
            writer.WriteEndObject();

            writer.WriteStartObject("sharedProperties"u8);
            writer.WriteString("exporter"u8, "ticketflow"u8);
            writer.WriteNumber("schema_version"u8, 1);
            writer.WriteString("workspace_id"u8, scope.WorkspaceId.Value);
            writer.WriteEndObject();

            writer.WriteStartArray("events"u8);
            var end = offset + count;
            for (var i = offset; i < end; i++)
                WriteFact(writer, scope, facts[i], options);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteFact(Utf8JsonWriter writer, TicketScope scope, ReportFact fact, InsightFlowExportOptions options)
    {
        var metricName = MetricName(fact.Metric);
        Span<char> guid = stackalloc char[36];
        writer.WriteStartObject();
        writer.WriteString("eventName"u8, options.EventPrefix + metricName);
        EventId(scope, fact).TryFormat(guid, out _, "D");
        writer.WriteString("eventId"u8, guid);
        writer.WriteString("timestamp"u8, fact.OccurredAt.ToUniversalTime());
        if (fact.Dimensions.Agent is { } agent)
        {
            writer.WriteStartObject("identity"u8);
            writer.WriteString("id"u8, "agent:" + agent.Value);
            writer.WriteEndObject();
        }
        writer.WriteStartObject("properties"u8);
        writer.WriteString("metric"u8, metricName);
        fact.SourceId.TryFormat(guid, out _, "D");
        writer.WriteString("source_id"u8, guid);
        writer.WriteString("conversation_id"u8, fact.Conversation.Value);
        writer.WriteString("inbox_id"u8, fact.Dimensions.Inbox.Value);
        if (fact.Dimensions.Team is { } team)
            writer.WriteString("team_id"u8, team.Value);
        if (fact.Dimensions.Agent is { } attributedAgent)
            writer.WriteString("agent_id"u8, attributedAgent.Value);
        if (fact.Dimensions.Channel is { } channel)
            writer.WriteString("channel"u8, channel);
        writer.WriteString("priority"u8, fact.Dimensions.Priority.WireName);
        if (fact.Dimensions.Labels.Count > 0)
        {
            writer.WriteStartArray("labels"u8);
            foreach (var label in fact.Dimensions.Labels)
                writer.WriteStringValue(label.Value);
            writer.WriteEndArray();
        }
        writer.WriteNumber("value"u8, fact.Value);
        if (fact.Metric.IsDuration)
        {
            writer.WriteNumber("duration_us"u8, fact.Value);
            writer.WriteNumber("duration_ms"u8, fact.Value / 1000.0);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
