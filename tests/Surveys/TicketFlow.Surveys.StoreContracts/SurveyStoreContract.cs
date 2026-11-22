using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Surveys.StoreContracts;

/// <summary>
/// Behavioral contract every survey store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the same
/// storage. Each verification uses a fresh scope.
/// </summary>
public static class SurveyStoreContract
{
    public static readonly SurveyLocale English = new("en");
    public static readonly DateTimeOffset Now = new(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);
    public static readonly SurveyTriggerPolicy Policy = new(TimeSpan.FromMinutes(30), TimeSpan.FromDays(7), TimeSpan.FromDays(3));

    public static TicketScope NewScope() => TestScopes.Create(workspaceId: "sv-" + Guid.NewGuid().ToString("N"));

    public static string Describe<T>(SurveyWriteResult<T> result, Func<T, long> revision) => result switch
    {
        SurveyWriteApplied<T> applied => $"applied:{revision(applied.Value)}",
        SurveyWriteNotFound => "not-found",
        SurveyWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
        SurveyWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static string S(SurveyWriteResult<StoredSurvey> result) => Describe(result, stored => stored.Revision);
    public static string I(SurveyWriteResult<StoredInvitation> result) => Describe(result, stored => stored.Revision);

    public static string C(InvitationCreateResult result) => result switch
    {
        InvitationCreated created => "created",
        InvitationDuplicate duplicate => "duplicate",
        InvitationCoolingDown cooling => "cooling",
        InvitationRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static string R(ResponseRecordResult result) => result switch
    {
        ResponseRecorded => "recorded",
        ResponseAlreadyRecorded => "already",
        ResponseInvitationNotFound => "not-found",
        ResponseInvitationExpired => "expired",
    };

    public static SurveyDefinition SampleSurvey(string id = "csat", SurveyKind kind = SurveyKind.Csat, string? inbox = null, bool enabled = true, SurveyTriggerPolicy? policy = null) =>
        new(new SurveyId(id), "Survey " + id, kind, English, [new SurveyQuestion(English, "How did we do?", "Tell us more"), new SurveyQuestion(new SurveyLocale("de"), "Wie war es?")],
            policy ?? Policy, inbox is null ? null : new InboxId(inbox), enabled);

    public static SurveyInvitation SampleInvitation(string survey = "csat", string conversation = "c1", string contact = "k1", DateTimeOffset? createdAt = null,
        SurveyKind kind = SurveyKind.Csat, string? inbox = "i0", string? team = "t0", string? agent = "u0") =>
        new(SurveyInvitationId.New(), new SurveyId(survey), kind, new ConversationId(conversation), new ContactId(contact),
            inbox is null ? null : new InboxId(inbox), team is null ? null : new TeamId(team), agent is null ? null : new UserId(agent),
            English, createdAt ?? Now, (createdAt ?? Now).AddMinutes(30));

    public static async Task<StoredInvitation> CreateSentAsync(ISurveyStore store, TicketScope scope, SurveyInvitation invitation, SurveyToken token, DateTimeOffset sentAt, TimeSpan? lifetime = null)
    {
        var created = await store.CreateInvitationAsync(scope, invitation, TimeSpan.Zero);
        Assert.AreEqual("created", C(created));
        var sent = invitation.MarkSent(token.Hash(), sentAt, lifetime ?? TimeSpan.FromDays(3));
        var updated = await store.UpdateInvitationAsync(scope, sent, 1);
        Assert.AreEqual("applied:2", I(updated));
        return ((SurveyWriteApplied<StoredInvitation>)updated.Value!).Value;
    }

    public static async Task VerifySurveysAsync(ISurveyStore primary, ISurveyStore competing)
    {
        var scope = NewScope();
        var survey = SampleSurvey(inbox: "i0");
        Assert.IsNull(await primary.GetSurveyAsync(scope, survey.Id));
        Assert.AreEqual("not-found", S(await primary.UpdateSurveyAsync(scope, survey, 1)));
        Assert.AreEqual("applied:1", S(await primary.CreateSurveyAsync(scope, survey)));
        Assert.AreEqual("conflict:1", S(await competing.CreateSurveyAsync(scope, survey)));
        var stored = (await competing.GetSurveyAsync(scope, survey.Id))!;
        Assert.AreEqual(survey, stored.Survey);
        Assert.AreEqual(scope, stored.Scope);
        var disabled = SampleSurvey(enabled: false, policy: new SurveyTriggerPolicy(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromHours(1)));
        Assert.AreEqual("applied:2", S(await competing.UpdateSurveyAsync(scope, disabled, 1)));
        Assert.AreEqual("conflict:2", S(await primary.UpdateSurveyAsync(scope, survey, 1)));
        Assert.AreEqual(disabled, (await primary.GetSurveyAsync(scope, survey.Id))!.Survey);
        Assert.AreEqual("applied:1", S(await primary.CreateSurveyAsync(scope, SampleSurvey("a-nps", SurveyKind.Nps))));
        CollectionAssert.AreEqual(new[] { "a-nps", "csat" }, (await competing.ListSurveysAsync(scope)).Select(item => item.Survey.Id.Value).ToArray());
        var outcomes = await Task.WhenAll(primary.UpdateSurveyAsync(scope, survey, 2), competing.UpdateSurveyAsync(scope, disabled, 2));
        CollectionAssert.AreEquivalent(new[] { "applied:3", "conflict:3" }, outcomes.Select(S).ToArray());
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetSurveyAsync(other, survey.Id));
            Assert.IsEmpty(await primary.ListSurveysAsync(other));
            Assert.AreEqual("not-found", S(await primary.DeleteSurveyAsync(other, survey.Id, 3)));
        }
        Assert.AreEqual("created", C(await primary.CreateInvitationAsync(scope, SampleInvitation(), TimeSpan.Zero)));
        Assert.AreEqual("rejected:InUse", S(await primary.DeleteSurveyAsync(scope, survey.Id, 3)));
        Assert.AreEqual("conflict:1", S(await primary.DeleteSurveyAsync(scope, new SurveyId("a-nps"), 2)));
        Assert.AreEqual("applied:1", S(await competing.DeleteSurveyAsync(scope, new SurveyId("a-nps"), 1)));
        Assert.IsNull(await primary.GetSurveyAsync(scope, new SurveyId("a-nps")));
    }

