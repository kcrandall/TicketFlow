using TicketFlow.Core;
using static TicketFlow.Surveys.StoreContracts.SurveyStoreContract;

namespace TicketFlow.Surveys.Tests;

[TestClass]
public sealed class ModelTests
{
    [TestMethod]
    public void TokensAreRandomStrictAndHashed()
    {
        var token = SurveyToken.Create();
        Assert.HasCount(SurveyToken.Length, token.Value);
        Assert.StartsWith(SurveyToken.Prefix, token.Value);
        Assert.AreNotEqual(token, SurveyToken.Create());
        Assert.IsTrue(SurveyToken.TryParse(token.Value, out var parsed));
        Assert.AreEqual(token.Hash(), parsed.Hash());
        Assert.HasCount(64, token.Hash().Hex);
        Assert.DoesNotContain(token.Value[SurveyToken.Prefix.Length..], token.Hash().Hex);
        foreach (var bad in new[] { "", token.Value[..^1], token.Value + "a", "tfs2." + token.Value[5..], token.Value[..^1] + "=", token.Value[..^1] + " " })
            Assert.IsFalse(SurveyToken.TryParse(bad, out _), bad);
        Assert.AreEqual("SurveyToken { <redacted> }", token.ToString());
        Assert.AreEqual("SurveyTokenHash { <redacted> }", token.Hash().ToString());
        Assert.AreEqual(token.Hash(), SurveyTokenHash.FromBytes(token.Hash().ToBytes()));
    }

    [TestMethod]
    public void DerivedTokensAreStablePerInvitationAndKeyed()
    {
        var key = new byte[32];
        key[0] = 1;
        var scope = Testing.TestScopes.Create();
        var id = SurveyInvitationId.New();
        var token = SurveyToken.Derive(key, scope, id);
        Assert.AreEqual(token, SurveyToken.Derive(key, scope, id));
        Assert.IsTrue(SurveyToken.TryParse(token.Value, out _));
        Assert.AreNotEqual(token, SurveyToken.Derive(key, scope, SurveyInvitationId.New()));
        Assert.AreNotEqual(token, SurveyToken.Derive(key, Testing.TestScopes.LevelVariants(scope)[1], id));
        Assert.AreNotEqual(token, SurveyToken.Derive(new byte[32], scope, id));
        Assert.ThrowsExactly<ArgumentException>(() => SurveyToken.Derive(new byte[31], scope, id));
    }

    [TestMethod]
    public void ScalesAndSentiment()
    {
        Assert.IsTrue(SurveyScales.IsValid(SurveyKind.Csat, 1));
        Assert.IsFalse(SurveyScales.IsValid(SurveyKind.Csat, 0));
        Assert.IsTrue(SurveyScales.IsValid(SurveyKind.Nps, 0));
        Assert.IsFalse(SurveyScales.IsValid(SurveyKind.Nps, 11));
        CollectionAssert.AreEqual(new[] { -1, -1, 0, 1, 1 }, Enumerable.Range(1, 5).Select(score => SurveyScales.Sentiment(SurveyKind.Csat, score)).ToArray());
        CollectionAssert.AreEqual(new[] { -1, -1, -1, -1, -1, -1, -1, 0, 0, 1, 1 }, Enumerable.Range(0, 11).Select(score => SurveyScales.Sentiment(SurveyKind.Nps, score)).ToArray());
        var row = new SurveyAggregateRow(SurveyKind.Nps, DateTimeOffset.UnixEpoch, null, 10, 5, 3, 2, 80);
        Assert.AreEqual(30.0, row.NpsScore);
        Assert.IsNull(row.CsatPercent);
        Assert.AreEqual(8.0, row.AverageScore);
    }

    [TestMethod]
    public void DefinitionsAndPoliciesAreBounded()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SurveyTriggerPolicy(TimeSpan.FromDays(8), TimeSpan.Zero, TimeSpan.FromHours(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.FromDays(366), TimeSpan.FromHours(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(59)));
        Assert.ThrowsExactly<ArgumentException>(() => new SurveyDefinition(new SurveyId("s"), "S", SurveyKind.Csat, new SurveyLocale("fr"), [new SurveyQuestion(English, "Q")], Policy));
        Assert.ThrowsExactly<ArgumentException>(() => new SurveyQuestion(English, new string('q', 513)));
        Assert.AreEqual("pt-BR", new SurveyLocale("PT-br").Value);
        var survey = SampleSurvey();
        Assert.AreEqual("How did we do?", survey.QuestionFor(new SurveyLocale("ja")).Text);
        Assert.AreEqual("Wie war es?", survey.QuestionFor(new SurveyLocale("de")).Text);
        Assert.ThrowsExactly<ArgumentException>(() => new SurveyResponseQuery(Now, Now.AddDays(367)));
        Assert.ThrowsExactly<ArgumentException>(() => new SurveyAggregateQuery(SurveyKind.Csat, Now, Now));
    }

