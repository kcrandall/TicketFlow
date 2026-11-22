using TicketFlow.Conversations;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>An SLA rule was applied (or re-applied after a priority/label change) to the conversation.</summary>
public sealed record SlaAppliedEvent(
    string Key,
    SlaPolicyId Policy,
    long PolicyRevision,
    int RuleIndex,
    string RuleName,
    SlaClockBasis Basis,
    BusinessCalendarId? Calendar,
    SlaTargets Targets,
    DateTimeOffset At);

/// <summary>A metric reached an escalation threshold below 100 % of its target. <see cref="Actions"/> are for E10/E7 to carry out.</summary>
public sealed record SlaWarningEvent(
    string Key,
    SlaPolicyId Policy,
    SlaMetric Metric,
    int Cycle,
    int ThresholdPercent,
    TimeSpan Target,
    DateTimeOffset At,
    DateTimeOffset? DueAt,
    IReadOnlyList<SlaAction> Actions)
{
    public bool Equals(SlaWarningEvent? other) =>
        other is not null && Key == other.Key && Policy == other.Policy && Metric == other.Metric && Cycle == other.Cycle && ThresholdPercent == other.ThresholdPercent
        && Target == other.Target && At == other.At && DueAt == other.DueAt && Actions.SequenceEqual(other.Actions);

    public override int GetHashCode() => HashCode.Combine(Key, Metric, Cycle, ThresholdPercent);
}

/// <summary>A metric's elapsed time reached its target at <see cref="At"/>. <see cref="Actions"/> are the 100 % escalation's actions.</summary>
public sealed record SlaBreachedEvent(
    string Key,
    SlaPolicyId Policy,
    SlaMetric Metric,
    int Cycle,
    TimeSpan Target,
    DateTimeOffset At,
    IReadOnlyList<SlaAction> Actions)
{
    public bool Equals(SlaBreachedEvent? other) =>
        other is not null && Key == other.Key && Policy == other.Policy && Metric == other.Metric && Cycle == other.Cycle && Target == other.Target
        && At == other.At && Actions.SequenceEqual(other.Actions);

    public override int GetHashCode() => HashCode.Combine(Key, Metric, Cycle);
}

/// <summary>A metric was satisfied at <see cref="At"/> after <see cref="Elapsed"/> measured time; <see cref="Breached"/> when late.</summary>
public sealed record SlaMetEvent(
    string Key,
    SlaPolicyId Policy,
    SlaMetric Metric,
    int Cycle,
    TimeSpan Target,
    TimeSpan Elapsed,
    bool Breached,
    DateTimeOffset At);

/// <summary>An SLA timeline event. Switch over every case without a discard arm; persisted only through <see cref="SlaEventCodec"/>.</summary>
public union SlaTimelineEvent(SlaAppliedEvent, SlaWarningEvent, SlaBreachedEvent, SlaMetEvent)
{
    /// <summary>The <see cref="ExtensionEvent.Kind"/>.</summary>
    public string Kind => this switch
    {
        SlaAppliedEvent => SlaEventCodec.AppliedKind,
        SlaWarningEvent => SlaEventCodec.WarningKind,
        SlaBreachedEvent => SlaEventCodec.BreachedKind,
        SlaMetEvent => SlaEventCodec.MetKind,
    };

    public string Key => this switch
    {
        SlaAppliedEvent applied => applied.Key,
        SlaWarningEvent warning => warning.Key,
        SlaBreachedEvent breached => breached.Key,
        SlaMetEvent met => met.Key,
    };
}

/// <summary>
/// Encodes SLA timeline events as conversation <see cref="ExtensionEvent"/>s with payload version <see cref="PayloadVersion"/>. Payloads
/// are compact JSON objects <c>{"v":1,"key":…,"policy":…,…}</c>: instants are UTC ISO-8601 with microseconds, durations integer
/// microseconds, metrics <c>first-response|next-response|resolution</c> and actions use the policy codec's <c>"a"</c>
/// discriminators. Consumers (automation, notifications, reporting) decode with <see cref="TryDecode"/>.
/// </summary>
public static class SlaEventCodec
{
    public const string AppliedKind = "sla.applied";
    public const string WarningKind = "sla.warning";
    public const string BreachedKind = "sla.breached";
    public const string MetKind = "sla.met";
    public const int PayloadVersion = 1;
    public const int MaximumKeyLength = 128;

    public static ExtensionEvent Encode(SlaTimelineEvent timelineEvent) => new(timelineEvent.Kind, PayloadVersion, Serialize(timelineEvent));

