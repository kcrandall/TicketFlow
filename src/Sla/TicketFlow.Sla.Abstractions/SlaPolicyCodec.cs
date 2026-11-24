using System.Text.Json;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>
/// Versioned persisted JSON for SLA policy definitions (everything except the identifier, which stores keep in their key):
/// <c>{"v":1,"name":…,"risk":75,"pause":{"pending":true,"snoozed":true},"rules":[…]}</c>. Rules carry <c>name</c>, <c>basis</c>
/// (<c>business|calendar</c>), <c>priorities</c>, <c>labels</c>, <c>targets</c> (microseconds per metric name) and <c>esc</c>
/// escalations whose actions carry an explicit <c>"a"</c> discriminator (<c>notify-user</c>, <c>notify-team</c>,
/// <c>raise-priority</c>, <c>reassign-team</c>). Unions are never serialized directly; decoding re-validates every value and rejects
/// unknown versions, discriminators and properties.
/// </summary>
public static class SlaPolicyCodec
{
    public const int FormatVersion = 1;

    public static string Serialize(SlaPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return SlaJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("name", policy.Name);
            writer.WriteNumber("risk", policy.AtRiskPercent);
            WritePause(writer, policy.Pause);
            writer.WriteStartArray("rules");
            foreach (var rule in policy.Rules)
                WriteRule(writer, rule);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, invalid or of an unknown version.</exception>
    public static SlaPolicy Deserialize(SlaPolicyId id, string json) => SlaJson.Read(json, root =>
    {
        SlaJson.RequireProperties(root, "v", "name", "risk", "pause", "rules");
        SlaJson.RequireVersion(root, FormatVersion);
        return new SlaPolicy(id, root.RequiredString("name"), [.. root.RequiredArray("rules").Select(ReadRule)],
            ReadPause(root.GetProperty("pause")), root.GetProperty("risk").GetInt32());
    });

    internal static void WritePause(Utf8JsonWriter writer, SlaPausePolicy pause)
    {
        writer.WriteStartObject("pause");
        writer.WriteBoolean("pending", pause.PauseWhenPending);
        writer.WriteBoolean("snoozed", pause.PauseWhenSnoozed);
        writer.WriteEndObject();
    }

    internal static SlaPausePolicy ReadPause(JsonElement element)
    {
        SlaJson.RequireProperties(element, "pending", "snoozed");
        return new(element.RequiredBool("pending"), element.RequiredBool("snoozed"));
    }

    internal static void WriteRule(Utf8JsonWriter writer, SlaRule rule)
    {
        writer.WriteStartObject();
        writer.WriteString("name", rule.Name);
        writer.WriteString("basis", SlaJson.Name(rule.Basis));
        writer.WriteStartArray("priorities");
        foreach (var priority in rule.Condition.Priorities)
            writer.WriteStringValue(priority.WireName);
        writer.WriteEndArray();
        writer.WriteStartArray("labels");
        foreach (var label in rule.Condition.Labels)
            writer.WriteStringValue(label.Value);
        writer.WriteEndArray();
        writer.WriteStartObject("targets");
        foreach (var metric in Enum.GetValues<SlaMetric>())
            SlaJson.WriteOptionalDuration(writer, metric.WireName, rule.Targets.For(metric));
        writer.WriteEndObject();
        writer.WriteStartArray("esc");
        foreach (var escalation in rule.Escalations)
        {
            writer.WriteStartObject();
            writer.WriteString("metric", escalation.Metric.WireName);
            writer.WriteNumber("pct", escalation.ThresholdPercent);
            SlaJson.WriteActions(writer, "actions", escalation.Actions);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    internal static SlaRule ReadRule(JsonElement element)
    {
        SlaJson.RequireProperties(element, "name", "basis", "priorities", "labels", "targets", "esc");
        var targets = element.GetProperty("targets");
        SlaJson.RequireProperties(targets, "first-response", "next-response", "resolution");
        return new SlaRule(
            element.RequiredString("name"),
            new SlaCondition([.. element.RequiredArray("priorities").Select(SlaJson.Priority)],
                [.. element.RequiredArray("labels").Select(label => new LabelId(label.RequiredString()))]),
            new SlaTargets(targets.OptionalDuration("first-response"), targets.OptionalDuration("next-response"), targets.OptionalDuration("resolution")),
            SlaJson.Basis(element.GetProperty("basis")),
            [.. element.RequiredArray("esc").Select(escalation =>
            {
                SlaJson.RequireProperties(escalation, "metric", "pct", "actions");
                return new SlaEscalation(SlaJson.Metric(escalation.GetProperty("metric")), escalation.GetProperty("pct").GetInt32(), SlaJson.Actions(escalation.GetProperty("actions")));
            })]);
    }
}