    [TestMethod]
    public void InvitationStateInvariants()
    {
        var invitation = SampleInvitation();
        Assert.ThrowsExactly<ArgumentException>(() => new SurveyInvitation(invitation.Id, invitation.Survey, invitation.Kind, invitation.Conversation, invitation.Contact, null, null, null,
            English, Now, Now, SurveyInvitationState.Sent));
        Assert.ThrowsExactly<ArgumentException>(() => invitation.MarkSent(), "Sent requires a token.");
        var claimed = invitation.Claim(Now.AddMinutes(5));
        Assert.AreEqual((SurveyInvitationState.Sending, 1), (claimed.State, claimed.Attempts));
        var sent = claimed.Issue(SurveyToken.Create().Hash(), Now.AddHours(1).AddTicks(7), TimeSpan.FromHours(1)).MarkSent();
        Assert.AreEqual(0, sent.IssuedAt!.Value.UtcTicks % 10, "Times are truncated to microseconds.");
        Assert.IsNull(sent.LeaseUntil);
        Assert.IsTrue(sent.AcceptsResponses(Now.AddHours(1).AddMinutes(1)));
        Assert.IsFalse(sent.AcceptsResponses(Now.AddHours(2).AddTicks(7)));
        var retry = sent.Retry(Now.AddDays(1));
        Assert.AreEqual(sent.TokenHash, retry.TokenHash, "Retries keep the token.");
        Assert.IsTrue(retry.AcceptsResponses(Now.AddHours(1).AddMinutes(1)));
        Assert.IsFalse(sent.Close(SurveyInvitationState.Cancelled).AcceptsResponses(Now.AddHours(1).AddMinutes(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sent.Close(SurveyInvitationState.Responded));
        Assert.ThrowsExactly<ArgumentException>(() => new SurveyInvitation(invitation.Id, invitation.Survey, invitation.Kind, invitation.Conversation, invitation.Contact, null, null, null,
            English, Now, Now, SurveyInvitationState.Sending));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SurveyResponse.For(invitation, 6, null, Now));
        Assert.ThrowsExactly<ArgumentException>(() => SurveyResponse.For(invitation, 5, "   ", Now));
        Assert.AreEqual("SurveyInvitation { <redacted> }", invitation.ToString());
    }

    [TestMethod]
    public void QuestionCodecRoundTripsAndRejectsUnknownShapes()
    {
        var questions = SampleSurvey().Questions;
        Assert.IsTrue(questions.SequenceEqual(SurveyJsonCodec.DecodeQuestions(SurveyJsonCodec.EncodeQuestions(questions))));
        Assert.ThrowsExactly<FormatException>(() => SurveyJsonCodec.DecodeQuestions("""{"v":2,"items":[]}"""));
        Assert.ThrowsExactly<FormatException>(() => SurveyJsonCodec.DecodeQuestions("""{"v":1,"items":[{"locale":"en","text":"t","x":1}]}"""));
    }

    [TestMethod]
    public void BucketsAreUtcCalendarUnits()
    {
        var query = new SurveyAggregateQuery(SurveyKind.Csat, Now, Now.AddDays(30), SurveyTimeBucket.Week);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), query.BucketStart(new DateTimeOffset(2026, 3, 8, 23, 59, 0, TimeSpan.Zero)));
        Assert.AreEqual(new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero), query.BucketStart(new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero)));
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), new SurveyAggregateQuery(SurveyKind.Csat, Now, Now.AddDays(1), SurveyTimeBucket.Month).BucketStart(Now.AddHours(5)));
        Assert.AreEqual(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), new SurveyAggregateQuery(SurveyKind.Csat, Now, Now.AddDays(1), SurveyTimeBucket.Day).BucketStart(new DateTimeOffset(2026, 3, 2, 3, 0, 0, TimeSpan.FromHours(2))));
        Assert.AreEqual(Now, new SurveyAggregateQuery(SurveyKind.Csat, Now, Now.AddDays(1)).BucketStart(Now.AddHours(5)));
    }

    [TestMethod]
    public void PermissionsAreCatalogued()
    {
        CollectionAssert.AreEqual(new[] { "survey:manage", "survey:read" }, SurveyPermissions.Definitions.Select(definition => definition.Permission.Value).ToArray());
        Assert.AreEqual(Access.AccessPermissions.ReportView, SurveyPermissions.ReportView);
        Assert.AreEqual("survey.comment", SurveyFieldPaths.Comment.Value);
    }
}
