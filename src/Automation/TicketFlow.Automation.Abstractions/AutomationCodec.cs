using System.Globalization;
using System.Text.Json;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.Automation;

/// <summary>
/// Versioned persisted JSON for automation definitions and execution records (identifiers stay in store keys). Every envelope is
/// <c>{"v":1,...}</c> with explicit discriminators — triggers <c>"t"</c>, condition nodes <c>"op"</c>, actions <c>"a"</c>, outcomes
/// <c>"o"</c>, attribute values <c>"t"</c>. Unions and closed hierarchies are never serialized directly; decoding re-validates every
/// value through the model constructors and rejects unknown versions, discriminators and properties.
/// </summary>
public static class AutomationCodec
{
    public const int FormatVersion = 1;

    public static string SerializeRule(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return AutomationJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("name", rule.Name);
            writer.WriteBoolean("enabled", rule.Enabled);
            writer.WriteNumber("priority", rule.Priority);
            writer.WriteBoolean("stop", rule.StopProcessing);
            writer.WriteBoolean("auto", rule.RunOnAutomationEvents);
            writer.WritePropertyName("trigger");
            WriteTrigger(writer, rule.Trigger);
            if (rule.Condition is { } condition)
            {
                writer.WritePropertyName("cond");
                WriteCondition(writer, condition);
            }
            WriteActions(writer, "actions", rule.Actions);
            writer.WriteEndObject();
        });
    }

    /// <exception cref="FormatException">The payload is malformed, invalid or of an unknown version.</exception>
    public static AutomationRule DeserializeRule(AutomationRuleId id, string json) => AutomationJson.Read(json, root =>
    {
        AutomationJson.RequireProperties(root, "v", "name", "enabled", "priority", "stop", "auto", "trigger", "cond", "actions");
        AutomationJson.RequireVersion(root, FormatVersion);
        var regexCount = 0;
        return new AutomationRule(id, root.RequiredString("name"), ReadTrigger(root.GetProperty("trigger")),
            root["cond"] is { } condition ? ReadCondition(condition, 1, ref regexCount) : null, ReadActions(root.GetProperty("actions")),
            root.RequiredInt("priority"), root.RequiredBool("enabled"), root.RequiredBool("stop"),
            root.RequiredBool("auto"));
    });

    public static string SerializeMacro(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        return AutomationJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("name", macro.Name);
            writer.WriteString("vis", AutomationJson.Kebab(macro.Visibility));
            if (macro.Owner is { } owner)
                writer.WriteString("owner", owner.Value);
            if (macro.Description is { } description)
                writer.WriteString("desc", description);
            WriteActions(writer, "actions", macro.Actions);
            writer.WriteEndObject();
        });
    }

    public static Macro DeserializeMacro(MacroId id, string json) => AutomationJson.Read(json, root =>
    {
        AutomationJson.RequireProperties(root, "v", "name", "vis", "owner", "desc", "actions");
        AutomationJson.RequireVersion(root, FormatVersion);
        return new Macro(id, root.RequiredString("name"), root.RequiredKebab<MacroVisibility>("vis"),
            root.OptionalString("owner") is { } owner ? new UserId(owner) : null, ReadActions(root.GetProperty("actions")),
            root.OptionalString("desc"));
    });

    public static string SerializeCannedResponse(CannedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return AutomationJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("shortcut", response.Shortcut);
            writer.WriteString("title", response.Title);
            writer.WriteString("default", response.DefaultLocale);
            writer.WriteString("content", AutomationJson.Kebab(response.ContentType));
            writer.WriteStartArray("variants");
            foreach (var variant in response.Variants)
            {
                writer.WriteStartObject();
                writer.WriteString("locale", variant.Locale);
                writer.WriteString("template", variant.Template.Source);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static CannedResponse DeserializeCannedResponse(CannedResponseId id, string json) => AutomationJson.Read(json, root =>
    {
        AutomationJson.RequireProperties(root, "v", "shortcut", "title", "default", "content", "variants");
        AutomationJson.RequireVersion(root, FormatVersion);
        return new CannedResponse(id, root.RequiredString("shortcut"), root.RequiredString("title"),
            root.RequiredString("default"),
            [.. root.RequiredArray("variants").Select(variant =>
            {
                AutomationJson.RequireProperties(variant, "locale", "template");
                return new CannedResponseVariant(variant.RequiredString("locale"), new AutomationTemplate(variant.RequiredString("template")));
            })],
            root.RequiredKebab<MessageContentType>("content"));
    });

    public static string SerializeExecution(AutomationExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return AutomationJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", FormatVersion);
            writer.WriteString("id", execution.Id.ToString("D"));
            writer.WriteString("rule", execution.Rule.Value);
            if (execution.Conversation is { } conversation)
                writer.WriteString("conv", conversation.Value);
            writer.WriteString("trigger", execution.TriggerKey);
            writer.WriteString("event", AutomationJson.Kebab(execution.EventKind));
            writer.WriteNumber("depth", execution.Depth);
            writer.WriteString("status", AutomationJson.Kebab(execution.Status));
            if (execution.Suppression is { } suppression)
                writer.WriteString("supp", AutomationJson.Kebab(suppression));
            AutomationJson.WriteInstant(writer, "started", execution.StartedAt);
            if (execution.CompletedAt is { } completed)
                AutomationJson.WriteInstant(writer, "completed", completed);
            writer.WriteNumber("actions", execution.ActionCount);
            writer.WriteStartArray("outcomes");
            foreach (var outcome in execution.Outcomes)
                WriteOutcome(writer, outcome);
            writer.WriteEndArray();
            if (execution.Marker is { } marker)
                writer.WriteNumber("marker", marker);
            writer.WriteEndObject();
        });
    }

    public static AutomationExecution DeserializeExecution(string json) => AutomationJson.Read(json, root =>
    {
        AutomationJson.RequireProperties(root, "v", "id", "rule", "conv", "trigger", "event", "depth", "status", "supp", "started", "completed", "actions", "outcomes", "marker");
        AutomationJson.RequireVersion(root, FormatVersion);
        return new AutomationExecution(Guid.ParseExact(root.RequiredString("id"), "D"), new AutomationRuleId(root.RequiredString("rule")),
            root.OptionalString("conv") is { } conversation ? new ConversationId(conversation) : null, root.RequiredString("trigger"),
            root.RequiredKebab<AutomationEventKind>("event"), root.RequiredInt("depth"),
            root.RequiredKebab<AutomationExecutionStatus>("status"),
            root["supp"] is { } suppression ? AutomationJson.ParseKebab<AutomationSuppression>(suppression) : null,
            root.RequiredInstant("started"), root.OptionalInstant("completed"), root.RequiredInt("actions"),
            [.. root.RequiredArray("outcomes").Select(ReadOutcome)],
            root["marker"] is { } marker ? marker.RequiredLong() : null);
    });

    /// <summary>Serializes an action list (used by rule and macro envelopes).</summary>
    public static string SerializeActions(IReadOnlyList<AutomationAction> actions) => AutomationJson.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", FormatVersion);
        WriteActions(writer, "actions", actions);
        writer.WriteEndObject();
    });

    public static IReadOnlyList<AutomationAction> DeserializeActions(string json) => AutomationJson.Read(json, root =>
    {
        AutomationJson.RequireProperties(root, "v", "actions");
        AutomationJson.RequireVersion(root, FormatVersion);
        return ReadActions(root.GetProperty("actions"));
    });

    private static void WriteTrigger(Utf8JsonWriter writer, AutomationTrigger trigger)
    {
        writer.WriteStartObject();
        switch (trigger)
        {
            case ConversationCreatedTrigger:
                writer.WriteString("t", "conversation-created");
                break;
            case MessageReceivedTrigger:
                writer.WriteString("t", "message-received");
                break;
            case MessageSentTrigger:
                writer.WriteString("t", "message-sent");
                break;
            case ConversationUpdatedTrigger updated:
                writer.WriteString("t", "conversation-updated");
                AutomationJson.WriteStrings(writer, "fields", updated.Fields.Select(AutomationJson.Kebab));
                break;
            case StatusChangedTrigger status:
                writer.WriteString("t", "status-changed");
                AutomationJson.WriteStrings(writer, "to", status.To.Select(item => item.WireName));
                break;
            case AssignmentChangedTrigger:
                writer.WriteString("t", "assignment-changed");
                break;
            case SlaEventTrigger sla:
                writer.WriteString("t", "sla-event");
                AutomationJson.WriteStrings(writer, "kinds", sla.Kinds.Select(AutomationJson.Kebab));
                AutomationJson.WriteStrings(writer, "metrics", sla.Metrics.Select(item => item.WireName));
                break;
            case ScheduledTrigger scheduled:
                writer.WriteString("t", "scheduled");
                writer.WriteString("cron", scheduled.Schedule.Expression);
                writer.WriteString("tz", scheduled.Schedule.TimeZoneId);
                AutomationJson.WriteStrings(writer, "statuses", scheduled.Statuses.Select(item => item.WireName));
                if (scheduled.Inbox is { } inbox)
                    writer.WriteString("inbox", inbox.Value);
                writer.WriteNumber("max", scheduled.MaximumConversations);
                break;
            case ContactUpdatedTrigger:
                writer.WriteString("t", "contact-updated");
                break;
        }
        writer.WriteEndObject();
    }

    private static AutomationTrigger ReadTrigger(JsonElement element)
    {
        switch (element.RequiredString("t"))
        {
            case "conversation-created":
                AutomationJson.RequireProperties(element, "t");
                return ConversationCreatedTrigger.Instance;
            case "message-received":
                AutomationJson.RequireProperties(element, "t");
                return MessageReceivedTrigger.Instance;
            case "message-sent":
                AutomationJson.RequireProperties(element, "t");
                return MessageSentTrigger.Instance;
            case "conversation-updated":
                AutomationJson.RequireProperties(element, "t", "fields");
                return new ConversationUpdatedTrigger(element.RequiredArray("fields").Select(AutomationJson.ParseKebab<ConversationUpdateField>));
            case "status-changed":
                AutomationJson.RequireProperties(element, "t", "to");
                return new StatusChangedTrigger(element.RequiredArray("to").Select(AutomationJson.Status));
            case "assignment-changed":
                AutomationJson.RequireProperties(element, "t");
                return AssignmentChangedTrigger.Instance;
            case "sla-event":
                AutomationJson.RequireProperties(element, "t", "kinds", "metrics");
                return new SlaEventTrigger(element.RequiredArray("kinds").Select(AutomationJson.ParseKebab<SlaEventKind>),
                    element.RequiredArray("metrics").Select(AutomationJson.Metric));
            case "scheduled":
                AutomationJson.RequireProperties(element, "t", "cron", "tz", "statuses", "inbox", "max");
                return new ScheduledTrigger(new AutomationSchedule(element.RequiredString("cron"), element.RequiredString("tz")),
                    element.RequiredArray("statuses").Select(AutomationJson.Status),
                    element.OptionalString("inbox") is { } inbox ? new InboxId(inbox) : null, element.RequiredInt("max"));
            case "contact-updated":
                AutomationJson.RequireProperties(element, "t");
                return ContactUpdatedTrigger.Instance;
            default:
                throw new FormatException("Unknown automation trigger.");
        }
    }

    private static void WriteCondition(Utf8JsonWriter writer, AutomationCondition condition)
    {
        writer.WriteStartObject();
        switch (condition)
        {
            case AllOfCondition all:
                writer.WriteString("op", "all");
                writer.WriteStartArray("items");
                foreach (var item in all.Items)
                    WriteCondition(writer, item);
                writer.WriteEndArray();
                break;
            case AnyOfCondition any:
                writer.WriteString("op", "any");
                writer.WriteStartArray("items");
                foreach (var item in any.Items)
                    WriteCondition(writer, item);
                writer.WriteEndArray();
                break;
            case NegatedCondition negated:
                writer.WriteString("op", "not");
                writer.WritePropertyName("item");
                WriteCondition(writer, negated.Operand);
                break;
            case StatusIsCondition status:
                writer.WriteString("op", "status");
                AutomationJson.WriteStrings(writer, "values", status.Statuses.Select(item => item.WireName));
                break;
            case PriorityCondition priority:
                writer.WriteString("op", "priority");
                writer.WriteString("cmp", AutomationJson.Kebab(priority.Comparison));
                writer.WriteString("value", priority.Priority.WireName);
                break;
            case InboxIsCondition inbox:
                writer.WriteString("op", "inbox");
                AutomationJson.WriteStrings(writer, "values", inbox.Inboxes.Select(item => item.Value));
                break;
            case TeamIsCondition team:
                writer.WriteString("op", "team");
                AutomationJson.WriteStrings(writer, "values", team.Teams.Select(item => item.Value));
                writer.WriteBoolean("none", team.OrNone);
                break;
            case AssigneeIsCondition assignee:
                writer.WriteString("op", "assignee");
                AutomationJson.WriteStrings(writer, "values", assignee.Users.Select(item => item.Value));
                writer.WriteBoolean("none", assignee.OrUnassigned);
                break;
            case HasLabelsCondition labels:
                writer.WriteString("op", "labels");
                writer.WriteString("target", AutomationJson.Kebab(labels.Target));
                writer.WriteString("match", AutomationJson.Kebab(labels.Match));
                AutomationJson.WriteStrings(writer, "values", labels.Labels.Select(item => item.Value));
                break;
            case ChannelKindIsCondition channel:
                writer.WriteString("op", "channel");
                AutomationJson.WriteStrings(writer, "values", channel.Kinds.Select(item => item.Value));
                break;
            case AttributeCondition attribute:
                writer.WriteString("op", "attribute");
                writer.WriteString("target", AutomationJson.Kebab(attribute.Target));
                writer.WriteString("key", attribute.Key);
                writer.WriteString("cmp", AutomationJson.Kebab(attribute.Operator));
                AutomationJson.WriteStrings(writer, "values", attribute.Values);
                break;
            case MessageContentCondition message:
                writer.WriteString("op", "message");
                writer.WriteString("cmp", AutomationJson.Kebab(message.Operator));
                writer.WriteString("value", message.Value);
                writer.WriteBoolean("ci", message.IgnoreCase);
                writer.WriteNumber("timeoutMs", (int)message.Timeout.TotalMilliseconds);
                break;
            case BusinessHoursCondition hours:
                writer.WriteString("op", "business-hours");
                writer.WriteBoolean("open", hours.Open);
                break;
            case ElapsedCondition elapsed:
                writer.WriteString("op", "elapsed");
                writer.WriteString("since", AutomationJson.Kebab(elapsed.Since));
                writer.WriteString("cmp", AutomationJson.Kebab(elapsed.Comparison));
                writer.WriteNumber("seconds", (long)elapsed.Duration.TotalSeconds);
                break;
        }
        writer.WriteEndObject();
    }

    private static AutomationCondition ReadCondition(JsonElement element, int depth, ref int regexCount)
    {
        if (depth > AutomationCondition.MaximumDepth)
            throw new FormatException("The condition is too deep.");
        switch (element.RequiredString("op"))
        {
            case "all":
                AutomationJson.RequireProperties(element, "op", "items");
                return new AllOfCondition(ReadConditionItems(element.RequiredArray("items"), depth + 1, ref regexCount));
            case "any":
                AutomationJson.RequireProperties(element, "op", "items");
                return new AnyOfCondition(ReadConditionItems(element.RequiredArray("items"), depth + 1, ref regexCount));
            case "not":
                AutomationJson.RequireProperties(element, "op", "item");
                return new NegatedCondition(ReadCondition(element.GetProperty("item"), depth + 1, ref regexCount));
            case "status":
                AutomationJson.RequireProperties(element, "op", "values");
                return new StatusIsCondition(element.RequiredArray("values").Select(AutomationJson.Status));
            case "priority":
                AutomationJson.RequireProperties(element, "op", "cmp", "value");
                return new PriorityCondition(element.RequiredKebab<PriorityComparison>("cmp"), AutomationJson.Priority(element.GetProperty("value")));
            case "inbox":
                AutomationJson.RequireProperties(element, "op", "values");
                return new InboxIsCondition(element.RequiredStrings("values").Select(value => new InboxId(value)));
            case "team":
                AutomationJson.RequireProperties(element, "op", "values", "none");
                return new TeamIsCondition(element.RequiredStrings("values").Select(value => new TeamId(value)), element.RequiredBool("none"));
            case "assignee":
                AutomationJson.RequireProperties(element, "op", "values", "none");
                return new AssigneeIsCondition(element.RequiredStrings("values").Select(value => new UserId(value)), element.RequiredBool("none"));
            case "labels":
                AutomationJson.RequireProperties(element, "op", "target", "match", "values");
                return new HasLabelsCondition(element.RequiredKebab<ConditionTarget>("target"), element.RequiredKebab<LabelMatch>("match"),
                    element.RequiredStrings("values").Select(value => new LabelId(value)));
            case "channel":
                AutomationJson.RequireProperties(element, "op", "values");
                return new ChannelKindIsCondition(element.RequiredStrings("values").Select(value => new ChannelKind(value)));
            case "attribute":
                AutomationJson.RequireProperties(element, "op", "target", "key", "cmp", "values");
                return new AttributeCondition(element.RequiredKebab<ConditionTarget>("target"), element.RequiredString("key"),
                    element.RequiredKebab<AttributeOperator>("cmp"), element.RequiredStrings("values"));
            case "message":
                AutomationJson.RequireProperties(element, "op", "cmp", "value", "ci", "timeoutMs");
                var messageOperator = element.RequiredKebab<MessageTextOperator>("cmp");
                if (messageOperator == MessageTextOperator.Matches && ++regexCount > AutomationCondition.MaximumRegexConditions)
                    throw new FormatException($"Conditions can contain at most {AutomationCondition.MaximumRegexConditions} regular expression matches.");
                return new MessageContentCondition(messageOperator, element.RequiredString("value"),
                    element.RequiredBool("ci"), TimeSpan.FromMilliseconds(element.RequiredInt("timeoutMs")));
            case "business-hours":
                AutomationJson.RequireProperties(element, "op", "open");
                return new BusinessHoursCondition(element.RequiredBool("open"));
            case "elapsed":
                AutomationJson.RequireProperties(element, "op", "since", "cmp", "seconds");
                return new ElapsedCondition(element.RequiredKebab<ConversationTimestamp>("since"),
                    element.RequiredKebab<ElapsedComparison>("cmp"), TimeSpan.FromSeconds(element.RequiredLong("seconds")));
            default:
                throw new FormatException("Unknown condition operator.");
        }
    }

    private static List<AutomationCondition> ReadConditionItems(JsonElement.ArrayEnumerator items, int depth, ref int regexCount)
    {
        var result = new List<AutomationCondition>();
        foreach (var item in items)
        {
            result.Add(ReadCondition(item, depth, ref regexCount));
            if (result.Count > AutomationCondition.MaximumNodes)
                break;
        }
        return result;
    }

    private static void WriteActions(Utf8JsonWriter writer, string name, IReadOnlyList<AutomationAction> actions)
    {
        writer.WriteStartArray(name);
        foreach (var action in actions)
            WriteAction(writer, action);
        writer.WriteEndArray();
    }

    private static IReadOnlyList<AutomationAction> ReadActions(JsonElement element) =>
        [.. element.RequiredArray().Take(AutomationRule.MaximumActions + 1).Select(ReadAction)];

    private static void WriteAction(Utf8JsonWriter writer, AutomationAction action)
    {
        writer.WriteStartObject();
        switch (action)
        {
            case AssignUserAction assign:
                writer.WriteString("a", "assign-user");
                writer.WriteString("user", assign.User.Value);
                break;
            case AssignTeamAction team:
                writer.WriteString("a", "assign-team");
                writer.WriteString("team", team.Team.Value);
                break;
            case AssignToRoutingAction routing:
                writer.WriteString("a", "assign-routing");
                if (routing.Team is { } routingTeam)
                    writer.WriteString("team", routingTeam.Value);
                break;
            case AddLabelsAction add:
                writer.WriteString("a", "add-labels");
                AutomationJson.WriteStrings(writer, "labels", add.Labels.Select(label => label.Value));
                break;
            case RemoveLabelsAction remove:
                writer.WriteString("a", "remove-labels");
                AutomationJson.WriteStrings(writer, "labels", remove.Labels.Select(label => label.Value));
                break;
            case SetPriorityAction priority:
                writer.WriteString("a", "set-priority");
                writer.WriteString("priority", priority.Priority.WireName);
                break;
            case SetStatusAction status:
                writer.WriteString("a", "set-status");
                writer.WriteString("status", status.Status.WireName);
                break;
            case SnoozeAction snooze:
                writer.WriteString("a", "snooze");
                writer.WriteNumber("seconds", (long)snooze.Duration.TotalSeconds);
                break;
            case SetAttributeAction attribute:
                writer.WriteString("a", "set-attribute");
                writer.WriteString("key", attribute.Key);
                if (attribute.Value is { } value)
                {
                    writer.WritePropertyName("value");
                    WriteAttributeValue(writer, value);
                }
                break;
            case SendReplyAction reply:
                writer.WriteString("a", "send-reply");
                writer.WriteString("response", reply.CannedResponse.Value);
                break;
            case AddNoteAction note:
                writer.WriteString("a", "add-note");
                writer.WriteString("template", note.Template.Source);
                break;
            case SendWebhookAction webhook:
                writer.WriteString("a", "webhook");
                writer.WriteString("destination", webhook.Destination.Value);
                break;
            case TriggerSurveyAction survey:
                writer.WriteString("a", "survey");
                if (survey.Survey is { } surveyId)
                    writer.WriteString("survey", surveyId);
                break;
            case ExecuteSlaEscalationAction:
                writer.WriteString("a", "sla-escalation");
                break;
        }
        writer.WriteEndObject();
    }

    private static AutomationAction ReadAction(JsonElement element)
    {
        switch (element.RequiredString("a"))
        {
            case "assign-user":
                AutomationJson.RequireProperties(element, "a", "user");
                return new AssignUserAction(new UserId(element.RequiredString("user")));
            case "assign-team":
                AutomationJson.RequireProperties(element, "a", "team");
                return new AssignTeamAction(new TeamId(element.RequiredString("team")));
            case "assign-routing":
                AutomationJson.RequireProperties(element, "a", "team");
                return new AssignToRoutingAction(element.OptionalString("team") is { } team ? new TeamId(team) : null);
            case "add-labels":
                AutomationJson.RequireProperties(element, "a", "labels");
                return new AddLabelsAction(element.RequiredStrings("labels").Select(label => new LabelId(label)));
            case "remove-labels":
                AutomationJson.RequireProperties(element, "a", "labels");
                return new RemoveLabelsAction(element.RequiredStrings("labels").Select(label => new LabelId(label)));
            case "set-priority":
                AutomationJson.RequireProperties(element, "a", "priority");
                return new SetPriorityAction(AutomationJson.Priority(element.GetProperty("priority")));
            case "set-status":
                AutomationJson.RequireProperties(element, "a", "status");
                return new SetStatusAction(AutomationJson.Status(element.GetProperty("status")));
            case "snooze":
                AutomationJson.RequireProperties(element, "a", "seconds");
                return new SnoozeAction(TimeSpan.FromSeconds(element.RequiredLong("seconds")));
            case "set-attribute":
                AutomationJson.RequireProperties(element, "a", "key", "value");
                return new SetAttributeAction(element.RequiredString("key"),
                    element["value"] is { } value ? ReadAttributeValue(value) : (ConversationAttributeValue?)null);
            case "send-reply":
                AutomationJson.RequireProperties(element, "a", "response");
                return new SendReplyAction(new CannedResponseId(element.RequiredString("response")));
            case "add-note":
                AutomationJson.RequireProperties(element, "a", "template");
                return new AddNoteAction(new AutomationTemplate(element.RequiredString("template")));
            case "webhook":
                AutomationJson.RequireProperties(element, "a", "destination");
                return new SendWebhookAction(new WebhookDestinationId(element.RequiredString("destination")));
            case "survey":
                AutomationJson.RequireProperties(element, "a", "survey");
                return new TriggerSurveyAction(element.OptionalString("survey"));
            case "sla-escalation":
                AutomationJson.RequireProperties(element, "a");
                return ExecuteSlaEscalationAction.Instance;
            default:
                throw new FormatException("Unknown automation action.");
        }
    }

    private static void WriteAttributeValue(Utf8JsonWriter writer, ConversationAttributeValue value)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case ConversationTextValue text:
                writer.WriteString("t", "text");
                writer.WriteString("v", text.Value);
                break;
            case ConversationNumberValue number:
                writer.WriteString("t", "number");
                writer.WriteString("v", number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case ConversationBooleanValue boolean:
                writer.WriteString("t", "boolean");
                writer.WriteBoolean("v", boolean.Value);
                break;
            case ConversationDateValue date:
                writer.WriteString("t", "date");
                writer.WriteString("v", date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case ConversationListValue list:
                writer.WriteString("t", "list");
                AutomationJson.WriteStrings(writer, "v", list.Items);
                break;
        }
        writer.WriteEndObject();
    }

    private static ConversationAttributeValue ReadAttributeValue(JsonElement element)
    {
        AutomationJson.RequireProperties(element, "t", "v");
        var value = element.GetProperty("v");
        return element.RequiredString("t") switch
        {
            "text" => new ConversationTextValue(value.RequiredString()),
            "number" => new ConversationNumberValue(decimal.Parse(value.RequiredString(), NumberStyles.Number, CultureInfo.InvariantCulture)),
            "boolean" => new ConversationBooleanValue(value.RequiredBool()),
            "date" => new ConversationDateValue(DateOnly.ParseExact(value.RequiredString(), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            "list" => new ConversationListValue(AutomationJson.Strings(value)),
            _ => throw new FormatException("Unknown attribute value type."),
        };
    }

    private static void WriteOutcome(Utf8JsonWriter writer, AutomationActionOutcome outcome)
    {
        writer.WriteStartObject();
        switch (outcome)
        {
            case AutomationActionApplied applied:
                writer.WriteString("o", "applied");
                writer.WriteStartArray("seq");
                foreach (var sequence in applied.Sequences)
                    writer.WriteNumberValue(sequence);
                writer.WriteEndArray();
                break;
            case AutomationActionSkipped skipped:
                writer.WriteString("o", "skipped");
                writer.WriteString("reason", skipped.Reason);
                break;
            case AutomationActionRefused refused:
                writer.WriteString("o", "refused");
                writer.WriteString("reason", AutomationJson.Kebab(refused.Reason));
                break;
            case AutomationActionFailed failed:
                writer.WriteString("o", "failed");
                writer.WriteString("code", failed.Code);
                break;
        }
        writer.WriteEndObject();
    }

    private static AutomationActionOutcome ReadOutcome(JsonElement element)
    {
        switch (element.RequiredString("o"))
        {
            case "applied":
                AutomationJson.RequireProperties(element, "o", "seq");
                return new AutomationActionApplied(element.RequiredArray("seq").Select(AutomationJson.Long));
            case "skipped":
                AutomationJson.RequireProperties(element, "o", "reason");
                return new AutomationActionSkipped(element.RequiredString("reason"));
            case "refused":
                AutomationJson.RequireProperties(element, "o", "reason");
                return new AutomationActionRefused(element.RequiredKebab<AutomationActionRefusal>("reason"));
            case "failed":
                AutomationJson.RequireProperties(element, "o", "code");
                return new AutomationActionFailed(element.RequiredString("code"));
            default:
                throw new FormatException("Unknown action outcome.");
        }
    }
}

/// <summary>
/// Encodes the <c>automation.applied</c> timeline marker appended after a rule execution (cause metadata: rule, execution, trigger
/// key, chain depth and per-outcome counts) and <c>automation.macro-applied</c> after a macro run. Identifiers and counts only.
/// </summary>
public static class AutomationEventCodec
{
    public const string AppliedKind = "automation.applied";
    public const string MacroAppliedKind = "automation.macro-applied";
    public const int PayloadVersion = 1;

    public static ExtensionEvent Applied(AutomationExecution execution, IReadOnlyList<AutomationActionOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new(AppliedKind, PayloadVersion, AutomationJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", PayloadVersion);
            writer.WriteString("rule", execution.Rule.Value);
            writer.WriteString("execution", execution.Id.ToString("D"));
            writer.WriteString("trigger", execution.TriggerKey);
            writer.WriteNumber("depth", execution.Depth);
            WriteCounts(writer, outcomes);
            writer.WriteEndObject();
        }));
    }

    public static ExtensionEvent MacroApplied(MacroId macro, Guid run, IReadOnlyList<AutomationActionOutcome> outcomes) =>
        new(MacroAppliedKind, PayloadVersion, AutomationJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", PayloadVersion);
            writer.WriteString("macro", macro.Value);
            writer.WriteString("run", run.ToString("D"));
            WriteCounts(writer, outcomes);
            writer.WriteEndObject();
        }));

    /// <summary>Decodes an <c>automation.applied</c> marker; <see langword="false"/> for other kinds or invalid payloads.</summary>
    public static bool TryDecodeApplied(ExtensionEvent extension, out AutomationRuleId rule, out Guid execution, out int depth)
    {
        ArgumentNullException.ThrowIfNull(extension);
        (rule, execution, depth) = (default, Guid.Empty, 0);
        if (!string.Equals(extension.Kind, AppliedKind, StringComparison.Ordinal) || extension.PayloadVersion != PayloadVersion)
            return false;
        try
        {
            (rule, execution, depth) = AutomationJson.Read(extension.Payload, root =>
            {
                AutomationJson.RequireProperties(root, "v", "rule", "execution", "trigger", "depth", "applied", "skipped", "refused", "failed");
                AutomationJson.RequireVersion(root, PayloadVersion);
                return (new AutomationRuleId(root.RequiredString("rule")), Guid.ParseExact(root.RequiredString("execution"), "D"),
                    root.RequiredInt("depth"));
            });
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void WriteCounts(Utf8JsonWriter writer, IReadOnlyList<AutomationActionOutcome> outcomes)
    {
        int applied = 0, skipped = 0, refused = 0, failed = 0;
        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case AutomationActionApplied:
                    applied++;
                    break;
                case AutomationActionSkipped:
                    skipped++;
                    break;
                case AutomationActionRefused:
                    refused++;
                    break;
                case AutomationActionFailed:
                    failed++;
                    break;
            }
        }
        writer.WriteNumber("applied", applied);
        writer.WriteNumber("skipped", skipped);
        writer.WriteNumber("refused", refused);
        writer.WriteNumber("failed", failed);
    }
}