    public static string Serialize(SlaTimelineEvent timelineEvent)
    {
        if (timelineEvent.Value is null)
            throw new ArgumentException("An event is required.", nameof(timelineEvent));
        return SlaJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", PayloadVersion);
            switch (timelineEvent)
            {
                case SlaAppliedEvent applied:
                    writer.WriteString("key", applied.Key);
                    writer.WriteString("policy", applied.Policy.Value);
                    writer.WriteNumber("policyRevision", applied.PolicyRevision);
                    writer.WriteNumber("rule", applied.RuleIndex);
                    writer.WriteString("ruleName", applied.RuleName);
                    writer.WriteString("basis", SlaJson.Name(applied.Basis));
                    if (applied.Calendar is { } calendar)
                        writer.WriteString("calendar", calendar.Value);
                    writer.WriteStartObject("targets");
                    foreach (var metric in Enum.GetValues<SlaMetric>())
                        SlaJson.WriteOptionalDuration(writer, SlaJson.Name(metric), applied.Targets.For(metric));
                    writer.WriteEndObject();
                    SlaJson.WriteInstant(writer, "at", applied.At);
                    break;
                case SlaWarningEvent warning:
                    writer.WriteString("key", warning.Key);
                    writer.WriteString("policy", warning.Policy.Value);
                    writer.WriteString("metric", SlaJson.Name(warning.Metric));
                    writer.WriteNumber("cycle", warning.Cycle);
                    writer.WriteNumber("pct", warning.ThresholdPercent);
                    SlaJson.WriteDuration(writer, "target", warning.Target);
                    SlaJson.WriteInstant(writer, "at", warning.At);
                    SlaJson.WriteOptionalInstant(writer, "dueAt", warning.DueAt);
                    SlaJson.WriteActions(writer, "actions", warning.Actions);
                    break;
                case SlaBreachedEvent breached:
                    writer.WriteString("key", breached.Key);
                    writer.WriteString("policy", breached.Policy.Value);
                    writer.WriteString("metric", SlaJson.Name(breached.Metric));
                    writer.WriteNumber("cycle", breached.Cycle);
                    SlaJson.WriteDuration(writer, "target", breached.Target);
                    SlaJson.WriteInstant(writer, "at", breached.At);
                    SlaJson.WriteActions(writer, "actions", breached.Actions);
                    break;
                case SlaMetEvent met:
                    writer.WriteString("key", met.Key);
                    writer.WriteString("policy", met.Policy.Value);
                    writer.WriteString("metric", SlaJson.Name(met.Metric));
                    writer.WriteNumber("cycle", met.Cycle);
                    SlaJson.WriteDuration(writer, "target", met.Target);
                    SlaJson.WriteDuration(writer, "elapsed", met.Elapsed);
                    writer.WriteBoolean("breached", met.Breached);
                    SlaJson.WriteInstant(writer, "at", met.At);
                    break;
            }
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The kind, version or payload is unknown or invalid.</exception>
    public static SlaTimelineEvent Deserialize(string kind, int payloadVersion, string payload)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (payloadVersion != PayloadVersion)
            throw new FormatException("Unsupported SLA payload version.");
        return SlaJson.Read(payload, root =>
        {
            SlaTimelineEvent result;
            switch (kind)
            {
                case AppliedKind:
                {
                    SlaJson.RequireProperties(root, "v", "key", "policy", "policyRevision", "rule", "ruleName", "basis", "calendar", "targets", "at");
                    var targets = root.GetProperty("targets");
                    SlaJson.RequireProperties(targets, "first-response", "next-response", "resolution");
                    result = new SlaAppliedEvent(Key(root), Policy(root), root.GetProperty("policyRevision").GetInt64(), root.GetProperty("rule").GetInt32(),
                        SlaText.RequireText(SlaJson.String(root.GetProperty("ruleName")), SlaRule.MaximumNameLength, "ruleName"), SlaJson.Basis(root.GetProperty("basis")),
                        SlaJson.OptionalString(root, "calendar") is { } calendar ? new BusinessCalendarId(calendar) : null,
                        new SlaTargets(SlaJson.OptionalDuration(targets, "first-response"), SlaJson.OptionalDuration(targets, "next-response"), SlaJson.OptionalDuration(targets, "resolution")),
                        SlaJson.Instant(root.GetProperty("at")));
                    break;
                }
                case WarningKind:
                    SlaJson.RequireProperties(root, "v", "key", "policy", "metric", "cycle", "pct", "target", "at", "dueAt", "actions");
                    result = new SlaWarningEvent(Key(root), Policy(root), SlaJson.Metric(root.GetProperty("metric")), root.GetProperty("cycle").GetInt32(),
                        root.GetProperty("pct").GetInt32(), SlaJson.Duration(root.GetProperty("target")), SlaJson.Instant(root.GetProperty("at")),
                        SlaJson.OptionalInstant(root, "dueAt"), SlaJson.Actions(root.GetProperty("actions")));
                    break;
                case BreachedKind:
                    SlaJson.RequireProperties(root, "v", "key", "policy", "metric", "cycle", "target", "at", "actions");
                    result = new SlaBreachedEvent(Key(root), Policy(root), SlaJson.Metric(root.GetProperty("metric")), root.GetProperty("cycle").GetInt32(),
                        SlaJson.Duration(root.GetProperty("target")), SlaJson.Instant(root.GetProperty("at")), SlaJson.Actions(root.GetProperty("actions")));
                    break;
                case MetKind:
                    SlaJson.RequireProperties(root, "v", "key", "policy", "metric", "cycle", "target", "elapsed", "breached", "at");
                    result = new SlaMetEvent(Key(root), Policy(root), SlaJson.Metric(root.GetProperty("metric")), root.GetProperty("cycle").GetInt32(),
                        SlaJson.Duration(root.GetProperty("target")), SlaJson.Duration(root.GetProperty("elapsed")), SlaJson.Bool(root.GetProperty("breached")),
                        SlaJson.Instant(root.GetProperty("at")));
                    break;
                default:
                    throw new FormatException("Unknown SLA event kind.");
            }
            SlaJson.RequireVersion(root, PayloadVersion);
            return result;
        });
    }

    /// <summary>Decodes an extension event owned by SLA; <see langword="false"/> for other kinds or invalid payloads.</summary>
    public static bool TryDecode(ExtensionEvent extension, out SlaTimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(extension);
        timelineEvent = default;
        if (!extension.Kind.StartsWith("sla.", StringComparison.Ordinal))
            return false;
        try
        {
            timelineEvent = Deserialize(extension.Kind, extension.PayloadVersion, extension.Payload);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Key(System.Text.Json.JsonElement root) => SlaText.RequireText(SlaJson.String(root.GetProperty("key")), MaximumKeyLength, "key");

    private static SlaPolicyId Policy(System.Text.Json.JsonElement root) => new(SlaJson.String(root.GetProperty("policy")));
}
