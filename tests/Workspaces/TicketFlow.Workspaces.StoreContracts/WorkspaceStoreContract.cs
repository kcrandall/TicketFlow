using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Workspaces.StoreContracts;

/// <summary>
/// Behavioral contract every workspace store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the
/// same storage (for example two data sources), so fencing is verified across clients.
/// </summary>
public static class WorkspaceStoreContract
{
    public static string Describe<T>(WorkspaceWriteResult<T> result, Func<T, long> revision) => result switch
    {
        WorkspaceWriteApplied<T> applied => $"applied:{revision(applied.Value)}",
        WorkspaceWriteNotFound => "not-found",
        WorkspaceWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
        WorkspaceWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    private static string S(WorkspaceWriteResult<StoredWorkspaceSettings> result) => Describe(result, stored => stored.Revision);
    private static string I(WorkspaceWriteResult<StoredInbox> result) => Describe(result, stored => stored.Revision);
    private static string T(WorkspaceWriteResult<StoredTeam> result) => Describe(result, stored => stored.Revision);
    private static string C(WorkspaceWriteResult<StoredBusinessCalendar> result) => Describe(result, stored => stored.Revision);

    public static Inbox SampleInbox(string id, string name = "Support", BusinessCalendarId? calendar = null) => new(
        new InboxId(id), name, new ChannelBinding(new ChannelKind("email"), "mailbox-1"), calendar, new SlaPolicyId("sla-gold"),
        new RoutingPolicyId("round-robin"), enabled: true, greetingMessage: "Hello!\nWe reply within a day.", awayMessage: "We are closed.");

    public static BusinessCalendar SampleCalendar(string id, string timeZone = "America/New_York") => new(
        new BusinessCalendarId(id), "Office hours", timeZone,
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }
            .Select(day => new WeeklyBusinessHours(day, BusinessHours.From(9, 0, 17, 0))),
         new WeeklyBusinessHours(DayOfWeek.Saturday, BusinessHours.From(22, 0, 24, 0)),
         new WeeklyBusinessHours(DayOfWeek.Sunday, BusinessHours.From(0, 0, 6, 30))],
        [CalendarDateOverride.Holiday(new(2026, 12, 25), "Christmas"), new CalendarDateOverride(new(2026, 12, 24), [BusinessHours.From(9, 0, 12, 0)])]);

    public static async Task VerifySettingsAsync(IWorkspaceSettingsStore primary, IWorkspaceSettingsStore competing)
    {
        var scope = TestScopes.Create();
        var settings = new WorkspaceSettings("Acme support", "en-US", "America/New_York", [new("csat", true), new("ai-assist", false)]);
        Assert.IsNull(await primary.GetAsync(scope));
        Assert.AreEqual("not-found", S(await primary.UpdateAsync(scope, settings, 1)));
        Assert.AreEqual("applied:1", S(await primary.CreateAsync(scope, settings)));
        Assert.AreEqual("conflict:1", S(await competing.CreateAsync(scope, settings)));
        var stored = (await competing.GetAsync(scope))!;
        Assert.AreEqual(settings, stored.Settings);
        Assert.AreEqual(scope, stored.Scope);
        Assert.IsTrue(stored.Settings.IsEnabled("csat"));
        Assert.IsFalse(stored.Settings.IsEnabled("ai-assist"));

        var renamed = new WorkspaceSettings("Acme help", "pt-BR", "Europe/Lisbon");
        Assert.AreEqual("applied:2", S(await competing.UpdateAsync(scope, renamed, 1)));
        Assert.AreEqual("conflict:2", S(await primary.UpdateAsync(scope, settings, 1)));
        Assert.AreEqual(renamed, (await primary.GetAsync(scope))!.Settings);

        var outcomes = await Task.WhenAll(primary.UpdateAsync(scope, settings, 2), competing.UpdateAsync(scope, renamed, 2));
        CollectionAssert.AreEquivalent(new[] { "applied:3", "conflict:3" }, outcomes.Select(S).ToArray());

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other));
            Assert.AreEqual("not-found", S(await primary.UpdateAsync(other, settings, 3)));
        }
    }

    public static async Task VerifyInboxesAsync(IInboxStore primary, IInboxStore competing)
    {
        var scope = TestScopes.Create();
        var email = SampleInbox("email");
        Assert.AreEqual("applied:1", I(await primary.CreateAsync(scope, email)));
        Assert.AreEqual("conflict:1", I(await competing.CreateAsync(scope, SampleInbox("email", "Other"))));
        var read = (await competing.GetAsync(scope, email.Id))!;
        AssertInbox(email, read.Inbox);
        Assert.AreEqual(1L, read.Revision);
        Assert.AreEqual(scope, read.Scope);

        var minimal = new Inbox(new InboxId("chat"), "Chat", new ChannelBinding(new ChannelKind("live-chat"), "widget"), enabled: false);
        Assert.AreEqual("applied:1", I(await primary.CreateAsync(scope, minimal)));
        AssertInbox(minimal, (await competing.GetAsync(scope, minimal.Id))!.Inbox);

        var renamed = SampleInbox("email", "Support desk", new BusinessCalendarId("office"));
        Assert.AreEqual("applied:2", I(await competing.UpdateAsync(scope, renamed, 1)));
        Assert.AreEqual("conflict:2", I(await primary.UpdateAsync(scope, email, 1)));
        AssertInbox(renamed, (await primary.GetAsync(scope, email.Id))!.Inbox);
        Assert.AreEqual("not-found", I(await primary.UpdateAsync(scope, SampleInbox("missing"), 1)));

        var outcomes = await Task.WhenAll(primary.UpdateAsync(scope, email, 2), competing.UpdateAsync(scope, renamed, 2));
        CollectionAssert.AreEquivalent(new[] { "applied:3", "conflict:3" }, outcomes.Select(I).ToArray());

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, email.Id));
            Assert.IsEmpty((await primary.ListAsync(other, AllowAllFilter.Instance, new())).Items);
            Assert.AreEqual("not-found", I(await primary.UpdateAsync(other, email, 3)));
            Assert.AreEqual("not-found", I(await primary.DeleteAsync(other, email.Id, 3)));
        }

        Assert.AreEqual("conflict:3", I(await primary.DeleteAsync(scope, email.Id, 1)));
        Assert.AreEqual("applied:3", I(await competing.DeleteAsync(scope, email.Id, 3)));
        Assert.IsNull(await primary.GetAsync(scope, email.Id));
        Assert.AreEqual("not-found", I(await primary.DeleteAsync(scope, email.Id, 3)));
        Assert.AreEqual("applied:1", I(await primary.CreateAsync(scope, email)));

        // Maximum-length identifiers in every scope level and key.
        var longScope = new TicketScope(TestScopes.LongIdentifier(1), TestScopes.LongIdentifier(2), TestScopes.LongIdentifier(3), TestScopes.LongIdentifier(4), new WorkspaceId(TestScopes.LongIdentifier(5)));
        var longInbox = new Inbox(new InboxId(TestScopes.LongIdentifier(6)), "Long", new ChannelBinding(new ChannelKind("email"), TestScopes.LongIdentifier(7)),
            new BusinessCalendarId(TestScopes.LongIdentifier(8)), new SlaPolicyId(TestScopes.LongIdentifier(9)), new RoutingPolicyId(TestScopes.LongIdentifier(10)));
        Assert.AreEqual("applied:1", I(await primary.CreateAsync(longScope, longInbox)));
        AssertInbox(longInbox, (await competing.GetAsync(longScope, longInbox.Id))!.Inbox);
        Assert.HasCount(1, (await competing.ListAsync(longScope, new InboxInFilter([longInbox.Id]), new())).Items);
    }

    public static void AssertInbox(Inbox expected, Inbox actual)
    {
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Channel, actual.Channel);
        Assert.AreEqual(expected.Calendar, actual.Calendar);
        Assert.AreEqual(expected.SlaPolicy, actual.SlaPolicy);
        Assert.AreEqual(expected.RoutingPolicy, actual.RoutingPolicy);
        Assert.AreEqual(expected.Enabled, actual.Enabled);
        Assert.AreEqual(expected.GreetingMessage, actual.GreetingMessage);
        Assert.AreEqual(expected.AwayMessage, actual.AwayMessage);
        Assert.AreEqual(expected, actual);
    }

    /// <summary>Lists apply the access filter exactly like <see cref="AccessFilterEvaluator"/> and page by ordinal identifier.</summary>
    public static async Task VerifyInboxListsAsync(IInboxStore store)
    {
        var scope = TestScopes.Create(workspaceId: "inbox-lists");
        string[] ids = ["a", "B", "b", "c-1", "c-10", "c-2", "z", "é", "中"];
        foreach (var id in ids)
            Assert.AreEqual("applied:1", I(await store.CreateAsync(scope, SampleInbox(id))));
        Assert.AreEqual("applied:1", I(await store.CreateAsync(TestScopes.Create(workspaceId: "inbox-lists-other"), SampleInbox("a"))));
        var ordinal = ids.Order(StringComparer.Ordinal).ToArray();

        var all = await store.ListAsync(scope, AllowAllFilter.Instance, new(200));
        CollectionAssert.AreEqual(ordinal, all.Items.Select(item => item.Inbox.Id.Value).ToArray());
        Assert.IsFalse(all.HasMore);

        var pages = new List<string>();
        string? after = null;
        while (true)
        {
            var page = await store.ListAsync(scope, AllowAllFilter.Instance, new(2, after));
            pages.AddRange(page.Items.Select(item => item.Inbox.Id.Value));
            Assert.IsLessThanOrEqualTo(2, page.Items.Count);
            if (!page.HasMore)
                break;
            after = page.Items[^1].Inbox.Id.Value;
        }
        CollectionAssert.AreEqual(ordinal, pages);

        foreach (var filter in Filters(scope))
        {
            var expected = ordinal.Where(id => AccessFilterEvaluator.Matches(filter, WorkspaceResources.Inbox(scope, new InboxId(id)))).ToArray();
            var actual = (await store.ListAsync(scope, filter, new(200))).Items.Select(item => item.Inbox.Id.Value).ToArray();
            CollectionAssert.AreEqual(expected, actual, $"Filter {filter.GetType().Name} with {filter.NodeCount} nodes.");
        }
    }

    /// <summary>Filters over inbox/team resources covering two- and three-valued nodes.</summary>
    public static IEnumerable<AccessFilter> Filters(TicketScope scope)
    {
        var inboxes = new InboxInFilter([new InboxId("a"), new InboxId("c-1"), new InboxId("中")]);
        var teams = new TeamInFilter([new TeamId("a"), new TeamId("c-1"), new TeamId("中")]);
        var scoped = new ScopeFilter(scope);
        yield return DenyAllFilter.Instance;
        yield return UnknownFilter.Instance;
        yield return scoped;
        yield return new ScopeFilter(TestScopes.Create(workspaceId: "elsewhere"));
        yield return inboxes;
        yield return teams;
        yield return new NotFilter(inboxes);
        yield return new NotFilter(teams);
        yield return new AndFilter([scoped, new OrFilter([inboxes, teams])]);
        yield return new AndFilter([scoped, new NotFilter(new InboxInFilter([new InboxId("b")])), new NotFilter(new TeamInFilter([new TeamId("z")]))]);
        yield return new HasLabelFilter(new LabelId("vip"));
        yield return new NotFilter(new HasLabelFilter(new LabelId("vip")));
        yield return new OrFilter([inboxes, new AttributeEqualsFilter("tier", "gold")]);
        yield return new AndFilter([teams, new NotFilter(new AttributeInFilter("tier", ["gold", "silver"]))]);
        yield return new AssigneeIsFilter(new UserId("ada"));
        yield return new NotFilter(new AssigneeIsFilter(new UserId("ada")));
        yield return new OrFilter([new NotFilter(new AssigneeIsFilter(new UserId("ada"))), UnknownFilter.Instance]);
    }

    public static async Task VerifyInboxCapacityAsync(IInboxStore store)
    {
        var scope = TestScopes.Create(workspaceId: "inbox-capacity");
        for (var index = 0; index < WorkspaceLimits.MaximumInboxesPerWorkspace; index++)
            Assert.AreEqual("applied:1", I(await store.CreateAsync(scope, SampleInbox($"inbox-{index:D4}"))));
        Assert.AreEqual("rejected:CapacityExceeded", I(await store.CreateAsync(scope, SampleInbox("overflow"))));
        Assert.AreEqual("conflict:1", I(await store.CreateAsync(scope, SampleInbox("inbox-0000"))));
        Assert.AreEqual("applied:1", I(await store.CreateAsync(TestScopes.Create(workspaceId: "inbox-capacity-other"), SampleInbox("overflow"))));
    }

    public static async Task VerifyTeamsAsync(ITeamStore primary, ITeamStore competing)
    {
        var scope = TestScopes.Create();
        var ada = new UserId("ada");
        var bob = new UserId("bob");
        var billing = new Team(new TeamId("billing"), "Billing", "Invoices and refunds.\nTier 2.", [ada, bob]);
        Assert.AreEqual("applied:1", T(await primary.CreateAsync(scope, billing)));
        Assert.AreEqual("conflict:1", T(await competing.CreateAsync(scope, new Team(billing.Id, "Other"))));
        var read = (await competing.GetAsync(scope, billing.Id))!;
        Assert.AreEqual(billing, read.Team);
        Assert.AreEqual(scope, read.Scope);

        var empty = new Team(new TeamId("empty"), "Empty");
        Assert.AreEqual("applied:1", T(await primary.CreateAsync(scope, empty)));
        Assert.AreEqual(empty, (await competing.GetAsync(scope, empty.Id))!.Team);
        var sales = new Team(new TeamId("sales"), "Sales", null, [ada]);
        Assert.AreEqual("applied:1", T(await primary.CreateAsync(scope, sales)));

        CollectionAssert.AreEqual(new[] { "billing", "sales" }, (await competing.ListTeamsForUserAsync(scope, ada)).Select(team => team.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "billing" }, (await competing.ListTeamsForUserAsync(scope, bob)).Select(team => team.Value).ToArray());
        Assert.IsEmpty(await competing.ListTeamsForUserAsync(scope, new UserId("nobody")));

        // Membership changes are revisioned and fenced across clients.
        var withoutBob = billing.WithMembers([ada]);
        Assert.AreEqual("applied:2", T(await competing.UpdateAsync(scope, withoutBob, 1)));
        Assert.AreEqual("conflict:2", T(await primary.UpdateAsync(scope, billing, 1)));
        Assert.AreEqual(withoutBob, (await primary.GetAsync(scope, billing.Id))!.Team);
        Assert.IsEmpty(await primary.ListTeamsForUserAsync(scope, bob));
        Assert.AreEqual("not-found", T(await primary.UpdateAsync(scope, new Team(new TeamId("missing"), "Missing"), 1)));

        var outcomes = await Task.WhenAll(primary.UpdateAsync(scope, billing.WithMembers([bob]), 2), competing.UpdateAsync(scope, billing.WithMembers([ada, bob]), 2));
        CollectionAssert.AreEquivalent(new[] { "applied:3", "conflict:3" }, outcomes.Select(T).ToArray());

        var summaries = await competing.ListAsync(scope, AllowAllFilter.Instance, new());
        CollectionAssert.AreEqual(new[] { "billing", "empty", "sales" }, summaries.Items.Select(team => team.Id.Value).ToArray());
        var summary = summaries.Items[0];
        Assert.AreEqual((await primary.GetAsync(scope, billing.Id))!.Team.Members.Count, summary.MemberCount);
        Assert.AreEqual("Billing", summary.Name);
        Assert.AreEqual(billing.Description, summary.Description);
        Assert.AreEqual(3L, summary.Revision);
        Assert.AreEqual(scope, summary.Scope);
        CollectionAssert.AreEqual(new[] { "sales" }, (await competing.ListAsync(scope, new TeamInFilter([sales.Id]), new())).Items.Select(team => team.Id.Value).ToArray());
        Assert.IsEmpty((await competing.ListAsync(scope, new InboxInFilter([new InboxId("billing")]), new())).Items);

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, billing.Id));
            Assert.IsEmpty(await primary.ListTeamsForUserAsync(other, ada));
            Assert.IsEmpty((await primary.ListAsync(other, AllowAllFilter.Instance, new())).Items);
            Assert.AreEqual("not-found", T(await primary.UpdateAsync(other, billing, 3)));
            Assert.AreEqual("not-found", T(await primary.DeleteAsync(other, billing.Id, 3)));
        }

        Assert.AreEqual("conflict:3", T(await primary.DeleteAsync(scope, billing.Id, 1)));
        var removed = await competing.DeleteAsync(scope, billing.Id, 3);
        Assert.AreEqual("applied:3", T(removed));
        Assert.IsNull(await primary.GetAsync(scope, billing.Id));
        CollectionAssert.AreEqual(new[] { "sales" }, (await primary.ListTeamsForUserAsync(scope, ada)).Select(team => team.Value).ToArray());
        Assert.AreEqual("applied:1", T(await primary.CreateAsync(scope, billing)));
        Assert.AreEqual(billing, (await primary.GetAsync(scope, billing.Id))!.Team);

        var longScope = new TicketScope(TestScopes.LongIdentifier(11), TestScopes.LongIdentifier(12), TestScopes.LongIdentifier(13), TestScopes.LongIdentifier(14), new WorkspaceId(TestScopes.LongIdentifier(15)));
        var longTeam = new Team(new TeamId(TestScopes.LongIdentifier(16)), "Long", null, [new UserId(TestScopes.LongIdentifier(17)), new UserId(TestScopes.LongIdentifier(18))]);
        Assert.AreEqual("applied:1", T(await primary.CreateAsync(longScope, longTeam)));
        Assert.AreEqual(longTeam, (await competing.GetAsync(longScope, longTeam.Id))!.Team);
        CollectionAssert.AreEqual(new[] { longTeam.Id }, (await competing.ListTeamsForUserAsync(longScope, new UserId(TestScopes.LongIdentifier(17)))).ToArray());
    }

    public static async Task VerifyTeamListsAsync(ITeamStore store)
    {
        var scope = TestScopes.Create(workspaceId: "team-lists");
        string[] ids = ["a", "B", "b", "c-1", "c-10", "c-2", "z", "é", "中"];
        foreach (var id in ids)
            Assert.AreEqual("applied:1", T(await store.CreateAsync(scope, new Team(new TeamId(id), "Team"))));
        var ordinal = ids.Order(StringComparer.Ordinal).ToArray();
        foreach (var filter in Filters(scope))
        {
            var expected = ordinal.Where(id => AccessFilterEvaluator.Matches(filter, WorkspaceResources.Team(scope, new TeamId(id)))).ToArray();
            var actual = (await store.ListAsync(scope, filter, new(200))).Items.Select(item => item.Id.Value).ToArray();
            CollectionAssert.AreEqual(expected, actual, $"Filter {filter.GetType().Name} with {filter.NodeCount} nodes.");
        }
        var first = await store.ListAsync(scope, AllowAllFilter.Instance, new(4));
        Assert.IsTrue(first.HasMore);
        var second = await store.ListAsync(scope, AllowAllFilter.Instance, new(200, first.Items[^1].Id.Value));
        CollectionAssert.AreEqual(ordinal, first.Items.Concat(second.Items).Select(item => item.Id.Value).ToArray());
        Assert.IsFalse(second.HasMore);
    }

    /// <summary>A user belongs to at most <see cref="WorkspaceLimits.MaximumTeamsPerUser"/> teams per workspace, on create and update.</summary>
    public static async Task VerifyTeamMembershipLimitAsync(ITeamStore store)
    {
        var scope = TestScopes.Create(workspaceId: "team-membership-limit");
        var busy = new UserId("busy");
        var other = new UserId("other");
        for (var index = 0; index < WorkspaceLimits.MaximumTeamsPerUser; index++)
            Assert.AreEqual("applied:1", T(await store.CreateAsync(scope, new Team(new TeamId($"team-{index:D3}"), "Team", null, [busy]))));
        Assert.HasCount(WorkspaceLimits.MaximumTeamsPerUser, await store.ListTeamsForUserAsync(scope, busy));
        Assert.AreEqual("rejected:TeamMembershipLimitExceeded", T(await store.CreateAsync(scope, new Team(new TeamId("overflow"), "Team", null, [other, busy]))));
        Assert.AreEqual("applied:1", T(await store.CreateAsync(scope, new Team(new TeamId("overflow"), "Team", null, [other]))));
        Assert.AreEqual("rejected:TeamMembershipLimitExceeded", T(await store.UpdateAsync(scope, new Team(new TeamId("overflow"), "Team", null, [other, busy]), 1)));
        // Updating an existing membership keeps the user at the limit and is allowed.
        Assert.AreEqual("applied:2", T(await store.UpdateAsync(scope, new Team(new TeamId("team-000"), "Renamed", null, [busy, other]), 1)));
        Assert.AreEqual("applied:1", T(await store.CreateAsync(TestScopes.Create(workspaceId: "team-membership-limit-other"), new Team(new TeamId("overflow"), "Team", null, [busy]))));
    }

    /// <summary>Concurrent writes that each add the same user's last free team slot: at most the limit applies.</summary>
    public static async Task VerifyConcurrentTeamMembershipLimitAsync(ITeamStore primary, ITeamStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "team-membership-race");
        var busy = new UserId("busy");
        for (var index = 0; index < WorkspaceLimits.MaximumTeamsPerUser - 1; index++)
            Assert.AreEqual("applied:1", T(await primary.CreateAsync(scope, new Team(new TeamId($"team-{index:D3}"), "Team", null, [busy]))));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            (index % 2 == 0 ? primary : competing).CreateAsync(scope, new Team(new TeamId($"race-{index}"), "Team", null, [busy]))));
        Assert.AreEqual(1, outcomes.Count(outcome => outcome.Value is WorkspaceWriteApplied<StoredTeam>));
        Assert.AreEqual(7, outcomes.Count(outcome => outcome.Value is WorkspaceWriteRejected { Reason: WorkspaceWriteRejection.TeamMembershipLimitExceeded }));
        Assert.HasCount(WorkspaceLimits.MaximumTeamsPerUser, await competing.ListTeamsForUserAsync(scope, busy));
    }

    public static async Task VerifyTeamCapacityAsync(ITeamStore store)
    {
        var scope = TestScopes.Create(workspaceId: "team-capacity");
        for (var index = 0; index < WorkspaceLimits.MaximumTeamsPerWorkspace; index++)
            Assert.AreEqual("applied:1", T(await store.CreateAsync(scope, new Team(new TeamId($"team-{index:D4}"), "Team"))));
        Assert.AreEqual("rejected:CapacityExceeded", T(await store.CreateAsync(scope, new Team(new TeamId("overflow"), "Team"))));
        Assert.AreEqual("conflict:1", T(await store.CreateAsync(scope, new Team(new TeamId("team-0000"), "Team"))));
    }

    public static async Task VerifyCalendarsAsync(IBusinessCalendarStore primary, IBusinessCalendarStore competing)
    {
        var scope = TestScopes.Create();
        var office = SampleCalendar("office");
        Assert.AreEqual("applied:1", C(await primary.CreateAsync(scope, office)));
        Assert.AreEqual("conflict:1", C(await competing.CreateAsync(scope, office)));
        var read = (await competing.GetAsync(scope, office.Id))!;
        Assert.AreEqual(office, read.Calendar);
        Assert.AreEqual(scope, read.Scope);
        var empty = new BusinessCalendar(new BusinessCalendarId("closed"), "Closed", "UTC");
        Assert.AreEqual("applied:1", C(await primary.CreateAsync(scope, empty)));
        Assert.AreEqual(empty, (await competing.GetAsync(scope, empty.Id))!.Calendar);
        CollectionAssert.AreEqual(new[] { "closed", "office" }, (await competing.ListAsync(scope)).Select(entry => entry.Calendar.Id.Value).ToArray());

        var london = SampleCalendar("office", "Europe/London");
        Assert.AreEqual("applied:2", C(await competing.UpdateAsync(scope, london, 1)));
        Assert.AreEqual("conflict:2", C(await primary.UpdateAsync(scope, office, 1)));
        Assert.AreEqual(london, (await primary.GetAsync(scope, office.Id))!.Calendar);
        Assert.AreEqual("not-found", C(await primary.UpdateAsync(scope, SampleCalendar("missing"), 1)));

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetAsync(other, office.Id));
            Assert.IsEmpty(await primary.ListAsync(other));
            Assert.AreEqual("not-found", C(await primary.UpdateAsync(other, office, 2)));
            Assert.AreEqual("not-found", C(await primary.DeleteAsync(other, office.Id, 2)));
        }

        Assert.AreEqual("conflict:2", C(await primary.DeleteAsync(scope, office.Id, 1)));
        Assert.AreEqual("applied:2", C(await competing.DeleteAsync(scope, office.Id, 2)));
        Assert.IsNull(await primary.GetAsync(scope, office.Id));
        Assert.AreEqual("applied:1", C(await primary.CreateAsync(scope, office)));
    }

    public static async Task VerifyCalendarCapacityAsync(IBusinessCalendarStore store)
    {
        var scope = TestScopes.Create(workspaceId: "calendar-capacity");
        for (var index = 0; index < WorkspaceLimits.MaximumCalendarsPerWorkspace; index++)
            Assert.AreEqual("applied:1", C(await store.CreateAsync(scope, new BusinessCalendar(new BusinessCalendarId($"c-{index:D3}"), "C", "UTC"))));
        Assert.AreEqual("rejected:CapacityExceeded", C(await store.CreateAsync(scope, new BusinessCalendar(new BusinessCalendarId("overflow"), "C", "UTC"))));
        Assert.AreEqual("conflict:1", C(await store.CreateAsync(scope, new BusinessCalendar(new BusinessCalendarId("c-000"), "C", "UTC"))));
    }
}
