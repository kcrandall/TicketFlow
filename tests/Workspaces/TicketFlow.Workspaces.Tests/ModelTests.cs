using TicketFlow.Core;
using TicketFlow.Workspaces.StoreContracts;

namespace TicketFlow.Workspaces.Tests;

[TestClass]
public sealed class ModelTests
{
    [TestMethod]
    public void SettingsAreValidatedAndBounded()
    {
        var settings = new WorkspaceSettings("Acme", "zh-Hant-TW", "Eastern Standard Time", [new("csat", true)]);
        Assert.AreEqual("zh-Hant-TW", settings.DefaultLocale);
        Assert.IsTrue(settings.IsEnabled("csat"));
        Assert.IsFalse(settings.IsEnabled("missing"));
        Assert.AreEqual(settings, new WorkspaceSettings("Acme", "zh-Hant-TW", "Eastern Standard Time", [new("csat", true)]));
        Assert.AreNotEqual(settings, new WorkspaceSettings("Acme", "zh-Hant-TW", "Eastern Standard Time", [new("csat", false)]));

        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings(" ", "en", "UTC"));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings(new string('a', 129), "en", "UTC"));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings("Bad\u0001", "en", "UTC"));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings("Lone \ud800", "en", "UTC"));
        foreach (var locale in new[] { "e", "en_US", "en-", "-en", "en--US", "123", "en-US-toolongsubtag", new string('a', 36) })
            Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings("Acme", locale, "UTC"), locale);
        foreach (var zone in new[] { "", " UTC", "UTC ", "../etc/passwd;", new string('a', 65), "Zone\n" })
            Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings("Acme", "en", zone), zone);
        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings("Acme", "en", "UTC", [new("Bad", true)]));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings("Acme", "en", "UTC", [new("a", true), new("a", false)]));
        Assert.ThrowsExactly<ArgumentException>(() => new WorkspaceSettings("Acme", "en", "UTC", Enumerable.Range(0, 65).Select(index => new KeyValuePair<string, bool>($"f{index}", true))));
        Assert.IsTrue(WorkspaceTimeZones.TryResolve("America/New_York", out _));
        Assert.IsFalse(WorkspaceTimeZones.TryResolve("Nowhere/Atlantis", out _));
        Assert.IsFalse(WorkspaceTimeZones.TryResolve(null, out _));
    }

    [TestMethod]
    public void InboxesAreValidatedAndRedacted()
    {
        var inbox = WorkspaceStoreContract.SampleInbox("email");
        Assert.AreEqual("Inbox { <redacted> }", inbox.ToString());
        Assert.AreEqual(new ChannelKind("email"), inbox.Channel.Kind);
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelKind("Email"));
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelKind(new string('a', 65)));
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelBinding(default, "x"));
        Assert.ThrowsExactly<ArgumentException>(() => new ChannelBinding(new ChannelKind("sms"), "bad\u0000"));
        var channel = new ChannelBinding(new ChannelKind("sms"), "twilio-1");
        Assert.ThrowsExactly<ArgumentException>(() => new Inbox(default, "Name", channel));
        Assert.ThrowsExactly<ArgumentException>(() => new Inbox(new InboxId("a"), "Name", channel, calendar: default(BusinessCalendarId)));
        Assert.ThrowsExactly<ArgumentException>(() => new Inbox(new InboxId("a"), "Name", channel, greetingMessage: new string('a', 1025)));
        Assert.ThrowsExactly<ArgumentException>(() => new Inbox(new InboxId("a"), "Name", channel, awayMessage: "Bell\u0007"));
        Assert.ThrowsExactly<ArgumentException>(() => new Inbox(new InboxId("a"), "Name", channel, awayMessage: "\n\t "));
        Assert.AreEqual("Line one\r\n\tLine two", new Inbox(new InboxId("a"), "Name", channel, greetingMessage: "Line one\r\n\tLine two").GreetingMessage);
        Assert.ThrowsExactly<ArgumentException>(() => new Inbox(new InboxId("a"), "Two\nlines", channel));
    }

    [TestMethod]
    public void TeamsAreBounded()
    {
        var team = new Team(new TeamId("t"), "Team", "Desc", [new UserId("a"), new UserId("a"), new UserId("b")]);
        Assert.HasCount(2, team.Members);
        Assert.AreEqual(team, new Team(new TeamId("t"), "Team", "Desc", [new UserId("b"), new UserId("a")]));
        Assert.AreEqual("Team { <redacted> }", team.ToString());
        Assert.HasCount(1000, new Team(new TeamId("t"), "Team", null, Enumerable.Range(0, 1000).Select(index => new UserId($"u{index}"))).Members);
        Assert.ThrowsExactly<ArgumentException>(() => new Team(new TeamId("t"), "Team", null, Enumerable.Range(0, 1001).Select(index => new UserId($"u{index}"))));
        Assert.ThrowsExactly<ArgumentException>(() => new Team(new TeamId("t"), "Team", null, [default]));
        Assert.ThrowsExactly<ArgumentException>(() => new Team(new TeamId("t"), "Team", new string('d', 1025)));
    }

    [TestMethod]
    public void BusinessHoursAreCanonical()
    {
        Assert.ThrowsExactly<ArgumentException>(() => BusinessHours.From(9, 0, 9, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new BusinessHours(TimeSpan.Zero, TimeSpan.FromHours(25)));
        Assert.ThrowsExactly<ArgumentException>(() => new BusinessHours(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)));
        Assert.ThrowsExactly<ArgumentException>(() => new WeeklyBusinessHours(DayOfWeek.Monday, default));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new WeeklyBusinessHours((DayOfWeek)7, BusinessHours.From(9, 0, 10, 0)));

        var calendar = new BusinessCalendar(new BusinessCalendarId("c"), "C", "UTC",
            [new(DayOfWeek.Monday, BusinessHours.From(13, 0, 17, 0)), new(DayOfWeek.Monday, BusinessHours.From(9, 0, 13, 0)), new(DayOfWeek.Friday, BusinessHours.From(9, 0, 10, 0))]);
        CollectionAssert.AreEqual(new[] { BusinessHours.From(9, 0, 17, 0) }, calendar.HoursFor(DayOfWeek.Monday).ToArray());
        Assert.AreEqual(calendar, new BusinessCalendar(new BusinessCalendarId("c"), "C", "UTC",
            [new(DayOfWeek.Friday, BusinessHours.From(9, 0, 10, 0)), new(DayOfWeek.Monday, BusinessHours.From(9, 0, 17, 0))]));
        Assert.AreEqual("09:00-17:00", calendar.HoursFor(DayOfWeek.Monday)[0].ToString());
        Assert.ThrowsExactly<ArgumentException>(() => new BusinessCalendar(new BusinessCalendarId("c"), "C", "UTC",
            [new(DayOfWeek.Monday, BusinessHours.From(9, 0, 13, 0)), new(DayOfWeek.Monday, BusinessHours.From(12, 0, 17, 0))]));
        Assert.ThrowsExactly<ArgumentException>(() => new BusinessCalendar(new BusinessCalendarId("c"), "C", "UTC",
            Enumerable.Range(0, 9).Select(hour => new WeeklyBusinessHours(DayOfWeek.Monday, BusinessHours.From(hour * 2, 0, hour * 2 + 1, 0)))));
        Assert.ThrowsExactly<ArgumentException>(() => new BusinessCalendar(new BusinessCalendarId("c"), "C", "UTC", null,
            [CalendarDateOverride.Holiday(new(2026, 1, 1)), CalendarDateOverride.Holiday(new(2026, 1, 1))]));
        Assert.ThrowsExactly<ArgumentException>(() => new BusinessCalendar(new BusinessCalendarId("c"), "C", "UTC", null,
            Enumerable.Range(0, 1025).Select(day => CalendarDateOverride.Holiday(new DateOnly(2026, 1, 1).AddDays(day)))));
        Assert.IsTrue(CalendarDateOverride.Holiday(new(2026, 1, 1)).IsClosed);
    }

    [TestMethod]
    public void CalendarCodecRoundTripsAndRejectsMalformedInput()
    {
        var calendar = WorkspaceStoreContract.SampleCalendar("office");
        var encoded = BusinessCalendarCodec.EncodeSchedule(calendar);
        Assert.AreEqual(calendar, BusinessCalendarCodec.DecodeSchedule(calendar.Id, calendar.Name, calendar.TimeZoneId, encoded));
        var empty = new BusinessCalendar(new BusinessCalendarId("e"), "E", "UTC");
        Assert.AreEqual("""{"v":1,"week":[],"overrides":[]}""", BusinessCalendarCodec.EncodeSchedule(empty));

        string[] malformed =
        [
            "", "null", "[]", """{"v":2,"week":[],"overrides":[]}""", """{"v":1,"week":[]}""", """{"v":1,"week":[],"overrides":[],"x":1}""",
            """{"v":1,"week":[{"d":7,"s":0,"e":60}],"overrides":[]}""", """{"v":1,"week":[{"d":1,"s":60,"e":0}],"overrides":[]}""",
            """{"v":1,"week":[{"d":1,"s":0,"e":1500}],"overrides":[]}""", """{"v":1,"week":[{"d":1,"s":0,"e":60,"z":1}],"overrides":[]}""",
            """{"v":1,"week":[],"overrides":[{"date":"2026-13-01","hours":[]}]}""", """{"v":1,"week":[],"overrides":[{"date":"2026-01-01"}]}""",
            // Valid but non-canonical: unmerged touching spans and unordered weekdays.
            """{"v":1,"week":[{"d":1,"s":0,"e":60},{"d":1,"s":60,"e":120}],"overrides":[]}""",
            """{"v":1,"week":[{"d":2,"s":0,"e":60},{"d":1,"s":0,"e":60}],"overrides":[]}""",
            """{"v":1,"v":1,"week":[],"overrides":[]}""",
        ];
        foreach (var value in malformed)
            Assert.ThrowsExactly<FormatException>(() => BusinessCalendarCodec.DecodeSchedule(calendar.Id, calendar.Name, calendar.TimeZoneId, value), value);
    }

    [TestMethod]
    public void InboxAgentGrantIdentifiersAreDeterministicAndOpaque()
    {
        var scope = Testing.TestScopes.Create();
        var first = InboxAgentGrants.For(scope.WorkspaceId, new InboxId("email"), new UserId("ada"), Access.BuiltInRoles.Agent.Id);
        Assert.AreEqual(first, InboxAgentGrants.For(scope.WorkspaceId, new InboxId("email"), new UserId("ada"), Access.BuiltInRoles.Agent.Id));
        Assert.AreNotEqual(first, InboxAgentGrants.For(scope.WorkspaceId, new InboxId("emai"), new UserId("lada"), Access.BuiltInRoles.Agent.Id));
        Assert.AreNotEqual(first, InboxAgentGrants.For(new WorkspaceId("other"), new InboxId("email"), new UserId("ada"), Access.BuiltInRoles.Agent.Id));
        Assert.AreNotEqual(first, InboxAgentGrants.For(scope.WorkspaceId, new InboxId("email"), new UserId("ada"), Access.BuiltInRoles.Viewer.Id));
        Assert.StartsWith(InboxAgentGrants.Prefix, first.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("ada", first.Value, StringComparison.Ordinal);
        Assert.HasCount(InboxAgentGrants.Prefix.Length + 64, first.Value);
        var longId = InboxAgentGrants.For(new WorkspaceId(Testing.TestScopes.LongIdentifier(1)), new InboxId(Testing.TestScopes.LongIdentifier(2)), new UserId(Testing.TestScopes.LongIdentifier(3)), Access.BuiltInRoles.Agent.Id);
        Assert.HasCount(InboxAgentGrants.Prefix.Length + 64, longId.Value);
    }
}
