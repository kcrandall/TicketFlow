using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    /// <summary>Deterministic RFC 9562 UUIDv8 event id for one fact within <paramref name="scope"/>.</summary>
    public static Guid EventId(TicketScope scope, ReportFact fact)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fact);
        var key = string.Join('\u001F', "tf-insightflow-fact-v1", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId,
            scope.WorkspaceId.Value, fact.SourceId.ToString("D"), ((int)fact.Metric).ToString(CultureInfo.InvariantCulture));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
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
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("app");
            writer.WriteString("tenantId", options.TenantId);
            writer.WriteString("projectId", options.ProjectId);
            writer.WriteString("environment", options.Environment);
            writer.WriteString("appName", options.AppName);
            writer.WriteString("source", options.AppSource);
            if (!string.IsNullOrWhiteSpace(options.AppVersion))
                writer.WriteString("version", options.AppVersion);
            writer.WriteEndObject();

            writer.WriteStartObject("sharedProperties");
            writer.WriteString("exporter", "ticketflow");
            writer.WriteNumber("schema_version", 1);
            writer.WriteString("workspace_id", scope.WorkspaceId.Value);
            writer.WriteEndObject();

            writer.WriteStartArray("events");
            foreach (var fact in facts)
                WriteFact(writer, scope, fact, options);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteFact(Utf8JsonWriter writer, TicketScope scope, ReportFact fact, InsightFlowExportOptions options)
    {
        var metricName = MetricName(fact.Metric);
        writer.WriteStartObject();
        writer.WriteString("eventName", options.EventPrefix + metricName);
        writer.WriteString("eventId", EventId(scope, fact).ToString("D"));
        writer.WriteString("timestamp", fact.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        if (fact.Dimensions.Agent is { } agent)
        {
            writer.WriteStartObject("identity");
            writer.WriteString("id", "agent:" + agent.Value);
            writer.WriteEndObject();
        }
        writer.WriteStartObject("properties");
        writer.WriteString("metric", metricName);
        writer.WriteString("source_id", fact.SourceId.ToString("D"));
        writer.WriteString("conversation_id", fact.Conversation.Value);
        writer.WriteString("inbox_id", fact.Dimensions.Inbox.Value);
        if (fact.Dimensions.Team is { } team)
            writer.WriteString("team_id", team.Value);
        if (fact.Dimensions.Agent is { } attributedAgent)
            writer.WriteString("agent_id", attributedAgent.Value);
        if (fact.Dimensions.Channel is { } channel)
            writer.WriteString("channel", channel);
        writer.WriteString("priority", fact.Dimensions.Priority.ToString().ToLowerInvariant());
        if (fact.Dimensions.Labels.Count > 0)
        {
            writer.WriteStartArray("labels");
            foreach (var label in fact.Dimensions.Labels)
                writer.WriteStringValue(label.Value);
            writer.WriteEndArray();
        }
        writer.WriteNumber("value", fact.Value);
        if (ReportMetrics.IsDuration(fact.Metric))
        {
            writer.WriteNumber("duration_us", fact.Value);
            writer.WriteNumber("duration_ms", fact.Value / 1000.0);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
