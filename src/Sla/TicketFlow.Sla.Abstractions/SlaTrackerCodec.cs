using System.Text.Json;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>
/// Versioned persisted JSON for <see cref="SlaTracker"/> state: <c>{"v":1,"conversation":…,"inbox":…,"applied":{…},"facts":{…},
/// "clocks":[…],"pauses":[…],"pending":[…],"emissions":n,"access":{…},"lastSequence":n,"evaluatedAt":…,"schedule":{…}}</c>. The
/// applied rule uses the <see cref="SlaPolicyCodec"/> rule shape; pending emissions keep their <see cref="SlaEventCodec"/> kind and
/// payload. Decoding re-validates every value and rejects unknown versions and properties.
/// </summary>
public static class SlaTrackerCodec
{
    public const int FormatVersion = 1;

    public static string Serialize(SlaTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        return SlaJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("conversation", tracker.Conversation.Value);
            writer.WriteString("inbox", tracker.Inbox.Value);
            if (tracker.Applied is { } applied)
            {
                writer.WriteStartObject("applied");
                writer.WriteString("policy", applied.Policy.Value);
                writer.WriteNumber("policyRevision", applied.PolicyRevision);
                writer.WriteNumber("rule", applied.RuleIndex);
                writer.WriteNumber("risk", applied.AtRiskPercent);
                if (applied.Calendar is { } calendar)
                    writer.WriteString("calendar", calendar.Value);
                SlaPolicyCodec.WritePause(writer, applied.Pause);
                writer.WritePropertyName("def");
                SlaPolicyCodec.WriteRule(writer, applied.Rule);
                writer.WriteEndObject();
            }
            var facts = tracker.Facts;
            writer.WriteStartObject("facts");
            writer.WriteString("status", SlaJson.Name(facts.Status));
            SlaJson.WriteOptionalInstant(writer, "snoozedUntil", facts.SnoozedUntil);
            writer.WriteString("priority", SlaJson.Name(facts.Priority));
            SlaJson.WriteOptionalInstant(writer, "waitingSince", facts.WaitingSince);
            writer.WriteBoolean("replied", facts.Replied);
            writer.WriteEndObject();
            writer.WriteStartArray("clocks");
            foreach (var clock in tracker.Clocks)
            {
                writer.WriteStartObject();
                writer.WriteString("metric", SlaJson.Name(clock.Metric));
                writer.WriteNumber("cycle", clock.Cycle);
                SlaJson.WriteInstant(writer, "startedAt", clock.StartedAt);
                SlaJson.WriteDuration(writer, "target", clock.Target);
                SlaJson.WriteDuration(writer, "elapsed", clock.Elapsed);
                SlaJson.WriteOptionalInstant(writer, "runningSince", clock.RunningSince);
                writer.WriteString("state", SlaJson.Name(clock.State));
                SlaJson.WriteOptionalInstant(writer, "dueAt", clock.DueAt);
                SlaJson.WriteOptionalInstant(writer, "breachedAt", clock.BreachedAt);
                SlaJson.WriteOptionalInstant(writer, "completedAt", clock.CompletedAt);
                if (clock.Outcome is { } outcome)
                    writer.WriteString("outcome", SlaJson.Name(outcome));
                writer.WriteNumber("completions", clock.Completions);
                writer.WriteStartArray("fired");
                foreach (var threshold in clock.FiredThresholds)
                    writer.WriteNumberValue(threshold);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("pauses");
            foreach (var pause in tracker.Pauses)
            {
                writer.WriteStartObject();
                SlaJson.WriteInstant(writer, "start", pause.Start);
                SlaJson.WriteOptionalInstant(writer, "end", pause.End);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("pending");
            foreach (var pending in tracker.Pending)
            {
                writer.WriteStartObject();
                writer.WriteString("key", pending.Key);
                writer.WriteString("kind", pending.Event.Kind);
                writer.WriteString("payload", SlaEventCodec.Serialize(pending.Event));
                writer.WriteNumber("after", pending.AfterSequence);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteNumber("emissions", tracker.EmissionCount);
            writer.WriteStartObject("access");
            if (tracker.Access.Team is { } team)
                writer.WriteString("team", team.Value);
            if (tracker.Access.Assignee is { } assignee)
                writer.WriteString("assignee", assignee.Value);
            writer.WriteStartArray("labels");
            foreach (var label in tracker.Access.Labels)
                writer.WriteStringValue(label.Value);
            writer.WriteEndArray();
            writer.WriteStartObject("attributes");
            foreach (var (key, value) in tracker.Access.Attributes)
                writer.WriteString(key, value);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteNumber("lastSequence", tracker.LastSequence);
            SlaJson.WriteInstant(writer, "evaluatedAt", tracker.EvaluatedAt);
            SlaJson.WriteOptionalInstant(writer, "flushRetryAt", tracker.FlushRetryAt);
            var schedule = tracker.Schedule;
            writer.WriteStartObject("schedule");
            SlaJson.WriteOptionalInstant(writer, "next", schedule.NextCheckAt);
            SlaJson.WriteOptionalInstant(writer, "due", schedule.DueAt);
            SlaJson.WriteOptionalInstant(writer, "risk", schedule.AtRiskAt);
            writer.WriteBoolean("breached", schedule.Breached);
            SlaJson.WriteOptionalInstant(writer, "urgency", schedule.UrgencyAt);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, invalid or of an unknown version.</exception>
    public static SlaTracker Deserialize(string json) => SlaJson.Read(json, root =>
    {
        SlaJson.RequireProperties(root, "v", "conversation", "inbox", "applied", "facts", "clocks", "pauses", "pending", "emissions", "access", "lastSequence", "evaluatedAt", "flushRetryAt", "schedule");
        SlaJson.RequireVersion(root, FormatVersion);
        SlaAppliedRule? applied = null;
        if (root.TryGetProperty("applied", out var appliedElement))
        {
            SlaJson.RequireProperties(appliedElement, "policy", "policyRevision", "rule", "risk", "calendar", "pause", "def");
            applied = new SlaAppliedRule(new SlaPolicyId(SlaJson.String(appliedElement.GetProperty("policy"))), appliedElement.GetProperty("policyRevision").GetInt64(),
                appliedElement.GetProperty("rule").GetInt32(), SlaPolicyCodec.ReadRule(appliedElement.GetProperty("def")),
                SlaPolicyCodec.ReadPause(appliedElement.GetProperty("pause")), appliedElement.GetProperty("risk").GetInt32(),
                SlaJson.OptionalString(appliedElement, "calendar") is { } calendar ? new BusinessCalendarId(calendar) : null);
        }
        var factsElement = root.GetProperty("facts");
        SlaJson.RequireProperties(factsElement, "status", "snoozedUntil", "priority", "waitingSince", "replied");
        var facts = new SlaConversationFacts(SlaJson.Status(factsElement.GetProperty("status")), SlaJson.OptionalInstant(factsElement, "snoozedUntil"),
            SlaJson.Priority(factsElement.GetProperty("priority")), SlaJson.OptionalInstant(factsElement, "waitingSince"), SlaJson.Bool(factsElement.GetProperty("replied")));
        var clocks = SlaJson.Array(root.GetProperty("clocks")).Select(ReadClock).ToList();
        var pauses = SlaJson.Array(root.GetProperty("pauses")).Select(pause =>
        {
            SlaJson.RequireProperties(pause, "start", "end");
            return new SlaPauseInterval(SlaJson.Instant(pause.GetProperty("start")), SlaJson.OptionalInstant(pause, "end"));
        }).ToList();
        var pending = SlaJson.Array(root.GetProperty("pending")).Select(item =>
        {
            SlaJson.RequireProperties(item, "key", "kind", "payload", "after");
            var timelineEvent = SlaEventCodec.Deserialize(SlaJson.String(item.GetProperty("kind")), SlaEventCodec.PayloadVersion, SlaJson.String(item.GetProperty("payload")));
            var key = SlaJson.String(item.GetProperty("key"));
            if (timelineEvent.Key != key)
                throw new FormatException("A pending emission key does not match its payload.");
            return new SlaPendingEmission(key, timelineEvent, item.GetProperty("after").GetInt64());
        }).ToList();
        var accessElement = root.GetProperty("access");
        SlaJson.RequireProperties(accessElement, "team", "assignee", "labels", "attributes");
        var attributes = accessElement.GetProperty("attributes");
        if (attributes.ValueKind != JsonValueKind.Object)
            throw new FormatException("Access attributes are an object.");
        var access = new SlaAccessSnapshot(
            SlaJson.OptionalString(accessElement, "team") is { } team ? new TeamId(team) : null,
            SlaJson.OptionalString(accessElement, "assignee") is { } assignee ? new UserId(assignee) : null,
            [.. SlaJson.Array(accessElement.GetProperty("labels")).Select(label => new LabelId(SlaJson.String(label)))],
            [.. attributes.EnumerateObject().Select(property => new KeyValuePair<string, string>(property.Name, SlaJson.String(property.Value)))]);
        var scheduleElement = root.GetProperty("schedule");
        SlaJson.RequireProperties(scheduleElement, "next", "due", "risk", "breached", "urgency");
        var schedule = new SlaSchedule(SlaJson.OptionalInstant(scheduleElement, "next"), SlaJson.OptionalInstant(scheduleElement, "due"),
            SlaJson.OptionalInstant(scheduleElement, "risk"), SlaJson.Bool(scheduleElement.GetProperty("breached")), SlaJson.OptionalInstant(scheduleElement, "urgency"));
        return new SlaTracker(new ConversationId(SlaJson.String(root.GetProperty("conversation"))), new InboxId(SlaJson.String(root.GetProperty("inbox"))), applied, facts,
            clocks, pauses, pending, root.GetProperty("emissions").GetInt64(), access, root.GetProperty("lastSequence").GetInt64(),
            SlaJson.Instant(root.GetProperty("evaluatedAt")), schedule, SlaJson.OptionalInstant(root, "flushRetryAt"));
    });

    private static SlaClock ReadClock(JsonElement element)
    {
        SlaJson.RequireProperties(element, "metric", "cycle", "startedAt", "target", "elapsed", "runningSince", "state", "dueAt", "breachedAt", "completedAt", "outcome",
            "completions", "fired");
        return new SlaClock(SlaJson.Metric(element.GetProperty("metric")), element.GetProperty("cycle").GetInt32(), SlaJson.Instant(element.GetProperty("startedAt")),
            SlaJson.Duration(element.GetProperty("target")), SlaJson.Duration(element.GetProperty("elapsed")), SlaJson.OptionalInstant(element, "runningSince"),
            SlaJson.State(element.GetProperty("state")), SlaJson.OptionalInstant(element, "dueAt"), SlaJson.OptionalInstant(element, "breachedAt"),
            SlaJson.OptionalInstant(element, "completedAt"), element.TryGetProperty("outcome", out var outcome) ? SlaJson.Outcome(outcome) : null,
            element.GetProperty("completions").GetInt32(), [.. SlaJson.Array(element.GetProperty("fired")).Select(threshold => threshold.GetInt32())]);
    }
}
