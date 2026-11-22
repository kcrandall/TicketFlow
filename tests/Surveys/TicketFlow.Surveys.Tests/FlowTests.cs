using TicketFlow.Core;
using static TicketFlow.Surveys.StoreContracts.SurveyStoreContract;
using static TicketFlow.Surveys.Tests.SurveyFixture;

namespace TicketFlow.Surveys.Tests;

[TestClass]
public sealed class FlowTests
{
    [TestMethod]
    public async Task ResolutionSchedulesDispatchesAndAcceptsOneResponse()
    {
        using var fixture = Create();
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey("workspace-csat"));
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey("inbox-csat", inbox: "i0"));
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey("nps", SurveyKind.Nps));
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey("other-inbox", SurveyKind.Nps, inbox: "i9"));
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey("disabled", SurveyKind.Csat, inbox: "i0", enabled: false));

        var scheduled = await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved(locale: "de"));
        CollectionAssert.AreEqual(new[] { "inbox-csat", "nps" }, scheduled.Created.Select(item => item.Invitation.Survey.Value).ToArray());
        Assert.IsEmpty(scheduled.Skipped);
        var invitation = scheduled.Created[0].Invitation;
        Assert.AreEqual(new SurveyLocale("de"), invitation.Locale);
        Assert.AreEqual(fixture.Clock.GetUtcNow() + Policy.Delay, invitation.SendAfter);

        var replay = await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved(locale: "de"));
        Assert.IsEmpty(replay.Created);
        Assert.IsTrue(replay.Skipped.All(skip => skip.Reason == SurveySkipReason.Duplicate));
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        var cooling = await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved(conversation: "c2"));
        Assert.IsTrue(cooling.Skipped.All(skip => skip.Reason == SurveySkipReason.CoolingDown));
        Assert.HasCount(2, cooling.Skipped);

        Assert.AreEqual(new SurveyDispatchSummary(2, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        Assert.AreEqual(new SurveyDispatchSummary(0, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        var message = fixture.Sender.Sent.Single(item => item.Invitation.Survey.Value == "inbox-csat");
        Assert.AreEqual("Wie war es?", message.Question.Text);
        Assert.AreEqual((1, 5), (message.MinimumScore, message.MaximumScore));
        Assert.AreEqual("SurveyToken { <redacted> }", message.Token.ToString());
        Assert.DoesNotContain(message.Token.Value, message.ToString());
        var stored = (await fixture.Store.GetInvitationAsync(fixture.Scope, invitation.Id))!.Invitation;
        Assert.AreEqual(SurveyInvitationState.Sent, stored.State);
        Assert.AreEqual(message.Token.Hash(), stored.TokenHash);
        Assert.AreEqual(fixture.Clock.GetUtcNow() + Policy.InvitationLifetime, stored.ExpiresAt);

        var token = message.Token.Value;
        var view = (await fixture.Responder.GetInvitationAsync(fixture.Scope, token))!;
        Assert.AreEqual(SurveyKind.Csat, view.Kind);
        Assert.IsFalse(view.Responded);
        Assert.IsNull(await fixture.Responder.GetInvitationAsync(Testing.TestScopes.LevelVariants(fixture.Scope)[1], token));
        Assert.AreEqual("rejected:InvalidScore", Describe(await fixture.Responder.RespondAsync(fixture.Scope, token, 6)));
        Assert.AreEqual("rejected:InvalidComment", Describe(await fixture.Responder.RespondAsync(fixture.Scope, token, 5, new string('x', 2001))));
        Assert.AreEqual("rejected:InvalidComment", Describe(await fixture.Responder.RespondAsync(fixture.Scope, token, 5, "bell\u0007")));
        Assert.AreEqual("rejected:InvalidToken", Describe(await fixture.Responder.RespondAsync(fixture.Scope, "tfs1.not-a-token", 5)));
        Assert.AreEqual("rejected:InvalidToken", Describe(await fixture.Responder.RespondAsync(fixture.Scope, SurveyToken.Create().Value, 5)));
        Assert.AreEqual("rejected:InvalidToken", Describe(await fixture.Responder.RespondAsync(Testing.TestScopes.LevelVariants(fixture.Scope)[1], token, 5)));
        Assert.AreEqual("accepted", Describe(await fixture.Responder.RespondAsync(fixture.Scope, token, 5, "Great!")));
        Assert.AreEqual("duplicate", Describe(await fixture.Responder.RespondAsync(fixture.Scope, token, 5, "Great!")));
        Assert.AreEqual("rejected:AlreadyResponded", Describe(await fixture.Responder.RespondAsync(fixture.Scope, token, 1)));
        Assert.IsTrue((await fixture.Responder.GetInvitationAsync(fixture.Scope, token))!.Responded);

        var nps = fixture.Sender.Sent.Single(item => item.Kind == SurveyKind.Nps).Token.Value;
        fixture.Clock.Advance(Policy.InvitationLifetime);
        Assert.IsNull(await fixture.Responder.GetInvitationAsync(fixture.Scope, nps));
        Assert.AreEqual("rejected:Expired", Describe(await fixture.Responder.RespondAsync(fixture.Scope, nps, 10)));
    }

    [TestMethod]
    public async Task SenderFailuresRetryThenFail()
    {
        using var fixture = Create(options =>
        {
            options.MaximumSendAttempts = 2;
            options.RetryDelay = TimeSpan.FromMinutes(1);
        });
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromHours(1))));
        var invitation = (await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved())).Created.Single().Invitation;
        fixture.Sender.Failures = 5;
        Assert.AreEqual(new SurveyDispatchSummary(0, 1, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        var retrying = (await fixture.Store.GetInvitationAsync(fixture.Scope, invitation.Id))!.Invitation;
        Assert.AreEqual(SurveyInvitationState.Pending, retrying.State);
        Assert.AreEqual(1, retrying.Attempts);
        Assert.IsNotNull(retrying.TokenHash, "The token survives the failed attempt.");
        Assert.AreEqual(new SurveyDispatchSummary(0, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope), "Backoff not yet elapsed.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(new SurveyDispatchSummary(0, 0, 1, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        Assert.AreEqual(SurveyInvitationState.Failed, (await fixture.Store.GetInvitationAsync(fixture.Scope, invitation.Id))!.Invitation.State);
    }

    [TestMethod]
    public async Task DisabledSurveysCancelPendingInvitations()
    {
        using var fixture = Create();
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromHours(1))));
        var invitation = (await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved())).Created.Single().Invitation;
        await fixture.Store.UpdateSurveyAsync(fixture.Scope, SampleSurvey(enabled: false), 1);
        Assert.AreEqual(new SurveyDispatchSummary(0, 0, 0, 1), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        Assert.AreEqual(SurveyInvitationState.Cancelled, (await fixture.Store.GetInvitationAsync(fixture.Scope, invitation.Id))!.Invitation.State);
        Assert.IsEmpty(fixture.Sender.Sent);
        Assert.IsEmpty((await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved(conversation: "c2", contact: "k2"))).Created);
    }

    [TestMethod]
    public async Task AcceptedThenFailingSendKeepsTheDeliveredLinkValid()
    {
        using var fixture = Create(options => options.RetryDelay = TimeSpan.FromMinutes(1));
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(1))));
        var invitation = (await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved())).Created.Single().Invitation;
        fixture.Sender.AcceptThenFail = 1;
        Assert.AreEqual(new SurveyDispatchSummary(0, 1, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        var delivered = fixture.Sender.Sent.Single();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(new SurveyDispatchSummary(1, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        var retried = fixture.Sender.Sent[1];
        Assert.AreEqual(delivered.Token, retried.Token, "The retry re-sends the same link.");
        Assert.AreEqual(delivered.DeliveryKey, retried.DeliveryKey, "The provider can deduplicate the retry.");
        Assert.AreEqual($"tfs-{invitation.Id.Value:N}", retried.DeliveryKey);
        Assert.AreEqual("accepted", Describe(await fixture.Responder.RespondAsync(fixture.Scope, delivered.Token.Value, 4)));
    }

    [TestMethod]
    public async Task ResponsesAreAcceptedWhileARetryIsPending()
    {
        using var fixture = Create(options => options.RetryDelay = TimeSpan.FromHours(1));
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(1))));
        await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved());
        fixture.Sender.AcceptThenFail = 1;
        await fixture.Dispatcher.DispatchDueAsync(fixture.Scope);
        var token = fixture.Sender.Sent.Single().Token.Value;
        Assert.IsNotNull(await fixture.Responder.GetInvitationAsync(fixture.Scope, token));
        Assert.AreEqual("accepted", Describe(await fixture.Responder.RespondAsync(fixture.Scope, token, 5)));
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        Assert.AreEqual(new SurveyDispatchSummary(0, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope), "Responded invitations are not re-sent.");
    }

    [TestMethod]
    public async Task DispatcherStoppingMidSendLeavesTheInvitationDueAfterTheLease()
    {
        using var fixture = Create(options => options.SendLease = TimeSpan.FromMinutes(2));
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(1))));
        var invitation = (await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved())).Created.Single().Invitation;
        using (var stop = new CancellationTokenSource())
        {
            fixture.Sender.OnSend = message =>
            {
                fixture.Sender.Sent.Add(message);   // the provider got it ...
                stop.Cancel();                      // ... and then the host shut down before the dispatcher recorded the outcome
                stop.Token.ThrowIfCancellationRequested();
            };
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Dispatcher.DispatchDueAsync(fixture.Scope, cancellationToken: stop.Token));
        }
        fixture.Sender.OnSend = null;
        var stuck = (await fixture.Store.GetInvitationAsync(fixture.Scope, invitation.Id))!.Invitation;
        Assert.AreEqual(SurveyInvitationState.Sending, stuck.State);
        Assert.AreEqual(new SurveyDispatchSummary(0, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope), "The lease is still held.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(new SurveyDispatchSummary(1, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
        Assert.HasCount(2, fixture.Sender.Sent);
        Assert.AreEqual(fixture.Sender.Sent[0].Token, fixture.Sender.Sent[1].Token);
        Assert.AreEqual(SurveyInvitationState.Sent, (await fixture.Store.GetInvitationAsync(fixture.Scope, invitation.Id))!.Invitation.State);

        // A claim abandoned before the token was issued also becomes due again.
        await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved("c2", "k2"));
        Assert.HasCount(1, await fixture.Store.ClaimDueInvitationsAsync(fixture.Scope, fixture.Clock.GetUtcNow(), TimeSpan.FromMinutes(2), 10));
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.AreEqual(new SurveyDispatchSummary(1, 0, 0, 0), await fixture.Dispatcher.DispatchDueAsync(fixture.Scope));
    }

    [TestMethod]
    public async Task ConcurrentDispatchersSendEachInvitationOnce()
    {
        using var fixture = Create();
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(1))));
        for (var index = 0; index < 50; index++)
            await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved($"c{index}", $"k{index}"));
        var dispatchers = Enumerable.Range(0, 4).Select(_ => new SurveyDispatcher(fixture.Store, fixture.Sender, fixture.Clock,
            Microsoft.Extensions.Options.Options.Create(new SurveyOptions()))).ToList();
        var summaries = await Task.WhenAll(dispatchers.Select(dispatcher => Task.Run(() => dispatcher.DispatchDueAsync(fixture.Scope, 20))));
        await Task.WhenAll(dispatchers.Select(dispatcher => dispatcher.DispatchDueAsync(fixture.Scope, 500)));
        Assert.HasCount(50, fixture.Sender.Sent);
        Assert.AreEqual(50, fixture.Sender.Sent.Select(message => message.Invitation.Id).Distinct().Count());
    }
}
