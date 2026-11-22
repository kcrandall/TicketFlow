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
        Assert.IsNull(retrying.TokenHash);
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
}
