using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Assist.Tests;

[TestClass]
public sealed class ServiceTests
{
    private const string SummaryJson = """{"summary":"Customer was double charged.","key_points":["Refund requested"," "],"next_step":"Confirm the refund"}""";

    [TestMethod]
    public async Task SummaryIsParsedAccountedAndRecordedWithMetadataOnly()
    {
        using var fixture = await AssistFixture.CreateAsync();
        var agent = await fixture.AgentAsync();
        fixture.Model.Enqueue(new ScriptedTurn(SummaryJson, ChatFinishReason.Stop,
            new UsageDetails { InputTokenCount = 1000, OutputTokenCount = 50, CachedInputTokenCount = 600, AdditionalCounts = new() { ["CacheCreationInputTokens"] = 100 } }));

        var result = AssistFixture.Succeeded(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1)));

        Assert.AreEqual("Customer was double charged.", result.Value.Summary);
        CollectionAssert.AreEqual(new[] { "Refund requested" }, result.Value.KeyPoints.ToArray());
        Assert.AreEqual("Confirm the refund", result.Value.NextStep);
        Assert.AreEqual(new AssistUsage(300, 50, 600, 100), result.Usage);
        Assert.AreEqual("scripted-large", result.Model);
        Assert.AreEqual(AssistRecording.Recorded, result.Recording);
        Assert.AreEqual("scripted-large-v1", fixture.Model.Requests.Single().Options!.ModelId);
        Assert.IsNotNull(fixture.Model.Requests.Single().Options!.ResponseFormat as ChatResponseFormatJson);

        var usage = (await fixture.Store.GetUsageAsync(fixture.Scope, result.UsageId))!;
        Assert.AreEqual(AssistUsageState.Completed, usage.State);
        Assert.AreEqual(1050, usage.CountedTokens);
        Assert.AreEqual(AssistFixture.C1, usage.Conversation);
        Assert.AreEqual("agent", usage.Actor.Id);

        var timeline = await fixture.TimelineAsync();
        var recorded = timeline.Events[^1].Event;
        var extension = (ExtensionEvent)recorded.Event.Value!;
        Assert.AreEqual(AssistTimelineKinds.Summary, extension.Kind);
        Assert.AreEqual("agent", recorded.Actor.Id);
        var payload = AssistJsonCodec.DecodeTimelinePayload(extension.PayloadVersion, extension.Payload);
        Assert.AreEqual(new AssistTimelinePayload(AssistFeature.ConversationSummary, "scripted-large", result.UsageId, result.Usage, []), payload);
        Assert.DoesNotContain("double charged", extension.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain(AssistFixture.CustomerMessage, extension.Payload, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task RecordingNeedsConversationManageAndCanBeDisabled()
    {
        using var fixture = await AssistFixture.CreateAsync();
        var noManage = await fixture.UserWithAsync("no-manage", ConversationPermissions.Read, AssistPermissions.Use);
        fixture.Model.Reply(SummaryJson);
        Assert.AreEqual("ok:Forbidden", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(noManage, new AssistSummaryRequest(AssistFixture.C1))));

        using var quiet = await AssistFixture.CreateAsync(options => options.RecordTimelineEvents = false);
        var agent = await quiet.AgentAsync();
        quiet.Model.Reply(SummaryJson);
        Assert.AreEqual("ok:NotApplicable", AssistFixture.Describe(await quiet.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))));
        Assert.IsFalse((await quiet.TimelineAsync()).Events.Any(view => view.Event.Event.Value is ExtensionEvent));
    }

    [TestMethod]
    public async Task TriageSuggestionsAreRestrictedToCandidates()
    {
        using var fixture = await AssistFixture.CreateAsync();
        var agent = await fixture.AgentAsync();
        var request = new AssistTriageRequest(AssistFixture.C1,
            [new AssistLabelOption(new LabelId("billing"), "Payments"), new AssistLabelOption(new LabelId("refund"))],
            [new AssistTeamOption(new TeamId("billing"), "Billing team")]);
        fixture.Model.Reply("""
            {"labels":[{"label":"refund","confidence":0.9},{"label":"made-up","confidence":0.8},{"label":"refund","confidence":0.4}],
             "team":{"team":"billing","confidence":0.7},"priority":{"priority":"high","confidence":0.6},"rationale":"Duplicate charge."}
            """);

        var result = AssistFixture.Succeeded(await fixture.Assist.TriageAsync(agent, request));

        Assert.AreEqual("refund", result.Value.Labels.Single().Label.Value);
        Assert.AreEqual(0.9, result.Value.Labels.Single().Confidence);
        Assert.AreEqual(new AssistTeamSuggestion(new TeamId("billing"), 0.7), result.Value.Team);
        Assert.AreEqual(new AssistPrioritySuggestion(ConversationPriority.High, 0.6), result.Value.Priority);
        Assert.AreEqual(AssistRecording.Recorded, result.Recording);
        Assert.AreEqual("scripted-small", result.Model);
        Assert.AreEqual(AssistThinkingLevel.Low, fixture.ScriptedProvider!.Applied.Last().Request.ThinkingLevel);
        var schema = ((ChatResponseFormatJson)fixture.Model.Requests.Single().Options!.ResponseFormat!).Schema!.Value.GetRawText();
        Assert.Contains("\"enum\":[\"billing\",\"refund\"]", schema, StringComparison.Ordinal);
        Assert.Contains("<label id=\"billing\">Payments</label>", fixture.Model.Transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("<contact", fixture.Model.Transcript, StringComparison.Ordinal);
        var view = (ConversationView)(await fixture.Conversations.GetAsync(fixture.Owner, AssistFixture.C1)).Value!;
        Assert.IsEmpty(view.Labels, "suggestions are never applied");
        Assert.AreEqual(ConversationPriority.None, view.Priority);

        fixture.Model.Reply("""{"labels":[],"team":{"team":"","confidence":0},"priority":{"priority":"urgent","confidence":1.5},"rationale":""}""");
        Assert.AreEqual("failed:InvalidResponse:retryable", AssistFixture.Describe(await fixture.Assist.TriageAsync(agent, request)));
        fixture.Model.Reply("""{"labels":[],"team":{"team":"sales","confidence":0.2},"priority":{"priority":"low","confidence":0.5},"rationale":""}""");
        Assert.AreEqual("failed:InvalidResponse:retryable", AssistFixture.Describe(await fixture.Assist.TriageAsync(agent, request)));
        fixture.Model.Reply("""{"labels":[],"team":{"team":"","confidence":0},"priority":{"priority":"low","confidence":0.5},"rationale":""}""");
        var noPriority = AssistFixture.Succeeded(await fixture.Assist.TriageAsync(agent, new AssistTriageRequest(AssistFixture.C1, request.Labels, request.Teams, suggestPriority: false))).Value;
        Assert.IsNull(noPriority.Priority);
        Assert.IsNull(noPriority.Team);
        Assert.IsNull(noPriority.Rationale);
    }

    [TestMethod]
    public async Task SentimentTranslationAndToneAreValidatedAndNotRecorded()
    {
        using var fixture = await AssistFixture.CreateAsync();
        var agent = await fixture.AgentAsync();

        fixture.Model.Reply("""{"sentiment":"negative","sentiment_confidence":0.8,"intent":"refund_request","intent_confidence":0.75}""");
        var sentiment = AssistFixture.Succeeded(await fixture.Assist.AnalyzeSentimentAsync(agent, new AssistSentimentRequest(AssistFixture.C1, ["refund_request", "question"])));
        Assert.AreEqual(new AssistSentimentAndIntent(AssistSentiment.Negative, 0.8, "refund_request", 0.75), sentiment.Value);
        Assert.AreEqual(AssistRecording.NotApplicable, sentiment.Recording);
        fixture.Model.Reply("""{"sentiment":"negative","sentiment_confidence":0.8,"intent":"other","intent_confidence":0.75}""");
        Assert.AreEqual("failed:InvalidResponse:retryable", AssistFixture.Describe(await fixture.Assist.AnalyzeSentimentAsync(agent, new AssistSentimentRequest(AssistFixture.C1, ["refund_request"]))));

        var message = ((MessageEvent)(await fixture.TimelineAsync()).Events[0].Event.Event.Value!).Id;
        fixture.Model.Reply("""{"text":"Meine Karte wurde doppelt belastet","source_locale":"en"}""");
        var translation = AssistFixture.Succeeded(await fixture.Assist.TranslateAsync(agent, new AssistTranslateRequest(AssistFixture.C1, new HelpCenterLocale("de"), message: message)));
        Assert.AreEqual(new HelpCenterLocale("en"), translation.Value.SourceLocale);
        Assert.Contains("<text_to_translate>" + AssistFixture.CustomerMessage + "</text_to_translate>", fixture.Model.Requests.Last().Messages.Single().Text, StringComparison.Ordinal);
        Assert.AreEqual("refused:NotFound", AssistFixture.Describe(await fixture.Assist.TranslateAsync(agent, new AssistTranslateRequest(AssistFixture.C1, new HelpCenterLocale("de"), message: new MessageId("nope")))));

        fixture.Model.Reply("""{"text":"Hola","source_locale":""}""");
        var text = AssistFixture.Succeeded(await fixture.Assist.TranslateAsync(agent, new AssistTranslateRequest(AssistFixture.C1, new HelpCenterLocale("es"), text: "Hello"))).Value;
        Assert.IsNull(text.SourceLocale);
        Assert.DoesNotContain(AssistFixture.CustomerMessage, fixture.Model.Requests.Last().Messages.Single().Text, StringComparison.Ordinal, "text translation sends no timeline");

        fixture.Model.Reply("""{"text":"We are truly sorry for the trouble."}""");
        var tone = AssistFixture.Succeeded(await fixture.Assist.RewriteToneAsync(agent, new AssistToneRewriteRequest(AssistFixture.C1, "sorry", AssistTone.Apologetic)));
        Assert.AreEqual(AssistTone.Apologetic, tone.Value.Tone);
        Assert.AreEqual(AssistRecording.NotApplicable, tone.Recording);
        Assert.Contains("apologetic", fixture.Model.Requests.Last().Messages.Single().Text, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task DailyQuotaAndRateLimitRefuseBeforeTheModelIsCalled()
    {
        using var fixture = await AssistFixture.CreateAsync(options => options.DefaultDailyTokenLimit = 5_000);
        var agent = await fixture.AgentAsync();
        var quota = (AssistRefused)(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))).Value!;
        Assert.AreEqual(AssistRefusal.QuotaExceeded, quota.Reason, "summary reserves 4096 output tokens plus the prompt estimate");
        Assert.AreEqual(TimeSpan.FromHours(15), quota.RetryAfter);
        Assert.IsEmpty(fixture.Model.Requests);

        var admin = fixture.Owner;
        Assert.AreEqual("ok", AssistFixture.Describe(await fixture.Administration.UpdateSettingsAsync(admin, new AssistSettings(true, 1_000_000, 1), 0)));
        fixture.Model.Reply(SummaryJson);
        Assert.AreEqual("ok:Recorded", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))));
        var limited = (AssistRefused)(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))).Value!;
        Assert.AreEqual(AssistRefusal.RateLimited, limited.Reason);
        Assert.AreEqual(TimeSpan.FromMinutes(1), limited.RetryAfter);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        fixture.Model.Reply(SummaryJson);
        Assert.AreEqual("ok:Recorded", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))));
        Assert.HasCount(2, fixture.Model.Requests);

        Assert.AreEqual("ok", AssistFixture.Describe(await fixture.Administration.UpdateSettingsAsync(admin, new AssistSettings(false, 1_000_000, 100), 1)));
        Assert.AreEqual("refused:Disabled", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))));
    }

    [TestMethod]
    public async Task ProvablyUnsentOrRejectedCallsReleaseTheirReservation()
    {
        using var fixture = await AssistFixture.CreateAsync(options => options.DefaultDailyTokenLimit = 30_000);
        var agent = await fixture.AgentAsync();
        for (var index = 0; index < 5; index++)
            fixture.Model.Enqueue(new ScriptedTurn(Error: new AssistProviderException(AssistProviderFailure.Overloaded, true, 529, tokensMayHaveBeenConsumed: false)));
        for (var index = 0; index < 5; index++)
            Assert.AreEqual("failed:Overloaded:retryable", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))));
        fixture.Model.Enqueue(new ScriptedTurn(Error: new AssistProviderException(AssistProviderFailure.NotConfigured, false, tokensMayHaveBeenConsumed: false)));
        Assert.AreEqual("failed:NotConfigured:final", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))));
        var summary = await fixture.Store.SummarizeAsync(fixture.Scope, fixture.Clock.GetUtcNow().AddHours(-1), fixture.Clock.GetUtcNow().AddHours(1));
        Assert.AreEqual(6, summary.Totals.Single().Failed);
        Assert.AreEqual(0, summary.Totals.Single().ReservedTokens);
        Assert.AreEqual(0, summary.Totals.Single().EstimatedTokens);
        fixture.Model.Reply(SummaryJson);
        Assert.AreEqual("ok:Recorded", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))));
    }

    [TestMethod]
    public async Task AmbiguousFailuresAndMissingUsageAreChargedAtTheEstimate()
    {
        using var fixture = await AssistFixture.CreateAsync(options => options.DefaultDailyTokenLimit = 30_000);
        var agent = await fixture.AgentAsync();
        var request = new AssistSummaryRequest(AssistFixture.C1);

        // A timeout after the request was sent: the provider may have generated (and billed) output.
        fixture.Model.Enqueue(new ScriptedTurn(Error: new AssistProviderException(AssistProviderFailure.Timeout, true)));
        Assert.AreEqual("failed:Timeout:retryable", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        // A transport error and a provider-internal cancellation: ambiguous too.
        fixture.Model.Enqueue(new ScriptedTurn(Error: new HttpRequestException("reset after send")));
        Assert.AreEqual("failed:Network:retryable", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        // A success without usage counts at the estimate rather than zero.
        fixture.Model.Enqueue(new ScriptedTurn(SummaryJson, ChatFinishReason.Stop, Usage: null));
        var noUsage = AssistFixture.Succeeded(await fixture.Assist.SummarizeAsync(agent, request));

        var record = (await fixture.Store.GetUsageAsync(fixture.Scope, noUsage.UsageId))!;
        Assert.AreEqual(AssistUsageState.Completed, record.State);
        Assert.IsTrue(record.Estimated);
        Assert.AreEqual(record.ReservedTokens, record.CountedTokens);
        Assert.AreEqual(record.ReservedTokens, noUsage.Usage.TotalTokens, "the result reports the estimate");
        var summary = await fixture.Store.SummarizeAsync(fixture.Scope, fixture.Clock.GetUtcNow().AddHours(-1), fixture.Clock.GetUtcNow().AddHours(1));
        Assert.AreEqual(2, summary.Totals.Single().Failed);
        var afterThree = summary.Totals.Single().EstimatedTokens;
        Assert.AreEqual(3 * record.ReservedTokens, afterThree, "the three calls saw the same prompt, so the same estimate");
        Assert.AreEqual(0, summary.Totals.Single().ReservedTokens);

        // Caller cancellation while the request is in flight is ambiguous as well: settled at the estimate, then rethrown.
        using var cancellation = new CancellationTokenSource();
        fixture.Model.Enqueue(new ScriptedTurn(Error: new OperationCanceledException(cancellation.Token), OnCall: cancellation.Cancel));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Assist.SummarizeAsync(agent, request, cancellation.Token));
        summary = await fixture.Store.SummarizeAsync(fixture.Scope, fixture.Clock.GetUtcNow().AddHours(-1), fixture.Clock.GetUtcNow().AddHours(1));
        Assert.IsGreaterThanOrEqualTo(afterThree + 4096, summary.Totals.Single().EstimatedTokens, "the cancelled call is charged its estimate (prompt plus 4,096 output tokens)");
        Assert.AreEqual(0, summary.Totals.Single().ReservedTokens);

        // The estimates count against the quota: leave less room than one more estimate and the next call is refused.
        var charged = summary.Totals.Single().EstimatedTokens;
        Assert.AreEqual("ok", AssistFixture.Describe(await fixture.Administration.UpdateSettingsAsync(fixture.Owner, new AssistSettings(true, charged + 4096, 60), 0)));
        Assert.AreEqual("refused:QuotaExceeded", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
    }

    [TestMethod]
    public async Task MaintenanceSweepsAbandonedReservations()
    {
        using var fixture = await AssistFixture.CreateAsync();
        // A process that crashed between reserving and settling leaves a Reserved record behind.
        var crashed = new AssistUsageRecord(Guid.NewGuid(), AssistFeature.ReplyDraft, "scripted-large", new Access.AccessActor(Access.AccessActorKind.User, "agent"),
            AssistFixture.C1, AssistUsageState.Reserved, 7_000, AssistUsage.Zero, fixture.Clock.GetUtcNow(), null);
        Assert.IsInstanceOfType<AssistUsageReserved>((await fixture.Store.ReserveAsync(fixture.Scope, crashed, 1_000_000, 100)).Value);
        using var scope = fixture.Provider.CreateScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<AssistMaintenance>();

        fixture.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.AreEqual(0, await maintenance.RunOnceAsync());
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(1, await maintenance.RunOnceAsync());
        Assert.AreEqual(0, await maintenance.RunOnceAsync());
        var swept = (await fixture.Store.GetUsageAsync(fixture.Scope, crashed.Id))!;
        Assert.AreEqual(AssistUsageState.Failed, swept.State);
        Assert.IsTrue(swept.Estimated);
        Assert.AreEqual(7_000, swept.CountedTokens, "still charged at the estimate");
    }

    [TestMethod]
    public async Task EveryResultCaseIsReachable()
    {
        using var fixture = await AssistFixture.CreateAsync();
        var agent = await fixture.AgentAsync();
        var request = new AssistSummaryRequest(AssistFixture.C1);
        fixture.Model.Reply(SummaryJson);
        Assert.AreEqual("ok:Recorded", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        fixture.Model.Enqueue(new ScriptedTurn("", ChatFinishReason.ContentFilter));
        Assert.AreEqual("refused:ModelRefused", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        fixture.Model.Enqueue(new ScriptedTurn("{\"summary\":\"cut", ChatFinishReason.Length));
        Assert.AreEqual("failed:OutputTruncated:final", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        fixture.Model.Reply("not json");
        Assert.AreEqual("failed:InvalidResponse:retryable", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        fixture.Model.Reply("""{"summary":"ok","key_points":[],"next_step":"","extra":1}""");
        Assert.AreEqual("ok:Recorded", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)), "unknown properties are ignored; required ones validated");
        fixture.Model.Reply("""{"summary":"","key_points":[],"next_step":""}""");
        Assert.AreEqual("failed:InvalidResponse:retryable", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        fixture.Model.Enqueue(new ScriptedTurn(Error: new AssistProviderException(AssistProviderFailure.Authentication, false, 401)));
        Assert.AreEqual("failed:Authentication:final", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        fixture.Model.Enqueue(new ScriptedTurn(Error: new HttpRequestException("down")));
        Assert.AreEqual("failed:Network:retryable", AssistFixture.Describe(await fixture.Assist.SummarizeAsync(agent, request)));
        fixture.Model.Enqueue(new ScriptedTurn(Error: new InvalidOperationException("bug")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Assist.SummarizeAsync(agent, request));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Assist.SummarizeAsync(agent, request, cancelled.Token));

        var summary = await fixture.Store.SummarizeAsync(fixture.Scope, fixture.Clock.GetUtcNow().AddHours(-1), fixture.Clock.GetUtcNow().AddHours(1));
        Assert.AreEqual(0, summary.Totals.Sum(total => total.ReservedTokens), "every reservation was settled");
    }

    [TestMethod]
    public async Task AdministrationRequiresAssistManage()
    {
        using var fixture = await AssistFixture.CreateAsync();
        var agent = await fixture.AgentAsync();
        Assert.AreEqual("refused:Forbidden", AssistFixture.Describe(await fixture.Administration.GetSettingsAsync(agent)));
        Assert.AreEqual("refused:Forbidden", AssistFixture.Describe(await fixture.Administration.UpdateSettingsAsync(agent, new AssistSettings(true, 10, 1), 0)));
        Assert.AreEqual("refused:Forbidden", AssistFixture.Describe(await fixture.Administration.GetUsageAsync(agent, fixture.Clock.GetUtcNow().AddDays(-1), fixture.Clock.GetUtcNow())));

        var defaults = ((TicketValue<StoredAssistSettings>)(await fixture.Administration.GetSettingsAsync(fixture.Owner)).Value!).Value;
        Assert.AreEqual(0, defaults.Revision);
        Assert.AreEqual(2_000_000, defaults.Settings.DailyTokenLimit);
        var settings = new AssistSettings(true, 50_000, 10, new Dictionary<AssistFeature, string> { [AssistFeature.ConversationSummary] = "scripted-small" });
        Assert.AreEqual("refused:Invalid", AssistFixture.Describe(await fixture.Administration.UpdateSettingsAsync(fixture.Owner,
            new AssistSettings(true, 10, 1, new Dictionary<AssistFeature, string> { [AssistFeature.Triage] = "unregistered" }), 0)));
        Assert.AreEqual("ok", AssistFixture.Describe(await fixture.Administration.UpdateSettingsAsync(fixture.Owner, settings, 0)));
        Assert.AreEqual("refused:Conflict:1", AssistFixture.Describe(await fixture.Administration.UpdateSettingsAsync(fixture.Owner, settings, 0)));

        fixture.Model.Reply(SummaryJson);
        Assert.AreEqual("scripted-small", AssistFixture.Succeeded(await fixture.Assist.SummarizeAsync(agent, new AssistSummaryRequest(AssistFixture.C1))).Model, "workspace override wins");
        var usage = ((TicketValue<AssistUsageSummary>)(await fixture.Administration.GetUsageAsync(fixture.Owner, fixture.Clock.GetUtcNow().AddDays(-1), fixture.Clock.GetUtcNow().AddDays(1))).Value!).Value;
        Assert.AreEqual(AssistFeature.ConversationSummary, usage.Totals.Single().Feature);
        Assert.AreEqual("refused:Invalid", AssistFixture.Describe(await fixture.Administration.GetUsageAsync(fixture.Owner, fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow().AddDays(100))));
    }
}
