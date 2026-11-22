using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;

namespace TicketFlow.Automation.Tests;

[TestClass]
public sealed class AutomationEngineTests
{
    private static readonly ConversationId C1 = new("c1");

    private static AutomationRule RefundRule(int priority = 100, bool stop = false) => new(new AutomationRuleId("refunds"), "Refunds", MessageReceivedTrigger.Instance,
        new MessageContentCondition(MessageTextOperator.Matches, @"\brefund\b"), [new AddLabelsAction(new LabelId("billing")), new SetPriorityAction(ConversationPriority.High)],
        priority, stopProcessing: stop);

    [TestMethod]
    public async Task MatchingRuleRunsActionsAsTheAutomationClientAndLogsOutcomes()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(RefundRule());
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("never"), "Never", MessageReceivedTrigger.Instance,
            new MessageContentCondition(MessageTextOperator.Contains, "invoice"), [new AddLabelsAction(new LabelId("invoice"))]));
        await fixture.StartAsync("c1", "I want a refund please");
        var dispatch = await fixture.DispatchAsync();
        Assert.AreEqual(0, dispatch.DeadLettered);

        var conversation = await fixture.ConversationAsync("c1");
        CollectionAssert.AreEqual(new[] { new LabelId("billing") }, conversation.Labels.ToArray());
        Assert.AreEqual(ConversationPriority.High, conversation.Priority);
        var timeline = await fixture.TimelineAsync("c1");
        var automated = timeline.Where(item => item.Actor.Kind == AccessActorKind.ApiClient && item.Actor.Id == "automation-worker").ToList();
        Assert.AreEqual(3, automated.Count, "Labels, priority and the marker.");
        var marker = automated.Single(item => item.Event.Value is ExtensionEvent);
        Assert.IsTrue(AutomationEventCodec.TryDecodeApplied((ExtensionEvent)marker.Event.Value!, out var rule, out var executionId, out var depth));
        Assert.AreEqual("refunds", rule.Value);
        Assert.AreEqual(1, depth);

        var execution = (await fixture.ExecutionsAsync("c1")).Single().Execution;
        Assert.AreEqual(executionId, execution.Id);
        Assert.AreEqual(AutomationExecutionStatus.Completed, execution.Status);
        Assert.AreEqual(AutomationTriggerKeys.TimelineEvent(1), execution.TriggerKey);
        Assert.IsTrue(execution.Outcomes.All(outcome => outcome.Value is AutomationActionApplied));
        Assert.AreEqual(marker.Sequence, execution.Marker);
        foreach (var produced in automated)
            Assert.AreEqual(execution.Id, (await fixture.Get<IAutomationExecutionStore>().FindCauseAsync(fixture.Scope, C1, produced.Sequence))!.Execution);

        var log = Assert.IsInstanceOfType<AutomationValue<AutomationExecutionPage>>((await fixture.RulesAsync(service =>
            service.ListExecutionsAsync(fixture.Owner, new AutomationExecutionQuery(rule: new AutomationRuleId("refunds"))))).Value).Value;
        Assert.AreEqual(1, log.Executions.Count);
        Assert.IsInstanceOfType<AutomationRefused>((await fixture.RulesAsync(service => service.ListExecutionsAsync(fixture.Agent, new AutomationExecutionQuery()))).Value);
    }

    [TestMethod]
    public async Task ActionsNeverExceedTheAutomationClientsGrants()
    {
        using var fixture = await AutomationFixture.CreateAsync(workerPermissions: [ConversationPermissions.Read, ConversationPermissions.Manage]);
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("assign"), "Assign", ConversationCreatedTrigger.Instance, null,
            [new AssignUserAction(new UserId("agent")), new AddLabelsAction(new LabelId("triaged")), new SetStatusAction(ConversationStatusKind.Resolved)]));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        var conversation = await fixture.ConversationAsync("c1");
        Assert.IsNull(conversation.Assignee);
        Assert.AreEqual(ConversationStatusKind.Open, conversation.Status.Kind);
        CollectionAssert.AreEqual(new[] { new LabelId("triaged") }, conversation.Labels.ToArray());
        var outcomes = (await fixture.ExecutionsAsync("c1")).Single().Execution.Outcomes;
        Assert.AreEqual(AutomationActionRefusal.Forbidden, ((AutomationActionRefused)outcomes[0].Value!).Reason);
        Assert.IsInstanceOfType<AutomationActionApplied>(outcomes[1].Value);
        Assert.AreEqual(AutomationActionRefusal.Forbidden, ((AutomationActionRefused)outcomes[2].Value!).Reason);
    }

    [TestMethod]
    public async Task AutomationClientWithoutReadAccessRunsNothing()
    {
        using var fixture = await AutomationFixture.CreateAsync(workerPermissions: [ConversationPermissions.Manage]);
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("label"), "Label", ConversationCreatedTrigger.Instance, null, [new AddLabelsAction(new LabelId("x"))]));
        await fixture.StartAsync("c1");
        Assert.AreEqual(0, (await fixture.DispatchAsync()).DeadLettered);
        Assert.AreEqual(0, (await fixture.ConversationAsync("c1")).Labels.Count);
        Assert.AreEqual(0, (await fixture.ExecutionsAsync()).Count);
    }

    [TestMethod]
    public async Task RedeliveryAndResumeNeverDuplicateActions()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.InScopeAsync(services => services.GetRequiredService<ICannedResponseService>().CreateAsync(fixture.Owner,
            new CannedResponse(new CannedResponseId("ack"), "ack", "Ack", "en", [new CannedResponseVariant("en", new AutomationTemplate("We got it, {{contact.name|friend}}."))])));
        var rule = new AutomationRule(new AutomationRuleId("ack"), "Ack", MessageReceivedTrigger.Instance, null,
            [new SendReplyAction(new CannedResponseId("ack")), new AddNoteAction(new AutomationTemplate("Acknowledged automatically."))]);
        await fixture.CreateRuleAsync(rule);
        await fixture.StartAsync("c1", "hi");
        await fixture.DispatchAsync();
        var before = await fixture.TimelineAsync("c1");
        Assert.AreEqual(1, before.Count(item => item.Event.Value is MessageEvent { Direction: MessageDirection.Outgoing }));

        // Redeliver the Started change: the completed execution record short-circuits.
        var started = new ConversationOutboxEntry(1, new ConversationChange(Guid.NewGuid(), fixture.Scope, C1, new InboxId("email"), 1, ConversationChangeKind.Started,
            [ConversationEventSummary.From(before[0])], null, null, fixture.Clock.GetUtcNow()));
        await fixture.InScopeAsync(async services =>
        {
            await services.GetRequiredService<AutomationOutboxHandler>().HandleAsync(started);
            await services.GetRequiredService<AutomationOutboxHandler>().HandleAsync(started);
            return 0;
        });
        Assert.AreEqual(before.Count, (await fixture.TimelineAsync("c1")).Count);

        // A crash after the reply was sent but before its outcome was recorded: the resumed execution finds the deterministic message id.
        var store = fixture.Get<IAutomationExecutionStore>();
        var crashed = new AutomationExecution(Guid.NewGuid(), rule.Id, C1, "evt:99", AutomationEventKind.MessageReceived, 1, AutomationExecutionStatus.Running, null,
            fixture.Clock.GetUtcNow(), null, rule.Actions.Count, []);
        await store.BeginAsync(fixture.Scope, crashed);
        var actor = new ApiClientPrincipal(fixture.Scope, new ApiClientId("automation-worker"));
        await fixture.InScopeAsync(async services =>
        {
            var builder = services.GetRequiredService<AutomationContextBuilder>();
            var (context, _) = await builder.BuildAsync(actor, C1, AutomationEvent.MessageReceived(99), null);
            await services.GetRequiredService<AutomationActionExecutor>().ExecuteAsync(actor, new AutomationActionRequest(rule.Actions[0], context!, $"{crashed.Id:N}-0", null, rule.Id));
            return 0;
        });
        var afterCrash = (await fixture.TimelineAsync("c1")).Count;
        await fixture.InScopeAsync(services => services.GetRequiredService<AutomationRunner>().RunAsync(
            new AutomationOccurrence(fixture.Scope, C1, AutomationEvent.MessageReceived(99), "evt:99", false, null, null), [rule]));
        var resumed = (await store.GetAsync(fixture.Scope, crashed.Id))!.Execution;
        Assert.AreEqual(AutomationExecutionStatus.Completed, resumed.Status);
        var timeline = await fixture.TimelineAsync("c1");
        Assert.AreEqual(2, timeline.Count(item => item.Event.Value is MessageEvent { Direction: MessageDirection.Outgoing }), "One reply per execution, never two.");
        Assert.AreEqual(afterCrash + 2, timeline.Count, "Only the note and the marker were added on resume.");
        Assert.AreEqual(timeline.Single(item => item.Event.Value is MessageEvent { Direction: MessageDirection.Outgoing } && item.Sequence > before.Count).Sequence,
            ((AutomationActionApplied)resumed.Outcomes[0].Value!).Sequences.Single());
    }

    [TestMethod]
    public async Task AutomationCausedEventsDoNotRetriggerRulesUnlessOptedIn()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("echo"), "Echo", new ConversationUpdatedTrigger(ConversationUpdateField.Labels), null,
            [new AddLabelsAction(new LabelId("echoed"))]));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        await fixture.CommandAsync(service => service.UpdateLabelsAsync(fixture.Agent, C1, [new LabelId("human")], []));
        await fixture.DispatchAsync();
        Assert.AreEqual(1, (await fixture.ExecutionsAsync("c1")).Count, "The rule's own label change did not re-trigger it.");
        CollectionAssert.AreEquivalent(new[] { new LabelId("echoed"), new LabelId("human") }, (await fixture.ConversationAsync("c1")).Labels.ToArray());
    }

    [TestMethod]
    public async Task ChainsStopAtTheMaximumDepth()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        // Two rules that flip the priority back and forth forever if unchecked.
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("down"), "Down", new ConversationUpdatedTrigger(ConversationUpdateField.Priority),
            new PriorityCondition(PriorityComparison.Equal, ConversationPriority.High), [new SetPriorityAction(ConversationPriority.Low)], runOnAutomationEvents: true));
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("up"), "Up", new ConversationUpdatedTrigger(ConversationUpdateField.Priority),
            new PriorityCondition(PriorityComparison.Equal, ConversationPriority.Low), [new SetPriorityAction(ConversationPriority.High)], runOnAutomationEvents: true));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        await fixture.CommandAsync(service => service.SetPriorityAsync(fixture.Agent, C1, ConversationPriority.High));
        var dispatch = await fixture.DispatchAsync();
        Assert.AreEqual(0, dispatch.DeadLettered);

        var executions = (await fixture.ExecutionsAsync("c1")).Select(item => item.Execution).ToList();
        Assert.IsTrue(executions.Where(item => item.Status == AutomationExecutionStatus.Completed).All(item => item.Depth <= 3));
        Assert.IsTrue(executions.Any(item => item.Status == AutomationExecutionStatus.Completed && item.Depth == 3), "The chain ran to the maximum depth.");
        var suppressed = executions.Where(item => item.Status == AutomationExecutionStatus.Suppressed).ToList();
        Assert.IsTrue(suppressed.Count >= 1);
        Assert.IsTrue(suppressed.All(item => item.Suppression == AutomationSuppression.DepthExceeded && item.Depth == 4), "Nothing ran beyond depth 3.");
        Assert.IsTrue(executions.Count <= 50, "The per-conversation rate limit also bounds fan-out.");
    }

    [TestMethod]
    public async Task AutomationEventsOfUnknownCauseAreTreatedAsMaximumDepth()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("opt-in"), "Opt in", new ConversationUpdatedTrigger(ConversationUpdateField.Labels), null,
            [new SetPriorityAction(ConversationPriority.Urgent)], runOnAutomationEvents: true));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        // The automation client acts outside any recorded execution (for example a crash before its outcome was saved).
        await fixture.CommandAsync(service => service.UpdateLabelsAsync(fixture.Worker, C1, [new LabelId("x")], []));
        await fixture.DispatchAsync();
        var execution = (await fixture.ExecutionsAsync("c1")).Single().Execution;
        Assert.AreEqual(AutomationSuppression.DepthExceeded, execution.Suppression);
        Assert.AreEqual(ConversationPriority.None, (await fixture.ConversationAsync("c1")).Priority);
    }

    [TestMethod]
    public async Task PerConversationRateLimitSuppressesExecutions()
    {
        using var fixture = await AutomationFixture.CreateAsync(options: options => options.RateLimit = 2);
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("note"), "Note", MessageReceivedTrigger.Instance, null,
            [new AddNoteAction(new AutomationTemplate("Seen."))]));
        await fixture.StartAsync("c1", "one");
        for (var index = 0; index < 3; index++)
            await fixture.CommandAsync(service => service.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft($"more {index}", ExternalId: $"ext-{index}")));
        await fixture.DispatchAsync();
        var executions = (await fixture.ExecutionsAsync("c1")).Select(item => item.Execution).ToList();
        Assert.AreEqual(2, executions.Count(item => item.Status == AutomationExecutionStatus.Completed));
        Assert.AreEqual(2, executions.Count(item => item.Suppression == AutomationSuppression.RateLimited));
    }

    [TestMethod]
    public async Task RulesRunInPriorityOrderAndStopProcessingEndsEvaluation()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(RefundRule(priority: 1, stop: true));
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("later"), "Later", MessageReceivedTrigger.Instance, null, [new AddLabelsAction(new LabelId("later"))], 50));
        await fixture.StartAsync("c1", "refund");
        await fixture.StartAsync("c2", "hello");
        await fixture.DispatchAsync();
        CollectionAssert.AreEqual(new[] { new LabelId("billing") }, (await fixture.ConversationAsync("c1")).Labels.ToArray());
        CollectionAssert.AreEqual(new[] { new LabelId("later") }, (await fixture.ConversationAsync("c2")).Labels.ToArray());
    }

    [TestMethod]
    public async Task DryRunPreviewsWithoutSideEffects()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.InScopeAsync(services => services.GetRequiredService<ICannedResponseService>().CreateAsync(fixture.Owner,
            new CannedResponse(new CannedResponseId("ack"), "ack", "Ack", "en",
                [new CannedResponseVariant("en", new AutomationTemplate("Hi {{contact.name}}")), new CannedResponseVariant("de", new AutomationTemplate("Hallo {{contact.name}}"))])));
        await fixture.StartAsync("c1", "hello");
        await fixture.DispatchAsync();
        await fixture.CreateRuleAsync(RefundRule(priority: 1));
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("reply"), "Reply", MessageReceivedTrigger.Instance, null, [new SendReplyAction(new CannedResponseId("ack"))], 5));
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("created"), "Created", ConversationCreatedTrigger.Instance, null, [new AddLabelsAction(new LabelId("new"))], 9));
        var before = await fixture.Get<IConversationStore>().GetAsync(fixture.Scope, C1);

        var result = Assert.IsInstanceOfType<AutomationValue<AutomationDryRunResult>>((await fixture.RulesAsync(service => service.DryRunAsync(fixture.Owner,
            new AutomationDryRunRequest(C1, AutomationEvent.MessageReceived(), MessageBody: "Need a refund")))).Value).Value;
        Assert.AreEqual(AutomationMatch.Matched, result.Rules.Single(rule => rule.Rule.Value == "refunds").Match);
        var reply = result.Rules.Single(rule => rule.Rule.Value == "reply");
        Assert.AreEqual("Hallo Ada", reply.Actions.Single().RenderedText, "Rendered in the contact's locale.");
        Assert.AreEqual(AutomationMatch.NotTriggered, result.Rules.Single(rule => rule.Rule.Value == "created").Match);
        var noMessage = Assert.IsInstanceOfType<AutomationValue<AutomationDryRunResult>>((await fixture.RulesAsync(service => service.DryRunAsync(fixture.Owner,
            new AutomationDryRunRequest(C1, AutomationEvent.MessageReceived())))).Value).Value;
        Assert.AreEqual(AutomationMatch.Unknown, noMessage.Rules.Single(rule => rule.Rule.Value == "refunds").Match);
        var draft = Assert.IsInstanceOfType<AutomationValue<AutomationDryRunResult>>((await fixture.RulesAsync(service => service.DryRunAsync(fixture.Owner,
            new AutomationDryRunRequest(C1, AutomationEvent.ConversationCreated(), Draft: new AutomationRule(new AutomationRuleId("draft"), "Draft", ConversationCreatedTrigger.Instance,
                new BusinessHoursCondition(true), [new AddLabelsAction(new LabelId("open"))], enabled: false))))).Value).Value;
        Assert.AreEqual(AutomationMatch.Matched, draft.Rules.Single().Match, "Monday 10:00 New York is open.");

        var after = await fixture.Get<IConversationStore>().GetAsync(fixture.Scope, C1);
        Assert.AreEqual(before!.Revision, after!.Revision);
        Assert.AreEqual(before.Conversation.LastSequence, after.Conversation.LastSequence);
        Assert.AreEqual(0, (await fixture.ExecutionsAsync()).Count);
        Assert.AreEqual(0, (await fixture.DispatchAsync()).Handled, "No outbox entries were produced.");

        Assert.IsInstanceOfType<AutomationRefused>((await fixture.RulesAsync(service => service.DryRunAsync(fixture.Agent,
            new AutomationDryRunRequest(C1, AutomationEvent.MessageReceived())))).Value);
        Assert.IsInstanceOfType<AutomationNotFound>((await fixture.RulesAsync(service => service.DryRunAsync(fixture.Owner,
            new AutomationDryRunRequest(new ConversationId("missing"), AutomationEvent.MessageReceived())))).Value);
    }

    [TestMethod]
    public async Task ReplayReDerivesTriggersAndJoinsRecordedExecutions()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(RefundRule());
        var start = fixture.Clock.GetUtcNow();
        await fixture.StartAsync("c1", "refund now");
        await fixture.DispatchAsync();
        var result = Assert.IsInstanceOfType<AutomationValue<AutomationReplayResult>>((await fixture.RulesAsync(service => service.ReplayAsync(fixture.Owner,
            new AutomationReplayRequest(C1, start.AddMinutes(-1), start.AddHours(1))))).Value).Value;
        var message = result.Items.Single(item => item.Event.Kind == AutomationEventKind.MessageReceived);
        Assert.AreEqual(1, message.Sequence);
        Assert.IsNull(message.Cause);
        Assert.AreEqual(AutomationMatch.Matched, message.Previews.Single().Match);
        Assert.AreEqual(AutomationExecutionStatus.Completed, message.Executions.Single().Execution.Status);
        var labels = result.Items.Single(item => item.Event.Kind == AutomationEventKind.ConversationUpdated && item.Event.Field == ConversationUpdateField.Labels);
        Assert.AreEqual(1, labels.Cause!.Depth, "Automation-caused events report their cause.");
        Assert.IsFalse(result.Truncated);
        Assert.ThrowsExactly<ArgumentException>(() => new AutomationReplayRequest(C1, start, start.AddDays(8)));
        Assert.IsInstanceOfType<AutomationRefused>((await fixture.RulesAsync(service => service.ReplayAsync(fixture.Agent,
            new AutomationReplayRequest(C1, start, start.AddHours(1))))).Value);
    }

    [TestMethod]
    public async Task SlaBreachRunsTheEscalationActions()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("escalate"), "Escalate", new SlaEventTrigger([SlaEventKind.Breached]), null,
            [ExecuteSlaEscalationAction.Instance]));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        var breach = new SlaBreachedEvent("k1", new Workspaces.SlaPolicyId("standard"), SlaMetric.FirstResponse, 1, TimeSpan.FromHours(4), fixture.Clock.GetUtcNow(),
            [new RaisePriorityAction(ConversationPriority.Urgent), new ReassignTeamAction(new TeamId("billing")), new NotifyTeamAction(new TeamId("billing"))]);
        await fixture.CommandAsync(service => service.AppendExtensionAsync(fixture.SlaWorker, C1, SlaEventCodec.Encode(breach)));
        await fixture.DispatchAsync();
        var conversation = await fixture.ConversationAsync("c1");
        Assert.AreEqual(ConversationPriority.Urgent, conversation.Priority);
        Assert.AreEqual(new TeamId("billing"), conversation.Team);
        var execution = (await fixture.ExecutionsAsync("c1")).Single().Execution;
        Assert.AreEqual(AutomationEventKind.SlaEvent, execution.EventKind);
        Assert.AreEqual(2, ((AutomationActionApplied)execution.Outcomes.Single().Value!).Sequences.Count);
    }

    [TestMethod]
    public async Task ScheduledRulesFireOncePerSlotAndRescheduleInTheirTimeZone()
    {
        using var fixture = await AutomationFixture.CreateAsync(start: new DateTimeOffset(2026, 3, 6, 15, 0, 0, TimeSpan.Zero));
        await fixture.StartAsync("c1");
        await fixture.StartAsync("c2");
        await fixture.CommandAsync(service => service.ChangeStatusAsync(fixture.Agent, new ConversationId("c2"), ConversationStatus.Pending));
        await fixture.DispatchAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("stale"), "Stale", new ScheduledTrigger(new AutomationSchedule("0 9 * * *", "America/New_York"),
            [ConversationStatusKind.Pending]), new ElapsedCondition(ConversationTimestamp.LastActivity, ElapsedComparison.AtLeast, TimeSpan.FromHours(12)),
            [new SetStatusAction(ConversationStatusKind.Resolved)]));
        var scheduler = () => fixture.InScopeAsync(services => services.GetRequiredService<AutomationScheduler>().RunOnceAsync());

        Assert.AreEqual(0, (await scheduler()).Fired, "Not due yet.");
        fixture.Clock.Advance(TimeSpan.FromHours(23));   // Saturday 09:00 EST = 14:00Z
        var run = await scheduler();
        Assert.AreEqual(1, run.Fired);
        Assert.AreEqual(1, run.Conversations, "Only pending conversations are scanned.");
        Assert.AreEqual(ConversationStatusKind.Resolved, (await fixture.ConversationAsync("c2")).Status.Kind);
        Assert.AreEqual(ConversationStatusKind.Open, (await fixture.ConversationAsync("c1")).Status.Kind);
        var execution = (await fixture.ExecutionsAsync("c2")).Single().Execution;
        Assert.AreEqual(AutomationTriggerKeys.Scheduled(new DateTimeOffset(2026, 3, 7, 14, 0, 0, TimeSpan.Zero)), execution.TriggerKey);
        Assert.AreEqual(0, (await scheduler()).Fired, "The slot is done.");

        // The next slot is Sunday 09:00 EDT (13:00Z) — after the spring-forward.
        fixture.Clock.Advance(TimeSpan.FromHours(22).Add(TimeSpan.FromMinutes(59)));
        Assert.AreEqual(0, (await scheduler()).Fired);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, (await scheduler()).Fired);
    }

    [TestMethod]
    public async Task ContactUpdatesFireContactRules()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("contact"), "Contact", ContactUpdatedTrigger.Instance,
            new HasLabelsCondition(ConditionTarget.Contact, LabelMatch.Any, new LabelId("vip")), [new SetPriorityAction(ConversationPriority.High)]));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        var follower = () => fixture.InScopeAsync(services => services.GetRequiredService<AutomationContactFollower>().CatchUpAsync(fixture.Scope));
        await follower();
        Assert.AreEqual(ConversationPriority.None, (await fixture.ConversationAsync("c1")).Priority);
        await fixture.InScopeAsync(async services =>
        {
            var contacts = services.GetRequiredService<Contacts.IContactService>();
            var current = Assert.IsInstanceOfType<Contacts.ContactApplied<Contacts.ContactView>>((await contacts.GetAsync(fixture.Owner, new ContactId("ada"))).Value).Value;
            return await contacts.UpdateAsync(fixture.Owner, new ContactId("ada"), current.Revision, [new Contacts.AddContactLabel(new LabelId("vip"))]);
        });
        Assert.IsTrue(await follower() > 0);
        Assert.AreEqual(ConversationPriority.High, (await fixture.ConversationAsync("c1")).Priority);
        Assert.AreEqual(0, await follower(), "The checkpoint advanced.");
    }

    [TestMethod]
    public async Task RuleManagementIsAccessChecked()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        Assert.IsInstanceOfType<AutomationRefused>((await fixture.RulesAsync(service => service.CreateAsync(fixture.Agent, RefundRule()))).Value);
        Assert.IsInstanceOfType<AutomationRefused>((await fixture.RulesAsync(service => service.ListAsync(fixture.Agent))).Value);
        var created = await fixture.CreateRuleAsync(RefundRule());
        Assert.IsInstanceOfType<AutomationConflict>((await fixture.RulesAsync(service => service.UpdateAsync(fixture.Owner, RefundRule(5), 9))).Value);
        Assert.IsInstanceOfType<AutomationValue<StoredAutomationRule>>((await fixture.RulesAsync(service => service.UpdateAsync(fixture.Owner, RefundRule(5), created.Revision))).Value);
        var invalid = await fixture.RulesAsync(service => service.CreateAsync(fixture.Owner, new AutomationRule(new AutomationRuleId("tz"), "TZ",
            new ScheduledTrigger(new AutomationSchedule("0 9 * * *", "Mars/Olympus_Mons"), [ConversationStatusKind.Open]), null, [new AddLabelsAction(new LabelId("x"))])));
        Assert.AreEqual(AutomationRefusal.Invalid, ((AutomationRefused)invalid.Value!).Reason);
        Assert.IsInstanceOfType<AutomationValue<StoredAutomationRule>>((await fixture.RulesAsync(service => service.DeleteAsync(fixture.Owner, created.Id, 2))).Value);
        Assert.IsInstanceOfType<AutomationNotFound>((await fixture.RulesAsync(service => service.GetAsync(fixture.Owner, created.Id))).Value);
    }
}
