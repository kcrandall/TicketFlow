using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Automation.StoreContracts;

/// <summary>
/// Behavioral contract every automation store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the
/// same storage (for example two data sources). Schedule checks need the stores to share the supplied <see cref="FakeTimeProvider"/>.
/// </summary>
public static class AutomationStoreContract
{
    public static readonly DateTimeOffset At = TestClock.DefaultStart;

    public static string Describe<T>(AutomationWriteResult<T> result) => result switch
    {
        AutomationWriteApplied<T> => "applied",
        AutomationWriteNotFound => "not-found",
        AutomationWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
        AutomationWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static AutomationRule Rule(string id, int priority = 100, string name = "Tag VIP", bool scheduled = false) => new(new AutomationRuleId(id), name,
        scheduled
            ? new ScheduledTrigger(new AutomationSchedule("0 9 * * 1-5", "America/New_York"), [ConversationStatusKind.Open, ConversationStatusKind.Pending])
            : MessageReceivedTrigger.Instance,
        new AllOfCondition(new HasLabelsCondition(ConditionTarget.Conversation, LabelMatch.Any, new LabelId("vip")),
            new MessageContentCondition(MessageTextOperator.Matches, "refund|chargeback")),
        [new AddLabelsAction(new LabelId("billing")), new SetPriorityAction(ConversationPriority.High)], priority);

    public static Macro Macro(string id, string? owner = null) => new(new MacroId(id), "Close politely", owner is null ? MacroVisibility.Shared : MacroVisibility.Personal,
        owner is null ? null : new UserId(owner), [new SendReplyAction(new CannedResponseId("thanks")), new SetStatusAction(ConversationStatusKind.Resolved)]);

    public static CannedResponse Response(string id, string shortcut, string text = "Hi {{contact.name|there}}") => new(new CannedResponseId(id), shortcut, "Greeting", "en",
        [new CannedResponseVariant("en", new AutomationTemplate(text)), new CannedResponseVariant("de", new AutomationTemplate("Hallo {{contact.name}}"))]);

    public static AutomationExecution Execution(string rule = "r1", string conversation = "c1", string trigger = "evt:1", DateTimeOffset? startedAt = null) =>
        new(Guid.NewGuid(), new AutomationRuleId(rule), new ConversationId(conversation), trigger, AutomationEventKind.MessageReceived, 1, AutomationExecutionStatus.Running,
            null, startedAt ?? At, null, 2, []);

    public static async Task VerifyRulesAsync(IAutomationRuleStore primary, IAutomationRuleStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "rules-" + Guid.NewGuid().ToString("N")[..8]);
        var created = Assert.IsInstanceOfType<AutomationWriteApplied<StoredAutomationRule>>((await primary.CreateAsync(scope, Rule("b", 10), null)).Value).Value;
        Assert.AreEqual(1, created.Revision);
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(scope, Rule("b", 10), null)));
        await primary.CreateAsync(scope, Rule("a", 10), null);
        await primary.CreateAsync(scope, Rule("c", 5), null);
        var listed = await competing.ListAsync(scope);
        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, listed.Select(rule => rule.Id.Value).ToArray());
        Assert.AreEqual(Rule("b", 10), listed[2].Rule);

        var updated = Assert.IsInstanceOfType<AutomationWriteApplied<StoredAutomationRule>>((await competing.UpdateAsync(scope, Rule("b", 1, "Renamed"), 1, null)).Value).Value;
        Assert.AreEqual(2, updated.Revision);
        Assert.AreEqual("conflict:2", Describe(await primary.UpdateAsync(scope, Rule("b", 1), 1, null)));
        Assert.AreEqual("not-found", Describe(await primary.UpdateAsync(scope, Rule("zz"), 1, null)));
        Assert.AreEqual("Renamed", (await primary.GetAsync(scope, new AutomationRuleId("b")))!.Rule.Name);

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, new AutomationRuleId("b")));
            Assert.AreEqual(0, (await primary.ListAsync(other)).Count);
            Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(other, new AutomationRuleId("b"), 2)));
        }
        Assert.AreEqual("conflict:2", Describe(await primary.DeleteAsync(scope, new AutomationRuleId("b"), 1)));
        Assert.AreEqual("applied", Describe(await competing.DeleteAsync(scope, new AutomationRuleId("b"), 2)));
        Assert.IsNull(await primary.GetAsync(scope, new AutomationRuleId("b")));

        var capacityScope = TestScopes.Create(workspaceId: "cap-" + Guid.NewGuid().ToString("N")[..8]);
        for (var index = 0; index < IAutomationRuleStore.MaximumRulesPerWorkspace; index++)
            Assert.AreEqual("applied", Describe(await primary.CreateAsync(capacityScope, Rule($"r{index:D3}"), null)));
        Assert.AreEqual("rejected:CapacityExceeded", Describe(await competing.CreateAsync(capacityScope, Rule("overflow"), null)));
    }

    public static async Task VerifySchedulesAsync(IAutomationRuleStore primary, IAutomationRuleStore competing, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "sched-" + Guid.NewGuid().ToString("N")[..8]);
        var now = clock.GetUtcNow();
        await primary.CreateAsync(scope, Rule("later", scheduled: true), now.AddMinutes(10));
        await primary.CreateAsync(scope, Rule("due", scheduled: true), now.AddMinutes(-1));
        await primary.CreateAsync(scope, Rule("none"), null);
        var claims = (await competing.ClaimDueSchedulesAsync(10, TimeSpan.FromMinutes(1))).Where(claim => claim.Scope == scope).ToList();
        Assert.AreEqual(1, claims.Count);
        Assert.AreEqual("due", claims[0].Rule.Value);
        Assert.AreEqual(0, (await primary.ClaimDueSchedulesAsync(10, TimeSpan.FromMinutes(1))).Count(claim => claim.Scope == scope), "A live lease is never shared.");

        Assert.IsTrue(await primary.CompleteScheduleAsync(claims[0], now.AddMinutes(30)));
        Assert.IsFalse(await primary.CompleteScheduleAsync(claims[0], now.AddMinutes(30)), "The lease was cleared.");
        clock.Advance(TimeSpan.FromMinutes(11));
        var second = (await primary.ClaimDueSchedulesAsync(10, TimeSpan.FromMinutes(1))).Single(claim => claim.Scope == scope);
        Assert.AreEqual("later", second.Rule.Value);

        // A rule write bumps the fence, so the stale claim can no longer complete or release.
        var stored = await primary.GetAsync(scope, new AutomationRuleId("later"));
        await competing.UpdateAsync(scope, stored!.Rule, stored.Revision, clock.GetUtcNow().AddHours(1));
        Assert.IsFalse(await primary.ReleaseScheduleAsync(second, clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromMinutes(25));
        var retry = (await primary.ClaimDueSchedulesAsync(10, TimeSpan.FromMinutes(1))).Single(claim => claim.Scope == scope);
        Assert.AreEqual("due", retry.Rule.Value);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.IsFalse(await primary.ReleaseScheduleAsync(retry, clock.GetUtcNow()), "An expired lease cannot release.");
        var reclaimed = (await competing.ClaimDueSchedulesAsync(10, TimeSpan.FromMinutes(1))).Single(claim => claim.Scope == scope);
        Assert.IsTrue(reclaimed.Fence > retry.Fence);
        Assert.IsTrue(await competing.ReleaseScheduleAsync(reclaimed, clock.GetUtcNow().AddMinutes(5)));

        await primary.DeleteAsync(scope, new AutomationRuleId("due"), 1);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsFalse((await primary.ClaimDueSchedulesAsync(10, TimeSpan.FromMinutes(1))).Any(claim => claim.Scope == scope && claim.Rule.Value == "due"));
    }

    public static async Task VerifyConcurrentScheduleClaimsAsync(IReadOnlyList<IAutomationRuleStore> clients, FakeTimeProvider clock)
    {
        var scope = TestScopes.Create(workspaceId: "claims-" + Guid.NewGuid().ToString("N")[..8]);
        for (var index = 0; index < 30; index++)
            await clients[0].CreateAsync(scope, Rule($"s{index:D2}", scheduled: true), clock.GetUtcNow().AddSeconds(-index));
        var results = await Task.WhenAll(clients.Select(client => Task.Run(async () =>
        {
            var claimed = new List<string>();
            for (var round = 0; round < 5; round++)
                claimed.AddRange((await client.ClaimDueSchedulesAsync(5, TimeSpan.FromMinutes(5))).Where(claim => claim.Scope == scope).Select(claim => claim.Rule.Value));
            return claimed;
        })));
        var all = results.SelectMany(items => items).ToList();
        Assert.AreEqual(30, all.Count);
        Assert.AreEqual(30, all.Distinct().Count(), "Each due timer is claimed exactly once.");
    }

    public static async Task VerifyExecutionsAsync(IAutomationExecutionStore primary, IAutomationExecutionStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "exec-" + Guid.NewGuid().ToString("N")[..8]);
        var execution = Execution();
        var started = Assert.IsInstanceOfType<AutomationExecutionStarted>((await primary.BeginAsync(scope, execution)).Value).Stored;
        Assert.AreEqual(1, started.Revision);
        var duplicate = Assert.IsInstanceOfType<AutomationExecutionExists>((await competing.BeginAsync(scope, Execution())).Value).Stored;
        Assert.AreEqual(execution.Id, duplicate.Id);
        Assert.AreEqual(execution, (await competing.FindAsync(scope, new AutomationRuleId("r1"), new ConversationId("c1"), "evt:1"))!.Execution);

        var progress = execution.WithProgress([new AutomationActionApplied([5, 6])]);
        var updated = Assert.IsInstanceOfType<AutomationWriteApplied<StoredAutomationExecution>>((await competing.UpdateAsync(scope, progress, 1)).Value).Value;
        Assert.AreEqual(2, updated.Revision);
        Assert.AreEqual("conflict:2", Describe(await primary.UpdateAsync(scope, progress, 1)));
        var cause = await primary.FindCauseAsync(scope, new ConversationId("c1"), 6);
        Assert.AreEqual(new AutomationCause(execution.Id, new AutomationRuleId("r1"), 1), cause);
        Assert.IsNull(await primary.FindCauseAsync(scope, new ConversationId("c1"), 7));
        var completed = progress.WithProgress([new AutomationActionApplied([5, 6]), new AutomationActionRefused(AutomationActionRefusal.Forbidden)]).Complete(At.AddSeconds(1), 8);
        Assert.AreEqual("applied", Describe(await primary.UpdateAsync(scope, completed, 2)));
        Assert.AreEqual(completed, (await competing.GetAsync(scope, execution.Id))!.Execution);
        Assert.IsNotNull(await competing.FindCauseAsync(scope, new ConversationId("c1"), 8), "The marker sequence is indexed.");

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, execution.Id));
            Assert.IsNull(await primary.FindAsync(other, new AutomationRuleId("r1"), new ConversationId("c1"), "evt:1"));
            Assert.IsNull(await primary.FindCauseAsync(other, new ConversationId("c1"), 6));
            Assert.AreEqual("not-found", Describe(await primary.UpdateAsync(other, completed, 3)));
        }

        // Rate counts and the log.
        for (var index = 0; index < 5; index++)
            await primary.BeginAsync(scope, Execution(rule: "r2", trigger: $"evt:{10 + index}", startedAt: At.AddMinutes(index)));
        await primary.BeginAsync(scope, Execution(rule: "r2", conversation: "c2", trigger: "evt:1", startedAt: At.AddMinutes(3)));
        Assert.AreEqual(4, await competing.CountSinceAsync(scope, new ConversationId("c1"), At.AddMinutes(1), 100));
        Assert.AreEqual(2, await competing.CountSinceAsync(scope, new ConversationId("c1"), At, 2));
        var first = await competing.ListAsync(scope, new AutomationExecutionQuery(conversation: new ConversationId("c1"), limit: 4));
        Assert.AreEqual(4, first.Executions.Count);
        Assert.IsNotNull(first.Next);
        Assert.IsTrue(first.Executions.Zip(first.Executions.Skip(1)).All(pair => pair.First.Execution.StartedAt >= pair.Second.Execution.StartedAt));
        var second = await competing.ListAsync(scope, new AutomationExecutionQuery(conversation: new ConversationId("c1"), limit: 4, after: first.Next));
        Assert.AreEqual(2, second.Executions.Count);
        Assert.IsNull(second.Next);
        Assert.AreEqual(6, first.Executions.Concat(second.Executions).Select(item => item.Id).Distinct().Count());
        var ranged = await primary.ListAsync(scope, new AutomationExecutionQuery(rule: new AutomationRuleId("r2"), from: At.AddMinutes(1), to: At.AddMinutes(3)));
        Assert.AreEqual(2, ranged.Executions.Count);

        // Checkpoints.
        Assert.IsNull(await primary.GetCheckpointAsync(scope, "contacts"));
        Assert.IsTrue(await primary.SetCheckpointAsync(scope, "contacts", 5, null));
        Assert.IsFalse(await competing.SetCheckpointAsync(scope, "contacts", 6, null));
        Assert.IsTrue(await competing.SetCheckpointAsync(scope, "contacts", 9, 5));
        Assert.AreEqual(9, await primary.GetCheckpointAsync(scope, "contacts"));
        Assert.IsNull(await primary.GetCheckpointAsync(TestScopes.LevelVariants(scope)[5], "contacts"));
    }

    public static async Task VerifyConcurrentBeginAsync(IReadOnlyList<IAutomationExecutionStore> clients)
    {
        var scope = TestScopes.Create(workspaceId: "begin-" + Guid.NewGuid().ToString("N")[..8]);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() => clients[index % clients.Count].BeginAsync(scope, Execution(trigger: "evt:race")))));
        Assert.AreEqual(1, results.Count(result => result.Value is AutomationExecutionStarted), "Exactly one beginner wins.");
        Assert.AreEqual(1, results.Select(result => result switch
        {
            AutomationExecutionStarted started => started.Stored.Id,
            AutomationExecutionExists exists => exists.Stored.Id,
        }).Distinct().Count());
    }

    public static async Task VerifyMacrosAsync(IMacroStore primary, IMacroStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "macro-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.AreEqual("applied", Describe(await primary.CreateAsync(scope, Macro("shared"))));
        Assert.AreEqual("applied", Describe(await primary.CreateAsync(scope, Macro("mine", owner: "agent"))));
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(scope, Macro("mine", owner: "agent"))));
        CollectionAssert.AreEqual(new[] { "mine", "shared" }, (await competing.ListAsync(scope)).Select(item => item.Id.Value).ToArray());
        Assert.AreEqual(Macro("mine", owner: "agent"), (await competing.GetAsync(scope, new MacroId("mine")))!.Macro);
        Assert.AreEqual("applied", Describe(await competing.UpdateAsync(scope, Macro("shared"), 1)));
        Assert.AreEqual("conflict:2", Describe(await primary.UpdateAsync(scope, Macro("shared"), 1)));
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, new MacroId("shared")));
            Assert.AreEqual(0, (await primary.ListAsync(other)).Count);
        }
        Assert.AreEqual("applied", Describe(await primary.DeleteAsync(scope, new MacroId("shared"), 2)));
        Assert.AreEqual("not-found", Describe(await primary.DeleteAsync(scope, new MacroId("shared"), 2)));
    }

    public static async Task VerifyCannedResponsesAsync(ICannedResponseStore primary, ICannedResponseStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "canned-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.AreEqual("applied", Describe(await primary.CreateAsync(scope, Response("hi", "hello"))));
        Assert.AreEqual("applied", Describe(await primary.CreateAsync(scope, Response("bye", "goodbye"))));
        Assert.AreEqual("applied", Describe(await primary.CreateAsync(scope, Response("hey", "hello.short"))));
        Assert.AreEqual("rejected:DuplicateShortcut", Describe(await competing.CreateAsync(scope, Response("dup", "hello"))));
        Assert.AreEqual("conflict:1", Describe(await competing.CreateAsync(scope, Response("hi", "other"))));
        Assert.AreEqual(Response("hi", "hello"), (await competing.FindByShortcutAsync(scope, "hello"))!.Response);
        Assert.AreEqual("rejected:DuplicateShortcut", Describe(await competing.UpdateAsync(scope, Response("bye", "hello"), 1)));
        Assert.AreEqual("applied", Describe(await competing.UpdateAsync(scope, Response("bye", "farewell"), 1)));
        Assert.IsNull(await primary.FindByShortcutAsync(scope, "goodbye"));

        var page = await primary.ListAsync(scope, new CannedResponseQuery(prefix: "hello", limit: 1));
        Assert.AreEqual("hello", page.Responses.Single().Response.Shortcut);
        Assert.AreEqual("hello", page.NextAfterShortcut);
        var next = await primary.ListAsync(scope, new CannedResponseQuery(prefix: "hello", afterShortcut: page.NextAfterShortcut, limit: 1));
        Assert.AreEqual("hello.short", next.Responses.Single().Response.Shortcut);
        Assert.IsNull(next.NextAfterShortcut);
        Assert.AreEqual(3, (await competing.ListAsync(scope, new CannedResponseQuery())).Responses.Count);

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.FindByShortcutAsync(other, "hello"));
            Assert.AreEqual("applied", Describe(await primary.CreateAsync(other, Response("hi", "hello"))), "Shortcuts are unique per workspace only.");
        }
        Assert.AreEqual("applied", Describe(await primary.DeleteAsync(scope, new CannedResponseId("hi"), 1)));
        Assert.AreEqual("applied", Describe(await competing.CreateAsync(scope, Response("dup", "hello"))), "A deleted shortcut is free again.");
    }
}