    public static async Task VerifySurveyCapacityAsync(ISurveyStore store)
    {
        var scope = NewScope();
        for (var index = 0; index < SurveyLimits.MaximumSurveysPerWorkspace; index++)
            Assert.AreEqual("applied:1", S(await store.CreateSurveyAsync(scope, SampleSurvey($"s{index:D3}"))));
        Assert.AreEqual("rejected:CapacityExceeded", S(await store.CreateSurveyAsync(scope, SampleSurvey("overflow"))));
    }

    public static async Task VerifyInvitationsAsync(ISurveyStore primary, ISurveyStore competing)
    {
        var scope = NewScope();
        var first = SampleInvitation();
        Assert.AreEqual("created", C(await primary.CreateInvitationAsync(scope, first, TimeSpan.FromDays(7))));
        var stored = (await competing.GetInvitationAsync(scope, first.Id))!;
        Assert.AreEqual(1, stored.Revision);
        Assert.AreEqual(first.Conversation, stored.Invitation.Conversation);
        Assert.AreEqual(first.SendAfter, stored.Invitation.SendAfter);
        Assert.AreEqual(SurveyInvitationState.Pending, stored.Invitation.State);
        Assert.IsNull(stored.Invitation.TokenHash);

        var replay = SampleInvitation();
        Assert.AreEqual("duplicate", C(await competing.CreateInvitationAsync(scope, replay, TimeSpan.FromDays(7))));
        Assert.IsNull(await primary.GetInvitationAsync(scope, replay.Id));
        Assert.AreEqual("cooling", C(await competing.CreateInvitationAsync(scope, SampleInvitation(conversation: "c2", createdAt: Now.AddDays(6)), TimeSpan.FromDays(7))));
        Assert.AreEqual("created", C(await competing.CreateInvitationAsync(scope, SampleInvitation(conversation: "c3", createdAt: Now.AddDays(8)), TimeSpan.FromDays(7))));
        Assert.AreEqual("created", C(await competing.CreateInvitationAsync(scope, SampleInvitation(survey: "nps", conversation: "c2", kind: SurveyKind.Nps, createdAt: Now.AddDays(1)), TimeSpan.FromDays(7))));
        Assert.AreEqual("created", C(await competing.CreateInvitationAsync(scope, SampleInvitation(conversation: "c4", contact: "k2", createdAt: Now.AddDays(1)), TimeSpan.FromDays(7))));
        Assert.AreEqual("created", C(await competing.CreateInvitationAsync(scope, SampleInvitation(conversation: "c5", contact: "k2", createdAt: Now.AddDays(1)), TimeSpan.Zero)));

        // Concurrent invitations for one contact: the cooldown cannot be raced.
        var racing = await Task.WhenAll(
            primary.CreateInvitationAsync(scope, SampleInvitation(conversation: "r1", contact: "k9", createdAt: Now.AddMinutes(1)), TimeSpan.FromDays(1)),
            competing.CreateInvitationAsync(scope, SampleInvitation(conversation: "r2", contact: "k9", createdAt: Now.AddMinutes(1)), TimeSpan.FromDays(1)));
        CollectionAssert.AreEquivalent(new[] { "created", "cooling" }, racing.Select(C).ToArray());

        // Due listing is ordered by send-after and bounded.
        var due = await primary.ListDueInvitationsAsync(scope, Now.AddDays(1).AddHours(1), 10);
        Assert.IsTrue(due.All(item => item.Invitation.SendAfter <= Now.AddDays(1).AddHours(1) && item.Invitation.State == SurveyInvitationState.Pending));
        Assert.AreEqual(first.Id, due[0].Invitation.Id);
        Assert.HasCount(5, due);
        Assert.HasCount(2, await primary.ListDueInvitationsAsync(scope, Now.AddDays(1).AddHours(1), 2));
        Assert.HasCount(2, await primary.ListDueInvitationsAsync(scope, Now.AddHours(1), 10));

        // Token issue, lookup and fixed fields.
        var token = SurveyToken.Create();
        var sent = first.MarkSent(token.Hash(), Now.AddHours(1), TimeSpan.FromDays(3));
        Assert.AreEqual("conflict:1", I(await primary.UpdateInvitationAsync(scope, sent, 2)));
        Assert.AreEqual("applied:2", I(await primary.UpdateInvitationAsync(scope, sent, 1)));
        var found = (await competing.FindInvitationByTokenAsync(scope, token.Hash()))!;
        Assert.AreEqual(first.Id, found.Invitation.Id);
        Assert.AreEqual(SurveyInvitationState.Sent, found.Invitation.State);
        Assert.AreEqual(token.Hash(), found.Invitation.TokenHash);
        Assert.AreEqual(Now.AddHours(1), found.Invitation.SentAt);
        Assert.AreEqual(Now.AddHours(1).AddDays(3), found.Invitation.ExpiresAt);
        Assert.AreEqual(1, found.Invitation.Attempts);
        Assert.IsNull(await competing.FindInvitationByTokenAsync(scope, SurveyToken.Create().Hash()));
        Assert.IsNull(await competing.FindInvitationByTokenAsync(TestScopes.LevelVariants(scope)[1], token.Hash()));
        Assert.IsFalse((await primary.ListDueInvitationsAsync(scope, Now.AddDays(30), 100)).Any(item => item.Invitation.Id == first.Id));

        var retried = sent.Retry(Now.AddHours(2), 1);
        Assert.AreEqual("applied:3", I(await competing.UpdateInvitationAsync(scope, retried, 2)));
        Assert.IsNull(await primary.FindInvitationByTokenAsync(scope, token.Hash()), "Retry clears the token.");
        var moved = new SurveyInvitation(first.Id, first.Survey, first.Kind, new ConversationId("other"), first.Contact, first.Inbox, first.Team, first.Agent, first.Locale, first.CreatedAt, first.SendAfter);
        Assert.AreEqual("rejected:InUse", I(await primary.UpdateInvitationAsync(scope, moved, 3)));
        Assert.AreEqual("not-found", I(await primary.UpdateInvitationAsync(scope, SampleInvitation(conversation: "missing"), 1)));
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetInvitationAsync(other, first.Id));
            Assert.IsEmpty(await primary.ListDueInvitationsAsync(other, Now.AddDays(30), 10));
            Assert.AreEqual("not-found", I(await primary.UpdateInvitationAsync(other, retried, 3)));
        }
    }

    public static async Task VerifyResponsesAsync(ISurveyStore primary, ISurveyStore competing)
    {
        var scope = NewScope();
        var pending = SampleInvitation(conversation: "pending");
        Assert.AreEqual("created", C(await primary.CreateInvitationAsync(scope, pending, TimeSpan.Zero)));
        Assert.AreEqual("not-found", R(await primary.RecordResponseAsync(scope, SurveyResponse.For(pending, 5, null, Now.AddHours(1)))));
        Assert.AreEqual("not-found", R(await primary.RecordResponseAsync(scope, SurveyResponse.For(SampleInvitation(conversation: "ghost"), 5, null, Now.AddHours(1)))));

        var invitation = SampleInvitation(conversation: "sent");
        var stored = await CreateSentAsync(primary, scope, invitation, SurveyToken.Create(), Now.AddHours(1));
        var response = SurveyResponse.For(stored.Invitation, 4, "Quick and kind.\nThanks!", Now.AddHours(2));
        var racing = await Task.WhenAll(primary.RecordResponseAsync(scope, response), competing.RecordResponseAsync(scope, SurveyResponse.For(stored.Invitation, 1, null, Now.AddHours(2))));
        CollectionAssert.AreEquivalent(new[] { "recorded", "already" }, racing.Select(R).ToArray());
        var recorded = (await competing.GetResponseAsync(scope, invitation.Id))!.Response;
        Assert.AreEqual(invitation.Id, recorded.Invitation);
        Assert.AreEqual(invitation.Conversation, recorded.Conversation);
        Assert.AreEqual(invitation.Inbox, recorded.Inbox);
        Assert.AreEqual(Now.AddHours(2), recorded.RespondedAt);
        var winner = racing.Select(result => result.Value).OfType<ResponseRecorded>().Single().Response.Response;
        Assert.AreEqual(winner.Score, recorded.Score);
        Assert.AreEqual(winner.Comment, recorded.Comment);
        Assert.AreEqual(SurveyInvitationState.Responded, (await primary.GetInvitationAsync(scope, invitation.Id))!.Invitation.State);
        Assert.AreEqual(3, (await primary.GetInvitationAsync(scope, invitation.Id))!.Revision);
        Assert.AreEqual("already", R(await primary.RecordResponseAsync(scope, response)));

        var expiring = await CreateSentAsync(primary, scope, SampleInvitation(conversation: "late"), SurveyToken.Create(), Now, TimeSpan.FromHours(1));
        Assert.AreEqual("expired", R(await primary.RecordResponseAsync(scope, SurveyResponse.For(expiring.Invitation, 3, null, Now.AddHours(1)))));
        Assert.IsNull(await primary.GetResponseAsync(scope, expiring.Invitation.Id));
        Assert.IsNull(await primary.GetResponseAsync(TestScopes.LevelVariants(scope)[1], invitation.Id));
        Assert.AreEqual("not-found", R(await primary.RecordResponseAsync(TestScopes.LevelVariants(scope)[1], response)));
    }

    /// <summary>Seeds responses across inboxes, teams, agents, kinds and days; returns the scope.</summary>
    public static async Task<(TicketScope Scope, List<SurveyResponse> Responses)> SeedResponsesAsync(ISurveyStore store, int count = 60, int seed = 7, TicketScope? scope = null)
    {
        scope ??= NewScope();
        var random = new Random(seed);
        var responses = new List<SurveyResponse>();
        for (var index = 0; index < count; index++)
        {
            var kind = index % 4 == 3 ? SurveyKind.Nps : SurveyKind.Csat;
            var inbox = random.Next(4) is var i && i == 3 ? null : $"i{i}";
            var team = random.Next(5) is var t && t == 4 ? null : $"t{t}";
            var agent = random.Next(5) is var u && u == 4 ? null : $"u{u}";
            var created = Now.AddDays(random.Next(40)).AddMinutes(random.Next(600));
            var invitation = new SurveyInvitation(SurveyInvitationId.New(), new SurveyId(kind == SurveyKind.Csat ? "csat" : "nps"), kind, new ConversationId($"c{index}"), new ContactId($"k{index}"),
                inbox is null ? null : new InboxId(inbox), team is null ? null : new TeamId(team), agent is null ? null : new UserId(agent), English, created, created);
            var stored = await CreateSentAsync(store, scope, invitation, SurveyToken.Create(), created);
            var score = kind == SurveyKind.Csat ? random.Next(1, 6) : random.Next(0, 11);
            var response = SurveyResponse.For(stored.Invitation, score, random.Next(3) == 0 ? "Comment " + index : null, created.AddMinutes(random.Next(1, 120)));
            Assert.AreEqual("recorded", R(await store.RecordResponseAsync(scope, response)));
            responses.Add(response);
        }
        return (scope, responses);
    }

    public static async Task VerifyListsAndAggregatesAsync(ISurveyStore store)
    {
        var (scope, responses) = await SeedResponsesAsync(store);
        var from = Now.AddDays(-1);
        var to = Now.AddDays(60);
        var all = new List<StoredResponse>();
        SurveyResponseCursor? cursor = null;
        do
        {
            var page = await store.ListResponsesAsync(scope, new SurveyResponseQuery(from, to, limit: 7, after: cursor), AllowAllFilter.Instance);
            Assert.IsLessThanOrEqualTo(7, page.Items.Count);
            all.AddRange(page.Items);
            cursor = page.Next;
        }
        while (cursor is not null);
        CollectionAssert.AreEqual(responses.OrderByDescending(item => item.RespondedAt).ThenByDescending(item => item.Invitation).Select(item => item.Invitation).ToArray(),
            all.Select(item => item.Response.Invitation).ToArray());
        Assert.IsTrue(all.All(item => item.Scope == scope));
        var narrow = await store.ListResponsesAsync(scope, new SurveyResponseQuery(Now.AddDays(10), Now.AddDays(20), new SurveyId("nps"), 200), AllowAllFilter.Instance);
        CollectionAssert.AreEquivalent(responses.Where(item => item.Survey.Value == "nps" && item.RespondedAt >= Now.AddDays(10) && item.RespondedAt < Now.AddDays(20)).Select(item => item.Invitation).ToArray(),
            narrow.Items.Select(item => item.Response.Invitation).ToArray());
        Assert.IsEmpty((await store.ListResponsesAsync(scope, new SurveyResponseQuery(from, to), DenyAllFilter.Instance)).Items);
        Assert.IsEmpty((await store.ListResponsesAsync(TestScopes.LevelVariants(scope)[1], new SurveyResponseQuery(from, to), AllowAllFilter.Instance)).Items);

        AccessFilter[] filters =
        [
            AllowAllFilter.Instance,
            new InboxInFilter([new InboxId("i0"), new InboxId("i2")]),
            new NotFilter(new TeamInFilter([new TeamId("t1")])),
            new OrFilter(new AssigneeIsFilter(new UserId("u3")), new HasLabelFilter(new LabelId("vip"))),
            new NotFilter(new HasLabelFilter(new LabelId("vip"))),
            new AndFilter(new ScopeFilter(scope), new OrFilter(new AttributeEqualsFilter("tier", "vip"), new InboxInFilter([new InboxId("i1")]))),
            UnknownFilter.Instance,
        ];
        foreach (var filter in filters)
        {
            var visible = responses.Where(item => AccessFilterEvaluator.Matches(filter, SurveyResources.Response(scope, item))).ToList();
            var listed = await store.ListResponsesAsync(scope, new SurveyResponseQuery(from, to, limit: 200), filter);
            CollectionAssert.AreEquivalent(visible.Select(item => item.Invitation).ToArray(), listed.Items.Select(item => item.Response.Invitation).ToArray(), filter.GetType().Name);

            foreach (var kind in new[] { SurveyKind.Csat, SurveyKind.Nps })
            foreach (var bucket in Enum.GetValues<SurveyTimeBucket>())
            foreach (var dimension in Enum.GetValues<SurveyDimension>())
            {
                var query = new SurveyAggregateQuery(kind, from, to, bucket, dimension);
                var expected = visible.Where(item => item.Kind == kind)
                    .GroupBy(item => (query.BucketStart(item.RespondedAt), query.GroupKey(item)))
                    .OrderBy(group => group.Key.Item1).ThenBy(group => group.Key.Item2, StringComparer.Ordinal)
                    .Select(group => new SurveyAggregateRow(kind, group.Key.Item1, group.Key.Item2, group.LongCount(),
                        group.LongCount(item => SurveyScales.Sentiment(kind, item.Score) > 0), group.LongCount(item => SurveyScales.Sentiment(kind, item.Score) == 0),
                        group.LongCount(item => SurveyScales.Sentiment(kind, item.Score) < 0), group.Sum(item => (long)item.Score)))
                    .ToArray();
                var actual = await store.AggregateAsync(scope, query, filter);
                Assert.IsFalse(actual.Truncated);
                CollectionAssert.AreEqual(expected, actual.Rows.ToArray(), $"{filter.GetType().Name} {kind} {bucket} {dimension}");
            }
        }
        var single = await store.AggregateAsync(scope, new SurveyAggregateQuery(SurveyKind.Csat, from, to, survey: new SurveyId("nps")), AllowAllFilter.Instance);
        Assert.IsEmpty(single.Rows);
    }

    public static async Task VerifyAggregateTruncationAsync(ISurveyStore store)
    {
        var scope = NewScope();
        for (var index = 0; index < SurveyAggregateQuery.MaximumRows + 2; index++)
        {
            var invitation = new SurveyInvitation(SurveyInvitationId.New(), new SurveyId("csat"), SurveyKind.Csat, new ConversationId($"c{index}"), new ContactId($"k{index}"),
                null, null, new UserId($"agent-{index:D5}"), English, Now, Now);
            var stored = await CreateSentAsync(store, scope, invitation, SurveyToken.Create(), Now);
            Assert.AreEqual("recorded", R(await store.RecordResponseAsync(scope, SurveyResponse.For(stored.Invitation, 5, null, Now.AddMinutes(1)))));
        }
        var result = await store.AggregateAsync(scope, new SurveyAggregateQuery(SurveyKind.Csat, Now, Now.AddDays(1), groupBy: SurveyDimension.Agent), AllowAllFilter.Instance);
        Assert.IsTrue(result.Truncated);
        Assert.HasCount(SurveyAggregateQuery.MaximumRows, result.Rows);
        Assert.AreEqual("agent-00000", result.Rows[0].Group);
    }
}
