using System.Diagnostics;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Automation.Tests;

[TestClass]
public sealed class AutomationModelTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    internal static AutomationRule EveryFeatureRule() => new(new AutomationRuleId("everything"), "Everything",
        new SlaEventTrigger([SlaEventKind.Breached, SlaEventKind.Warning], [SlaMetric.FirstResponse]),
        new AllOfCondition(
            new AnyOfCondition(new StatusIsCondition(ConversationStatusKind.Open, ConversationStatusKind.Pending), new PriorityCondition(PriorityComparison.AtLeast, ConversationPriority.High)),
            new NegatedCondition(new InboxIsCondition(new InboxId("spam"))),
            new TeamIsCondition([new TeamId("billing")], orNone: true),
            new AssigneeIsCondition([], orUnassigned: true),
            new HasLabelsCondition(ConditionTarget.Contact, LabelMatch.None, new LabelId("blocked")),
            new ChannelKindIsCondition(new ChannelKind("email")),
            new AttributeCondition(ConditionTarget.Conversation, "plan", AttributeOperator.In, "gold", "platinum"),
            new AttributeCondition(ConditionTarget.Contact, "score", AttributeOperator.GreaterThan, "10.5"),
            new MessageContentCondition(MessageTextOperator.Matches, "^refund", ignoreCase: false, TimeSpan.FromMilliseconds(250)),
            new BusinessHoursCondition(Open: false),
            new ElapsedCondition(ConversationTimestamp.WaitingSince, ElapsedComparison.AtLeast, TimeSpan.FromHours(2))),
        [
            new AssignUserAction(new UserId("lead")), new AssignTeamAction(new TeamId("billing")), new AssignToRoutingAction(new TeamId("tier2")), new AssignToRoutingAction(),
            new AddLabelsAction(new LabelId("b"), new LabelId("a")), new RemoveLabelsAction(new LabelId("c")), new SetPriorityAction(ConversationPriority.Urgent),
            new SetStatusAction(ConversationStatusKind.Pending), new SnoozeAction(TimeSpan.FromHours(4)),
            new SetAttributeAction("plan", new ConversationTextValue("gold")), new SetAttributeAction("seats", new ConversationNumberValue(12.5m)),
            new SetAttributeAction("vip", new ConversationBooleanValue(true)), new SetAttributeAction("renewal", new ConversationDateValue(new DateOnly(2027, 1, 31))),
            new SetAttributeAction("tags", new ConversationListValue(["x", "y"])), new SetAttributeAction("old", null), ExecuteSlaEscalationAction.Instance,
        ], priority: 7, enabled: false, stopProcessing: true, runOnAutomationEvents: true);

    [TestMethod]
    public void RuleCodecRoundTripsEveryShape()
    {
        var rule = EveryFeatureRule();
        var json = AutomationCodec.SerializeRule(rule);
        Assert.AreEqual(rule, AutomationCodec.DeserializeRule(rule.Id, json));

        var other = new AutomationRule(new AutomationRuleId("other"), "Other", new ScheduledTrigger(new AutomationSchedule("*/15 9-17 * * 1-5", "America/New_York"),
            [ConversationStatusKind.Pending], new InboxId("email"), 250), null,
            [new SendReplyAction(new CannedResponseId("thanks")), new AddNoteAction(new AutomationTemplate("Auto: {{conversation.id}}")),
             new SendWebhookAction(new WebhookDestinationId("crm")), new TriggerSurveyAction("csat"), new TriggerSurveyAction()]);
        Assert.AreEqual(other, AutomationCodec.DeserializeRule(other.Id, AutomationCodec.SerializeRule(other)));
        foreach (AutomationTrigger trigger in new AutomationTrigger[]
        {
            ConversationCreatedTrigger.Instance, MessageReceivedTrigger.Instance, MessageSentTrigger.Instance, new ConversationUpdatedTrigger(ConversationUpdateField.Labels),
            new StatusChangedTrigger(), AssignmentChangedTrigger.Instance, ContactUpdatedTrigger.Instance,
        })
        {
            var simple = new AutomationRule(new AutomationRuleId("t"), "T", trigger, null, [new AddLabelsAction(new LabelId("x"))]);
            Assert.AreEqual(simple, AutomationCodec.DeserializeRule(simple.Id, AutomationCodec.SerializeRule(simple)));
        }
    }

    [TestMethod]
    public void CodecsRejectUnknownVersionsKindsAndProperties()
    {
        var json = AutomationCodec.SerializeRule(EveryFeatureRule());
        Assert.ThrowsExactly<FormatException>(() => AutomationCodec.DeserializeRule(new AutomationRuleId("x"), json.Replace("\"v\":1", "\"v\":2")));
        Assert.ThrowsExactly<FormatException>(() => AutomationCodec.DeserializeRule(new AutomationRuleId("x"), json.Replace("\"name\":", "\"extra\":1,\"name\":")));
        Assert.ThrowsExactly<FormatException>(() => AutomationCodec.DeserializeRule(new AutomationRuleId("x"), json.Replace("\"a\":\"assign-user\"", "\"a\":\"run-script\"")));
        Assert.ThrowsExactly<FormatException>(() => AutomationCodec.DeserializeRule(new AutomationRuleId("x"), json.Replace("\"op\":\"business-hours\"", "\"op\":\"eval\"")));
        Assert.ThrowsExactly<FormatException>(() => AutomationCodec.DeserializeRule(new AutomationRuleId("x"), json.Replace("\"t\":\"sla-event\"", "\"t\":\"cron\"")));
        Assert.ThrowsExactly<FormatException>(() => AutomationCodec.DeserializeRule(new AutomationRuleId("x"), "[]"));

        var macro = new Macro(new MacroId("m"), "M", MacroVisibility.Personal, new UserId("agent"), [new SetStatusAction(ConversationStatusKind.Resolved)], "desc");
        Assert.AreEqual(macro, AutomationCodec.DeserializeMacro(macro.Id, AutomationCodec.SerializeMacro(macro)));
        var response = StoreContracts.AutomationStoreContract.Response("hi", "hello");
        Assert.AreEqual(response, AutomationCodec.DeserializeCannedResponse(response.Id, AutomationCodec.SerializeCannedResponse(response)));
        var execution = StoreContracts.AutomationStoreContract.Execution()
            .WithProgress([new AutomationActionApplied([3, 4]), new AutomationActionFailed("http-500")]).Complete(TestClock.DefaultStart.AddSeconds(2), 9);
        Assert.AreEqual(execution, AutomationCodec.DeserializeExecution(AutomationCodec.SerializeExecution(execution)));
        var suppressed = new AutomationExecution(Guid.NewGuid(), new AutomationRuleId("r"), null, "sched:1", AutomationEventKind.Scheduled, 4, AutomationExecutionStatus.Suppressed,
            AutomationSuppression.DepthExceeded, TestClock.DefaultStart, TestClock.DefaultStart, 1, []);
        Assert.AreEqual(suppressed, AutomationCodec.DeserializeExecution(AutomationCodec.SerializeExecution(suppressed)));
        Assert.ThrowsExactly<FormatException>(() => AutomationCodec.DeserializeExecution(AutomationCodec.SerializeExecution(execution).Replace("\"o\":\"failed\"", "\"o\":\"exploded\"")));
    }

    [TestMethod]
    public void ModelIsBounded()
    {
        AutomationCondition deep = new StatusIsCondition(ConversationStatusKind.Open);
        for (var level = 1; level < AutomationCondition.MaximumDepth; level++)
            deep = new NegatedCondition(deep);
        Assert.ThrowsExactly<ArgumentException>(() => new NegatedCondition(deep));
        Assert.ThrowsExactly<ArgumentException>(() => new AllOfCondition(Enumerable.Range(0, 64).Select(_ => (AutomationCondition)new BusinessHoursCondition(true))));
        Assert.ThrowsExactly<ArgumentException>(() => new MessageContentCondition(MessageTextOperator.Matches, "(unclosed"));
        Assert.ThrowsExactly<ArgumentException>(() => new MessageContentCondition(MessageTextOperator.Contains, new string('a', 513)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MessageContentCondition(MessageTextOperator.Matches, "a", timeout: TimeSpan.FromSeconds(2)));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationRule(new AutomationRuleId("r"), "R", MessageReceivedTrigger.Instance, null,
            Enumerable.Range(0, 17).Select(index => (AutomationAction)new AddLabelsAction(new LabelId($"l{index}")))));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationRule(new AutomationRuleId("r"), "R", MessageReceivedTrigger.Instance, null, [ExecuteSlaEscalationAction.Instance]));
        Assert.ThrowsExactly<ArgumentException>(() => new Macro(new MacroId("m"), "M", MacroVisibility.Shared, null, [new SendWebhookAction(new WebhookDestinationId("x"))]));
        Assert.ThrowsExactly<ArgumentException>(() => new Macro(new MacroId("m"), "M", MacroVisibility.Shared, new UserId("agent"), [new SetPriorityAction(ConversationPriority.Low)]));
        Assert.ThrowsExactly<ArgumentException>(() => new SetStatusAction(ConversationStatusKind.Snoozed));
        Assert.ThrowsExactly<ArgumentException>(() => new CannedResponse(new CannedResponseId("c"), "x", "X", "fr", [new CannedResponseVariant("en", new AutomationTemplate("a"))]));
        Assert.ThrowsExactly<ArgumentException>(() => new CannedResponse(new CannedResponseId("c"), "Bad Shortcut", "X", "en", [new CannedResponseVariant("en", new AutomationTemplate("a"))]));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationRule(new AutomationRuleId("r"), "bad\u0001name", MessageReceivedTrigger.Instance, null, [new SetPriorityAction(ConversationPriority.Low)]));
    }

    [TestMethod]
    public void ConditionsAreThreeValuedAndMaskedValuesAreUnknown()
    {
        var context = Context(attributes: new() { ["plan"] = new(AutomationAttributeKind.Text, "gold", null), ["seats"] = new(AutomationAttributeKind.Number, "12", null) },
            masked: ["ssn"], message: "Please REFUND me");
        Assert.IsTrue(Evaluate(new AttributeCondition(ConditionTarget.Conversation, "plan", AttributeOperator.Equal, "gold"), context));
        Assert.IsTrue(Evaluate(new AttributeCondition(ConditionTarget.Conversation, "seats", AttributeOperator.GreaterThan, "9.5"), context));
        Assert.IsFalse(Evaluate(new AttributeCondition(ConditionTarget.Conversation, "missing", AttributeOperator.Exists), context));
        Assert.IsNull(Evaluate(new AttributeCondition(ConditionTarget.Conversation, "missing", AttributeOperator.Equal, "x"), context));
        Assert.IsNull(Evaluate(new AttributeCondition(ConditionTarget.Conversation, "ssn", AttributeOperator.Exists), context), "Masked attributes are unknown.");
        Assert.IsNull(Evaluate(new NegatedCondition(new AttributeCondition(ConditionTarget.Conversation, "ssn", AttributeOperator.Equal, "1")), context));
        Assert.IsNull(Evaluate(new AttributeCondition(ConditionTarget.Contact, "score", AttributeOperator.Exists), context), "No readable contact.");
        var unknown = new AttributeCondition(ConditionTarget.Conversation, "ssn", AttributeOperator.Exists);
        Assert.IsTrue(Evaluate(new AnyOfCondition(unknown, new StatusIsCondition(ConversationStatusKind.Open)), context));
        Assert.IsNull(Evaluate(new AllOfCondition(unknown, new StatusIsCondition(ConversationStatusKind.Open)), context));
        Assert.IsFalse(Evaluate(new AllOfCondition(unknown, new StatusIsCondition(ConversationStatusKind.Closed)), context));
        Assert.IsFalse(AutomationConditionEvaluator.Matches(unknown, context), "Rules fire only on true.");
        Assert.IsTrue(Evaluate(new MessageContentCondition(MessageTextOperator.Contains, "refund"), context));
        Assert.IsFalse(Evaluate(new MessageContentCondition(MessageTextOperator.Contains, "refund", ignoreCase: false), context));
        Assert.IsTrue(Evaluate(new MessageContentCondition(MessageTextOperator.Matches, @"\brefund\b"), context));
        Assert.IsNull(Evaluate(new MessageContentCondition(MessageTextOperator.Contains, "x"), Context()), "No message.");
        Assert.IsTrue(Evaluate(new PriorityCondition(PriorityComparison.AtMost, ConversationPriority.Low), context));
        Assert.IsTrue(Evaluate(new AssigneeIsCondition([], orUnassigned: true), context));
        Assert.IsFalse(Evaluate(new TeamIsCondition([new TeamId("billing")]), context));
        Assert.IsTrue(Evaluate(new ElapsedCondition(ConversationTimestamp.Created, ElapsedComparison.AtLeast, TimeSpan.FromHours(1)), context));
        Assert.IsNull(Evaluate(new ElapsedCondition(ConversationTimestamp.WaitingSince, ElapsedComparison.AtLeast, TimeSpan.FromHours(1)), context));
        Assert.IsNull(Evaluate(new BusinessHoursCondition(true), context), "No calendar.");
        Assert.IsTrue(Evaluate(new ChannelKindIsCondition(new ChannelKind("email")), context));
    }

    [TestMethod]
    public void RegexTimeoutFailsClosed()
    {
        var catastrophic = new MessageContentCondition(MessageTextOperator.Matches, "^(a+)+$", timeout: TimeSpan.FromMilliseconds(50));
        var context = Context(message: new string('a', 40) + "!");
        var watch = Stopwatch.StartNew();
        Assert.IsNull(Evaluate(catastrophic, context), "A timed-out match is unknown.");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.IsFalse(AutomationConditionEvaluator.Matches(new NegatedCondition(catastrophic), context), "Negating a timeout never fires either.");

        // Input beyond the bound is not examined.
        var tail = Context(message: new string('x', MessageContentCondition.MaximumInputLength) + "needle");
        Assert.IsFalse(Evaluate(new MessageContentCondition(MessageTextOperator.Contains, "needle"), tail));
    }

    [TestMethod]
    public void BusinessHoursUseTheInboxCalendar()
    {
        var calendar = new BusinessCalendarCalculator(new BusinessCalendar(new BusinessCalendarId("ny"), "NY", "America/New_York",
            [new WeeklyBusinessHours(DayOfWeek.Monday, BusinessHours.From(9, 0, 17, 0))]), NewYork);
        var open = Context(now: new DateTimeOffset(2026, 1, 5, 15, 0, 0, TimeSpan.Zero), calendar: calendar);
        var closed = Context(now: new DateTimeOffset(2026, 1, 5, 23, 0, 0, TimeSpan.Zero), calendar: calendar);
        Assert.IsTrue(Evaluate(new BusinessHoursCondition(true), open));
        Assert.IsFalse(Evaluate(new BusinessHoursCondition(true), closed));
        Assert.IsTrue(Evaluate(new BusinessHoursCondition(false), closed));
    }

    [TestMethod]
    public void TemplatesAreSafeAndBounded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationTemplate("Hi {{contact.email}}"));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationTemplate("Hi {{system.exec('rm')}}"));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationTemplate("Hi {{contact.name"));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationTemplate("{{ {{contact.name}} }}"));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationTemplate(string.Concat(Enumerable.Repeat("{{agent.name}}", 65))));
        var template = new AutomationTemplate("Hi {{ contact.name | there }}, re {{conversation.id}} ({{conversation.attributes.ssn|redacted}}){{agent.name}}.");
        CollectionAssert.AreEqual(new[] { "contact.name", "conversation.id", "conversation.attributes.ssn", "agent.name" }, template.Variables.ToArray());
        Assert.AreEqual("Hi Ada, re c1 (redacted).", template.Render(new Dictionary<string, string?> { ["contact.name"] = "Ada", ["conversation.id"] = "c1" }));
        Assert.AreEqual("Hi there, re c1 (redacted).", template.Render(new Dictionary<string, string?> { ["contact.name"] = null, ["conversation.id"] = "c1" }));
        Assert.AreEqual(@"Hi \[x\]\(http://evil\), re c\_1 (redacted).",
            template.Render(new Dictionary<string, string?> { ["contact.name"] = "[x](http://evil)", ["conversation.id"] = "c_1" }, TemplateEscaping.Markdown));
        Assert.AreEqual("Hi &lt;b&gt;, re 1 (redacted).", template.Render(new Dictionary<string, string?> { ["contact.name"] = "<b>", ["conversation.id"] = "1" }, TemplateEscaping.Html));
        Assert.AreEqual("Hi ab, re 1 (redacted).", template.Render(new Dictionary<string, string?> { ["contact.name"] = "a\u0000b", ["conversation.id"] = "1" }), "Control characters are stripped.");
        var huge = new AutomationTemplate(string.Concat(Enumerable.Repeat("{{contact.name}}", 20)));
        Assert.IsNull(huge.Render(new Dictionary<string, string?> { ["contact.name"] = new string('x', 10_000) }), "Output is capped at 64 KiB.");
        Assert.AreEqual(4096 * 2, new AutomationTemplate("{{contact.name}}{{contact.name}}").Render(new Dictionary<string, string?> { ["contact.name"] = new string('x', 10_000) })!.Length);
    }

    [TestMethod]
    public void SchedulesParseAndAreBounded()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationSchedule("* * * *", "UTC"));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationSchedule("*/5 * * * *", "UTC"), "More than 96 fires per day.");
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationSchedule("60 * * * *", "UTC"));
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationSchedule("0 5-3 * * *", "UTC"));
        Assert.IsNull(new AutomationSchedule("0 0 31 2 *", "UTC").NextAfter(DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc), "February 31 never fires.");
        var weekdays = new AutomationSchedule("*/15 9-17 * * 1-5", "UTC");
        var friday = new DateTimeOffset(2026, 1, 9, 17, 50, 0, TimeSpan.Zero);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero), weekdays.NextAfter(friday, TimeZoneInfo.Utc));
        Assert.AreEqual(new DateTimeOffset(2026, 1, 9, 17, 15, 0, TimeSpan.Zero), weekdays.NextAfter(friday.AddMinutes(-45), TimeZoneInfo.Utc));
        // Day-of-month and day-of-week restricted together match either (classic cron).
        var either = new AutomationSchedule("0 12 1 * 0", "UTC");
        Assert.AreEqual(new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero), either.NextAfter(new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.AreEqual(new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero), either.NextAfter(new DateTimeOffset(2026, 1, 25, 13, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.AreEqual(new DateTimeOffset(2028, 2, 29, 0, 0, 0, TimeSpan.Zero), new AutomationSchedule("0 0 29 2 *", "UTC").NextAfter(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [TestMethod]
    public void SchedulesAreDstCorrect()
    {
        // Daily 09:00 New York: 14:00Z in winter, 13:00Z after the March 8 2026 spring-forward.
        var nine = new AutomationSchedule("0 9 * * *", "America/New_York");
        Assert.AreEqual(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero), nine.NextAfter(new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero), NewYork));
        Assert.AreEqual(new DateTimeOffset(2026, 3, 8, 13, 0, 0, TimeSpan.Zero), nine.NextAfter(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero), NewYork));

        // 02:30 does not exist on March 8: it fires once at the transition (03:00 EDT = 07:00Z), then normally.
        var gap = new AutomationSchedule("30 2 * * *", "America/New_York");
        var transition = gap.NextAfter(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), NewYork);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), transition);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 9, 6, 30, 0, TimeSpan.Zero), gap.NextAfter(transition!.Value, NewYork));

        // 01:30 occurs twice on November 1: only the first (EDT, 05:30Z) fires; the next fire is the following day (EST, 06:30Z).
        var overlap = new AutomationSchedule("30 1 * * *", "America/New_York");
        var first = overlap.NextAfter(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), NewYork);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), first);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), overlap.NextAfter(first!.Value, NewYork));
        Assert.AreEqual(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), overlap.NextAfter(new DateTimeOffset(2026, 11, 1, 6, 10, 0, TimeSpan.Zero), NewYork),
            "Inside the repeated hour the first occurrence has passed.");

        // Every 15 minutes through the fall-back night never fires twice for one local time and stays strictly increasing.
        var quarter = new AutomationSchedule("*/15 0-3 * * *", "America/New_York");
        var fires = new List<DateTimeOffset>();
        var cursor = new DateTimeOffset(2026, 11, 1, 3, 59, 0, TimeSpan.Zero);
        for (var index = 0; index < 16; index++)
            fires.Add(cursor = quarter.NextAfter(cursor, NewYork)!.Value);
        Assert.IsTrue(fires.Zip(fires.Skip(1)).All(pair => pair.First < pair.Second));
        Assert.AreEqual(16, fires.Select(fire => TimeZoneInfo.ConvertTime(fire, NewYork).DateTime).Distinct().Count());
    }

    private static bool? Evaluate(AutomationCondition condition, AutomationContext context) => AutomationConditionEvaluator.Evaluate(condition, context);

    internal static AutomationContext Context(Dictionary<string, AutomationAttributeFact>? attributes = null, string[]? masked = null, string? message = null,
        DateTimeOffset? now = null, BusinessCalendarCalculator? calendar = null)
    {
        var created = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var conversation = new AutomationConversationFacts(new ConversationId("c1"), 1, new InboxId("email"), new ContactId("ada"), ConversationStatus.Open,
            ConversationPriority.Low, null, null, [new LabelId("vip")], attributes ?? new Dictionary<string, AutomationAttributeFact>(), new HashSet<string>(masked ?? [], StringComparer.Ordinal), created, created, null, created, null);
        return new AutomationContext(conversation, null, message is null ? null : new AutomationMessageFacts(MessageDirection.Incoming, message),
            AutomationEvent.MessageReceived(1), now ?? created.AddHours(3), new ChannelKind("email"), calendar);
    }
}
