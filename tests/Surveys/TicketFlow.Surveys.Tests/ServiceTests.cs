using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.Surveys.StoreContracts.SurveyStoreContract;
using static TicketFlow.Surveys.Tests.SurveyFixture;

namespace TicketFlow.Surveys.Tests;

[TestClass]
public sealed class ServiceTests
{
    [TestMethod]
    public async Task DefinitionsAuthorizeOnTheirInbox()
    {
        using var fixture = Create();
        var manager = await fixture.ManagerAsync();
        var inboxManager = await fixture.UserWithAsync("inbox-manager", new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i0")), SurveyPermissions.Read, SurveyPermissions.Manage);
        var stranger = Testing.TestPrincipals.User(fixture.Scope, "stranger");

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.CreateSurveyAsync(inboxManager, SampleSurvey())));
        Value(await fixture.Service.CreateSurveyAsync(inboxManager, SampleSurvey("inbox", inbox: "i0")));
        Value(await fixture.Service.CreateSurveyAsync(manager, SampleSurvey("workspace", SurveyKind.Nps)));
        CollectionAssert.AreEqual(new[] { "inbox" }, Value(await fixture.Service.ListSurveysAsync(inboxManager)).Select(item => item.Survey.Id.Value).ToArray());
        Assert.HasCount(2, Value(await fixture.Service.ListSurveysAsync(manager)));
        Assert.IsEmpty(Value(await fixture.Service.ListSurveysAsync(stranger)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.GetSurveyAsync(inboxManager, new SurveyId("workspace"))));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UpdateSurveyAsync(inboxManager, SampleSurvey("inbox", inbox: "i9"), 1)), "Moving to an inbox the actor cannot manage.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UpdateSurveyAsync(inboxManager, SampleSurvey("workspace", SurveyKind.Nps, inbox: "i0"), 1)), "Moving from the workspace needs workspace manage.");
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.UpdateSurveyAsync(inboxManager, SampleSurvey("workspace", SurveyKind.Csat, inbox: "i0"), 99)), "Unauthorized source survey must return Forbidden before revision or kind checks.");
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.UpdateSurveyAsync(manager, SampleSurvey("inbox", SurveyKind.Nps, inbox: "i0"), 1)));
        Assert.AreEqual("refused:Conflict:1", Describe(await fixture.Service.UpdateSurveyAsync(manager, SampleSurvey("inbox", inbox: "i0", enabled: false), 5)));
        Value(await fixture.Service.UpdateSurveyAsync(inboxManager, SampleSurvey("inbox", inbox: "i0", enabled: false), 1));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.DeleteSurveyAsync(inboxManager, new SurveyId("workspace"), 1)));
        Value(await fixture.Service.DeleteSurveyAsync(manager, new SurveyId("workspace"), 1));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Service.GetSurveyAsync(manager, new SurveyId("workspace"))));
    }

    [TestMethod]
    public async Task ResponsesAreFilteredMaskedAndAggregated()
    {
        using var fixture = Create();
        var manager = await fixture.ManagerAsync();
        var inboxReader = await fixture.UserWithAsync("inbox-reader", new InboxGrantScope(fixture.Scope.WorkspaceId, new InboxId("i0")), SurveyPermissions.Read, SurveyPermissions.ReportView);
        var stranger = Testing.TestPrincipals.User(fixture.Scope, "stranger");
        await fixture.Store.CreateSurveyAsync(fixture.Scope, SampleSurvey(policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromDays(1))));
        var scores = new[] { ("c1", "i0", 5), ("c2", "i0", 2), ("c3", "i1", 4) };
        foreach (var (conversation, inbox, _) in scores)
            await fixture.Scheduler.HandleResolvedAsync(fixture.Resolved(conversation, "k-" + conversation, inbox));
        await fixture.Dispatcher.DispatchDueAsync(fixture.Scope);
        foreach (var message in fixture.Sender.Sent)
        {
            var score = scores.Single(item => string.Equals(item.Item1, message.Invitation.Conversation.Value, StringComparison.Ordinal)).Item3;
            Assert.AreEqual("accepted", Describe(await fixture.Responder.RespondAsync(fixture.Scope, message.Token.Value, score, "Comment for " + message.Invitation.Conversation.Value)));
        }
        var query = new SurveyResponseQuery(fixture.Clock.GetUtcNow().AddDays(-1), fixture.Clock.GetUtcNow().AddDays(1));
        Assert.HasCount(3, Value(await fixture.Service.ListResponsesAsync(manager, query)).Items);
        var inboxPage = Value(await fixture.Service.ListResponsesAsync(inboxReader, query));
        CollectionAssert.AreEquivalent(new[] { "c1", "c2" }, inboxPage.Items.Select(item => item.Response.Conversation.Value).ToArray());
        Assert.IsTrue(inboxPage.Items.All(item => item.Comment is not null && !item.CommentMasked));
        Assert.IsEmpty(Value(await fixture.Service.ListResponsesAsync(stranger, query)).Items);

        var aggregate = Value(await fixture.Service.AggregateAsync(manager, new SurveyAggregateQuery(SurveyKind.Csat, query.From, query.To, groupBy: SurveyDimension.Inbox)));
        CollectionAssert.AreEqual(new[] { "i0", "i1" }, aggregate.Rows.Select(row => row.Group).ToArray());
        Assert.AreEqual(50.0, aggregate.Rows[0].CsatPercent);
        Assert.AreEqual(100.0, aggregate.Rows[1].CsatPercent);
        var scoped = Value(await fixture.Service.AggregateAsync(inboxReader, new SurveyAggregateQuery(SurveyKind.Csat, query.From, query.To)));
        Assert.AreEqual(2, scoped.Rows.Single().Responses);
        var denied = Value(await fixture.Service.AggregateAsync(stranger, new SurveyAggregateQuery(SurveyKind.Csat, query.From, query.To)));
        Assert.IsEmpty(denied.Rows, "Like every list path, denial yields an empty aggregate.");
        Assert.IsFalse(denied.Truncated);

        var c3 = fixture.Sender.Sent.Single(item => string.Equals(item.Invitation.Conversation.Value, "c3", StringComparison.Ordinal)).Invitation.Id;
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.GetResponseAsync(inboxReader, c3)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.GetInvitationAsync(inboxReader, c3)));
        Assert.AreEqual("Comment for c3", Value(await fixture.Service.GetResponseAsync(manager, c3)).Comment);

        // A field policy masks comments for survey:read unless the grant uses an unmasked role.
        Assert.IsInstanceOfType<StoreApplied<StoredAccessFieldPolicy>>((await fixture.Policies.CreateAsync(fixture.Scope,
            new AccessFieldPolicy(SurveyFieldPaths.Comment, [SurveyPermissions.Read], null, null))).Value);
        var masked = Value(await fixture.Service.GetResponseAsync(manager, c3));
        Assert.IsNull(masked.Comment);
        Assert.IsTrue(masked.CommentMasked);
        Assert.AreEqual("Comment for c3", masked.Response.Comment, "The raw response is still carried for trusted callers; views redact.");
        Assert.IsTrue(Value(await fixture.Service.ListResponsesAsync(manager, query)).Items.All(item => item.Comment is null && item.CommentMasked));
    }
}
