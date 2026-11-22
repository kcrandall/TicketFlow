using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.Automation.Tests;

[TestClass]
public sealed class AutomationReviewRegressionTests
{
    private static readonly ConversationId C1 = new("c1");

    private static SlaBreachedEvent Breach(DateTimeOffset at) => new("k1", new SlaPolicyId("standard"), SlaMetric.FirstResponse, 1, TimeSpan.FromHours(4), at,
        [new RaisePriorityAction(ConversationPriority.Urgent)]);

    [TestMethod]
    public async Task ForgedSlaEventsFromOtherPrincipalsAreIgnored()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("escalate"), "Escalate", new SlaEventTrigger([SlaEventKind.Breached]), null,
            [ExecuteSlaEscalationAction.Instance]));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        // An agent with conversation:manage appends a well-formed sla.breached payload.
        Assert.IsInstanceOfType<ConversationApplied>((await fixture.CommandAsync(service => service.AppendExtensionAsync(fixture.Agent, C1,
            SlaEventCodec.Encode(Breach(fixture.Clock.GetUtcNow()))))).Value);
        await fixture.DispatchAsync();
        Assert.AreEqual(ConversationPriority.None, (await fixture.ConversationAsync("c1")).Priority);
        Assert.AreEqual(0, (await fixture.ExecutionsAsync("c1")).Count);
        var start = fixture.Clock.GetUtcNow().AddMinutes(-1);
        var replay = Assert.IsInstanceOfType<AutomationValue<AutomationReplayResult>>((await fixture.RulesAsync(service => service.ReplayAsync(fixture.Owner,
            new AutomationReplayRequest(C1, start, start.AddHours(1))))).Value).Value;
        Assert.IsFalse(replay.Items.Any(item => item.Event.Kind == AutomationEventKind.SlaEvent), "Replay ignores forged SLA events too.");

        // The same payload from the configured SLA worker is honoured.
        await fixture.CommandAsync(service => service.AppendExtensionAsync(fixture.SlaWorker, C1, SlaEventCodec.Encode(Breach(fixture.Clock.GetUtcNow()))));
        await fixture.DispatchAsync();
        Assert.AreEqual(ConversationPriority.Urgent, (await fixture.ConversationAsync("c1")).Priority);
    }

    [TestMethod]
    public async Task NonMatchingRulesCostAConstantNumberOfStoreCalls()
    {
        var log = new CallLog();
        using var fixture = await AutomationFixture.CreateAsync(configure: services =>
        {
            services.AddSingleton<IAutomationRuleStore>(provider => Interceptor<IAutomationRuleStore>.Wrap(new InMemoryAutomationRuleStore(provider.GetService<TimeProvider>()), log, "rules."));
            services.AddSingleton<IAutomationExecutionStore>(_ => Interceptor<IAutomationExecutionStore>.Wrap(new InMemoryAutomationExecutionStore(), log, "executions."));
            services.AddSingleton<IInboxStore>(_ => Interceptor<IInboxStore>.Wrap(new InMemoryInboxStore(), log, "inboxes."));
            services.AddSingleton<IConversationStore>(provider => Interceptor<IConversationStore>.Wrap(provider.GetRequiredService<InMemoryConversationStore>(), log, "conversations."));
        });
        for (var index = 0; index < 50; index++)
            await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId($"r{index:D2}"), $"Rule {index}", MessageReceivedTrigger.Instance,
                new MessageContentCondition(MessageTextOperator.Contains, $"never-{index}"), [new AddLabelsAction(new LabelId("x"))]));
        await fixture.StartAsync("c1", "hello");
        log.Reset();
        await fixture.CommandAsync(service => service.AppendIncomingAsync(fixture.Channel, C1, new IncomingMessageDraft("still nothing", ExternalId: "e1")));
        log.Reset();
        await fixture.DispatchAsync();

        // Two changes (start, incoming message), each with one message event and 50 firing-but-false rules.
        Assert.AreEqual(2, log["rules.GetRuleSetVersionAsync"]);
        Assert.AreEqual(1, log["rules.ListAsync"], "The decoded rule set is cached by version.");
        Assert.AreEqual(2, log["executions.FindManyAsync"], "One record lookup per occurrence.");
        Assert.AreEqual(0, log["executions.FindAsync"]);
        Assert.AreEqual(0, log["executions.BeginAsync"]);
        Assert.AreEqual(2, log["inboxes.GetAsync"], "One context per occurrence, not per rule.");
        Assert.AreEqual(2, log["conversations.ReadTimelineAsync"], "One timeline read per change.");
        Assert.AreEqual(0, (await fixture.ExecutionsAsync()).Count);
    }

    [TestMethod]
    public async Task TwoHandlersRacingOnOneEntryRunTheRuleOnce()
    {
        using var fixture = await AutomationFixture.CreateAsync();
        await fixture.InScopeAsync(services => services.GetRequiredService<ICannedResponseService>().CreateAsync(fixture.Owner,
            new CannedResponse(new CannedResponseId("ack"), "ack", "Ack", "en", [new CannedResponseVariant("en", new AutomationTemplate("Got it."))])));
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("ack"), "Ack", MessageReceivedTrigger.Instance, null,
            [new SendReplyAction(new CannedResponseId("ack")), new AddLabelsAction(new LabelId("acked"))]));
        await fixture.StartAsync("c1", "hi");
        var first = (await fixture.TimelineAsync("c1"))[0];
        var entry = new ConversationOutboxEntry(1, new ConversationChange(Guid.NewGuid(), fixture.Scope, C1, new InboxId("email"), 1, ConversationChangeKind.Started,
            [ConversationEventSummary.From(first)], null, null, fixture.Clock.GetUtcNow()));
        for (var round = 0; round < 5; round++)
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => fixture.InScopeAsync(async services =>
            {
                await services.GetRequiredService<AutomationOutboxHandler>().HandleAsync(entry);
                return 0;
            }))));
        var timeline = await fixture.TimelineAsync("c1");
        Assert.AreEqual(1, timeline.Count(item => item.Event.Value is MessageEvent { Direction: MessageDirection.Outgoing }));
        Assert.AreEqual(1, timeline.Count(item => item.Event.Value is ExtensionEvent { Kind: AutomationEventCodec.AppliedKind }));
        var execution = (await fixture.ExecutionsAsync("c1")).Single().Execution;
        Assert.AreEqual(AutomationExecutionStatus.Completed, execution.Status);
        Assert.IsTrue(execution.Outcomes.All(outcome => outcome.Value is AutomationActionApplied));
    }

    [TestMethod]
    public async Task ExecutionResumesAfterLosingAProgressWrite()
    {
        var log = new CallLog();
        var inner = new InMemoryAutomationExecutionStore();
        using var fixture = await AutomationFixture.CreateAsync(configure: services =>
            services.AddSingleton<IAutomationExecutionStore>(_ => Interceptor<IAutomationExecutionStore>.Wrap(inner, log)));
        await fixture.InScopeAsync(services => services.GetRequiredService<ICannedResponseService>().CreateAsync(fixture.Owner,
            new CannedResponse(new CannedResponseId("ack"), "ack", "Ack", "en", [new CannedResponseVariant("en", new AutomationTemplate("Got it."))])));
        var rule = new AutomationRule(new AutomationRuleId("ack"), "Ack", MessageReceivedTrigger.Instance, null,
            [new SendReplyAction(new CannedResponseId("ack")), new AddNoteAction(new AutomationTemplate("noted"))]);
        await fixture.CreateRuleAsync(rule);
        await fixture.StartAsync("c1", "hi");

        // The first progress write loses to a concurrent writer: the runner stops after the reply without recording it.
        var lost = false;
        log.Override = (method, args) =>
        {
            if (method != "UpdateAsync" || lost || ((AutomationExecution)args![1]!).Outcomes.Count == 0)
                return null;
            lost = true;
            var execution = (AutomationExecution)args[1]!;
            var bumped = inner.UpdateAsync((TicketScope)args[0]!, execution.WithProgress([]), (long)args[2]!).GetAwaiter().GetResult();
            Assert.IsInstanceOfType<AutomationWriteApplied<StoredAutomationExecution>>(bumped.Value);
            return inner.UpdateAsync((TicketScope)args[0]!, execution, (long)args[2]!);
        };
        var occurrence = new AutomationOccurrence(fixture.Scope, C1, AutomationEvent.MessageReceived(1), AutomationTriggerKeys.TimelineEvent(1), false, null, null);
        var firstRun = await fixture.InScopeAsync(services => services.GetRequiredService<AutomationRunner>().RunAsync(occurrence, [rule]));
        Assert.AreEqual(0, firstRun.Executed);
        Assert.IsTrue(lost);
        var stalled = (await fixture.ExecutionsAsync("c1")).Single().Execution;
        Assert.AreEqual(AutomationExecutionStatus.Running, stalled.Status);
        Assert.AreEqual(0, stalled.Outcomes.Count);

        var secondRun = await fixture.InScopeAsync(services => services.GetRequiredService<AutomationRunner>().RunAsync(occurrence, [rule]));
        Assert.AreEqual(1, secondRun.Executed);
        var resumed = (await fixture.ExecutionsAsync("c1")).Single().Execution;
        Assert.AreEqual(AutomationExecutionStatus.Completed, resumed.Status);
        Assert.IsTrue(resumed.Outcomes.All(outcome => outcome.Value is AutomationActionApplied));
        var timeline = await fixture.TimelineAsync("c1");
        Assert.AreEqual(1, timeline.Count(item => item.Event.Value is MessageEvent { Direction: MessageDirection.Outgoing }), "The reply was not sent twice.");
        Assert.AreEqual(1, timeline.Count(item => item.Event.Value is NoteEvent));
    }

    [TestMethod]
    public async Task MissedSlotsCoalesceIntoOneRun()
    {
        using var fixture = await AutomationFixture.CreateAsync(start: new DateTimeOffset(2026, 1, 5, 15, 0, 0, TimeSpan.Zero));
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("daily"), "Daily", new ScheduledTrigger(new AutomationSchedule("0 9 * * *", "UTC"),
            [ConversationStatusKind.Open]), null, [new AddNoteAction(new AutomationTemplate("daily check"))]));
        fixture.Clock.Advance(TimeSpan.FromDays(5));   // five 09:00 slots missed
        var run = await fixture.InScopeAsync(services => services.GetRequiredService<AutomationScheduler>().RunOnceAsync());
        Assert.AreEqual(1, run.Fired);
        Assert.AreEqual(1, (await fixture.ExecutionsAsync("c1")).Count, "One execution, not one per missed slot.");
        Assert.AreEqual(0, (await fixture.InScopeAsync(services => services.GetRequiredService<AutomationScheduler>().RunOnceAsync())).Fired);
        fixture.Clock.Advance(TimeSpan.FromHours(17));   // 08:00, the next slot is 09:00
        Assert.AreEqual(0, (await fixture.InScopeAsync(services => services.GetRequiredService<AutomationScheduler>().RunOnceAsync())).Fired);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(1, (await fixture.InScopeAsync(services => services.GetRequiredService<AutomationScheduler>().RunOnceAsync())).Fired);
    }

    [TestMethod]
    public async Task FailedScheduledRunsAreReleasedOrCountedLost()
    {
        var rulesLog = new CallLog();
        var plannerLog = new CallLog();
        using var fixture = await AutomationFixture.CreateAsync(start: new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero), configure: services =>
        {
            services.AddSingleton<IAutomationRuleStore>(provider => Interceptor<IAutomationRuleStore>.Wrap(new InMemoryAutomationRuleStore(provider.GetService<TimeProvider>()), rulesLog));
            services.AddScoped<IAccessQueryPlanner>(provider => Interceptor<IAccessQueryPlanner>.Wrap(ActivatorUtilities.CreateInstance<AccessQueryPlanner>(provider), plannerLog));
        });
        await fixture.StartAsync("c1");
        await fixture.DispatchAsync();
        await fixture.CreateRuleAsync(new AutomationRule(new AutomationRuleId("daily"), "Daily", new ScheduledTrigger(new AutomationSchedule("0 9 * * *", "UTC"),
            [ConversationStatusKind.Open]), null, [new AddLabelsAction(new LabelId("checked"))]));
        Task<AutomationScheduleRunResult> RunAsync() => fixture.InScopeAsync(services => services.GetRequiredService<AutomationScheduler>().RunOnceAsync());
        plannerLog.Override = (method, _) => method == "PlanAsync" ? Task.FromException<AccessFilter>(new InvalidOperationException("planner down")) : null;
        fixture.Clock.Advance(TimeSpan.FromHours(1));

        var failed = await RunAsync();
        Assert.AreEqual(new AutomationScheduleRunResult(0, 0, 1, 0), failed, "A failed run is released for a retry.");
        Assert.AreEqual(0, (await fixture.ConversationAsync("c1")).Labels.Count);
        Assert.AreEqual(new AutomationScheduleRunResult(0, 0, 0, 0), await RunAsync(), "Not retried before the retry delay.");

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        rulesLog.Override = (method, _) => method == "ReleaseScheduleAsync" ? Task.FromResult(false) : null;
        Assert.AreEqual(new AutomationScheduleRunResult(0, 0, 0, 1), await RunAsync(), "A refused release (lease lost) is counted as lost.");

        plannerLog.Override = null;
        rulesLog.Override = null;
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));   // the lost claim's lease expires
        var recovered = await RunAsync();
        Assert.AreEqual(1, recovered.Fired);
        CollectionAssert.AreEqual(new[] { new LabelId("checked") }, (await fixture.ConversationAsync("c1")).Labels.ToArray());
    }
}
