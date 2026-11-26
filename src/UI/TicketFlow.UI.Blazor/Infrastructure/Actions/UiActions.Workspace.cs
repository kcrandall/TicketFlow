using System.Globalization;
using System.Text;
using TicketFlow.Channels;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Infrastructure;

public static partial class UiActions
{
    private static void RegisterWorkspace(Dictionary<string, UiActionHandler> handlers)
    {
        handlers["inbox.create"] = context => WithAsync<IInboxService>(context, async inboxes =>
            UiOutcomes.From(await inboxes.CreateAsync(context.Actor, Inbox(context, context.Text("id") ?? NewId()), context.Aborted),
                stored => UiOutcome.Success("Inbox created", $"inboxes/{stored.Inbox.Id.Value}")));
        handlers["inbox.update"] = context => WithAsync<IInboxService>(context, async inboxes =>
            UiOutcomes.From(await inboxes.UpdateAsync(context.Actor, Inbox(context, PathId(context, "inboxes")), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Inbox saved")));
        handlers["inbox.delete"] = context => WithAsync<IInboxService>(context, async inboxes =>
            UiOutcomes.From(await inboxes.DeleteAsync(context.Actor, new InboxId(PathId(context, "inboxes")), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Inbox deleted", "inboxes")));
        handlers["inbox.agent.add"] = context => WithAsync<IInboxService>(context, async inboxes =>
            UiOutcomes.From(await inboxes.AddAgentAsync(context.Actor, new InboxId(PathId(context, "inboxes")), new UserId(context.Require("user", "User")), null, context.Aborted),
                _ => UiOutcome.Success("Agent added to the inbox")));

        handlers["team.create"] = context => WithAsync<ITeamService>(context, async teams =>
            UiOutcomes.From(await teams.CreateAsync(context.Actor, new Team(new TeamId(context.Text("id") ?? NewId()), context.Require("name", "Name"), context.Text("description"), []), context.Aborted),
                stored => UiOutcome.Success("Team created", $"teams/{stored.Team.Id.Value}")));
        handlers["team.update"] = context => WithAsync<ITeamService>(context, async teams =>
        {
            var id = new TeamId(PathId(context, "teams"));
            if (await teams.GetAsync(context.Actor, id, context.Aborted) is not TicketValue<StoredTeam> current)
                return UiOutcome.NotFound();
            return UiOutcomes.From(await teams.UpdateAsync(context.Actor, new Team(id, context.Require("name", "Name"), context.Text("description"), current.Value.Team.Members),
                context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Team saved"));
        });
        handlers["team.delete"] = context => WithAsync<ITeamService>(context, async teams =>
            UiOutcomes.From(await teams.DeleteAsync(context.Actor, new TeamId(PathId(context, "teams")), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Team deleted", "teams")));
        handlers["team.member.add"] = context => WithAsync<ITeamService>(context, async teams =>
            UiOutcomes.From(await teams.AddMemberAsync(context.Actor, new TeamId(PathId(context, "teams")), new UserId(context.Require("user", "User")), context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success("Member added")));
        handlers["team.member.remove"] = context => WithAsync<ITeamService>(context, async teams =>
            UiOutcomes.From(await teams.RemoveMemberAsync(context.Actor, new TeamId(PathId(context, "teams")), new UserId(context.Require("user", "User")), context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success("Member removed")));

        handlers["calendar.create"] = context => WithAsync<IBusinessCalendarService>(context, async calendars =>
            UiOutcomes.From(await calendars.CreateAsync(context.Actor, Calendar(context), context.Aborted), _ => UiOutcome.Success("Calendar created")));
        handlers["calendar.delete"] = context => WithAsync<IBusinessCalendarService>(context, async calendars =>
            UiOutcomes.From(await calendars.DeleteAsync(context.Actor, new BusinessCalendarId(context.Require("id", "Calendar")), context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success("Calendar deleted")));

        handlers["channel.endpoint.create"] = context => WithAsync<IChannelEndpointService>(context, async endpoints =>
            UiOutcomes.From(await endpoints.CreateAsync(context.Actor, Endpoint(context), context.Aborted), _ => UiOutcome.Success("Channel connected")));
        handlers["channel.endpoint.update"] = context => WithAsync<IChannelEndpointService>(context, async endpoints =>
            UiOutcomes.From(await endpoints.UpdateAsync(context.Actor, Endpoint(context), context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Channel updated")));

        handlers["settings.save"] = context => WithAsync<IWorkspaceSettingsService>(context, async settings =>
        {
            var value = new WorkspaceSettings(context.Require("name", "Name"), context.Require("locale", "Default locale"), context.Require("timezone", "Time zone"),
                context.All("toggle").Select(toggle => new KeyValuePair<string, bool>(toggle, true)));
            var result = context.Revision is { } revision
                ? await settings.UpdateAsync(context.Actor, value, revision, context.Aborted)
                : await settings.CreateAsync(context.Actor, value, context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("Workspace settings saved"));
        });
    }

    private static async Task<UiActionResult> WithAsync<T>(UiActionContext context, Func<T, Task<UiActionResult>> action) where T : class =>
        context.Session.Service<T>() is { } service ? await action(service) : UiOutcome.NotFound();

    private static async Task<UiActionResult> WithAsync<T>(UiActionContext context, Func<T, Task<UiOutcome>> action) where T : class =>
        context.Session.Service<T>() is { } service ? await action(service) : UiOutcome.NotFound();

    internal static string NewId() => Guid.CreateVersion7().ToString("N")[..12];

    private static string PathId(UiActionContext context, string section) =>
        context.Session.Path.Split('/') is [var first, var id] && string.Equals(first, section, StringComparison.Ordinal) ? Uri.UnescapeDataString(id) : throw new UiInputException("The record is missing.");

    private static Inbox Inbox(UiActionContext context, string id) => new(new InboxId(id), context.Require("name", "Name"),
        new ChannelBinding(new ChannelKind(context.Require("kind", "Channel")), context.Require("configuration", "Channel configuration ID")),
        context.Text("calendar") is { } calendar ? new BusinessCalendarId(calendar) : null,
        context.Text("sla") is { } sla ? new SlaPolicyId(sla) : null,
        context.Text("routing") is { } routing ? new RoutingPolicyId(routing) : null,
        context.Flag("enabled"), context.Text("greeting"), context.Text("away"));

    private static ChannelEndpoint Endpoint(UiActionContext context) => new(new ChannelKind(context.Require("kind", "Channel")), context.Require("configuration", "Configuration"),
        new InboxId(context.Require("inbox", "Inbox")), context.Text("address"));

    /// <summary>Builds a weekly calendar (same hours on the selected days) in the codec's canonical form.</summary>
    private static BusinessCalendar Calendar(UiActionContext context)
    {
        static int Minutes(string? value, string label) =>
            TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.Hour * 60 + time.Minute : throw new UiInputException($"{label} must be a time (HH:mm).");
        var start = Minutes(context.Text("start"), "Opens");
        var end = Minutes(context.Text("end"), "Closes");
        if (end <= start)
            throw new UiInputException("Closing time must be after opening time.");
        var days = context.All("days").Select(day => int.TryParse(day, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 6 ? value : -1).Where(day => day >= 0).Distinct().Order().ToList();
        if (days.Count == 0)
            throw new UiInputException("Select at least one day.");
        var json = new StringBuilder("{\"v\":1,\"week\":[");
        json.AppendJoin(',', days.Select(day => $"{{\"d\":{day},\"s\":{start},\"e\":{end}}}"));
        json.Append("],\"overrides\":[]}");
        var name = context.Require("name", "Name");
        return BusinessCalendarCodec.DecodeSchedule(new BusinessCalendarId(NewId()), name, context.Require("timezone", "Time zone"), json.ToString());
    }
}
