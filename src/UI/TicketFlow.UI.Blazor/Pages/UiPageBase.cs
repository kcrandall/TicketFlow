using Microsoft.AspNetCore.Components;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.UI.Blazor.Infrastructure;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Pages;

/// <summary>Base class of UI pages: the cascaded session plus helpers for optional services and display lookups.</summary>
public abstract class UiPageBase : ComponentBase
{
    private readonly Dictionary<ContactId, string> contactNames = [];
    private Dictionary<InboxId, StoredInbox>? inboxById;
    private Dictionary<TeamId, TeamSummary>? teamById;

    [CascadingParameter] public TicketFlowUISession Session { get; set; } = default!;

    protected DateTimeOffset Now { get; } = TimeProvider.System.GetUtcNow();

    protected PrincipalRef Actor => Session.Principal;

    protected CancellationToken Aborted => Session.Aborted;

    protected T? Service<T>() where T : class => Session.Service<T>();

    protected string Url(string path, params (string Key, string? Value)[] query) => Session.Url(path, query);

    protected string? Query(string key) => Session.Query(key);

    /// <summary>Inboxes the viewer can read (first 100), loaded by <see cref="LoadWorkspaceAsync"/>.</summary>
    protected IReadOnlyList<StoredInbox> Inboxes { get; private set; } = [];

    /// <summary>Teams the viewer can read (first 100), loaded by <see cref="LoadWorkspaceAsync"/>.</summary>
    protected IReadOnlyList<TeamSummary> Teams { get; private set; } = [];

    protected async Task LoadWorkspaceAsync()
    {
        var inboxService = Service<IInboxService>();
        var teamService = Service<ITeamService>();
        var inboxTask = inboxService?.ListAsync(Actor, new WorkspacePageRequest(100), Aborted);
        var teamTask = teamService?.ListAsync(Actor, new WorkspacePageRequest(100), Aborted);
        if (inboxTask is not null && teamTask is not null)
            await Task.WhenAll(inboxTask, teamTask);
        if (inboxTask is not null && await inboxTask is WorkspaceSucceeded<WorkspacePage<StoredInbox>> inboxPage)
            SetInboxes(inboxPage.Value.Items);
        if (teamTask is not null && await teamTask is WorkspaceSucceeded<WorkspacePage<TeamSummary>> teamPage)
            SetTeams(teamPage.Value.Items);
    }

    protected async Task LoadInboxesAsync()
    {
        if (Service<IInboxService>() is { } inboxes && await inboxes.ListAsync(Actor, new WorkspacePageRequest(100), Aborted) is WorkspaceSucceeded<WorkspacePage<StoredInbox>> inboxPage)
            SetInboxes(inboxPage.Value.Items);
    }

    protected async Task LoadTeamsAsync()
    {
        if (Service<ITeamService>() is { } teams && await teams.ListAsync(Actor, new WorkspacePageRequest(100), Aborted) is WorkspaceSucceeded<WorkspacePage<TeamSummary>> teamPage)
            SetTeams(teamPage.Value.Items);
    }

    private void SetInboxes(IReadOnlyList<StoredInbox> items)
    {
        Inboxes = items;
        var map = new Dictionary<InboxId, StoredInbox>(items.Count);
        for (var i = 0; i < items.Count; i++)
            map[items[i].Inbox.Id] = items[i];
        inboxById = map;
    }

    private void SetTeams(IReadOnlyList<TeamSummary> items)
    {
        Teams = items;
        var map = new Dictionary<TeamId, TeamSummary>(items.Count);
        for (var i = 0; i < items.Count; i++)
            map[items[i].Id] = items[i];
        teamById = map;
    }

    protected string InboxName(InboxId inbox) => inboxById is not null && inboxById.TryGetValue(inbox, out var found) ? found.Inbox.Name : inbox.Value;

    protected string InboxChannel(InboxId inbox) => inboxById is not null && inboxById.TryGetValue(inbox, out var found) ? found.Inbox.Channel.Kind.Value : "api";

    protected string TeamName(TeamId? team) => team is not { } id ? "No team" : teamById is not null && teamById.TryGetValue(id, out var found) ? found.Name : id.Value;

    protected void SeedContact(ContactView view) => contactNames[view.Id] = UiText.ContactName(view);

    /// <summary>
    /// The open backlog from reporting (current open conversations matching <paramref name="filter"/>), or <see langword="null"/> when
    /// reporting is not registered or the viewer has no report access. Counts come from the store, never from loading pages of rows.
    /// </summary>
    protected async Task<long?> OpenBacklogAsync(TicketFlow.Reporting.ReportFilter? filter = null)
    {
        if (Service<TicketFlow.Reporting.IReportingService>() is not { } reporting || !Session.CanAny(AccessPermissions.ReportView, TicketFlow.Reporting.ReportPermissions.ViewOwn))
            return null;
        var (_, to) = UiFormat.Range(null, null, Now);
        return await reporting.GetOverviewAsync(Actor, new TicketFlow.Reporting.ReportQuery(to.AddDays(-1), to, filter), Aborted)
            is TicketFlow.Reporting.ReportSucceeded<TicketFlow.Reporting.ReportOverview> overview ? overview.Value.OpenBacklog : null;
    }

    protected string ContactName(ContactId contact) => contactNames.TryGetValue(contact, out var name) ? name : "Contact " + Short(contact.Value);

    /// <summary>Loads contact display names through the authorized contact service (masked or forbidden contacts keep a placeholder).</summary>
    protected async Task LoadContactsAsync(IEnumerable<ContactId> contacts)
    {
        if (Service<IContactService>() is not { } service)
            return;
        // One batched, authorized read for every contact on the page (IContactService.GetManyAsync).
        foreach (var chunk in contacts.Distinct().Where(contact => !contactNames.ContainsKey(contact)).Chunk(IContactService.MaximumBatch))
            foreach (var view in await service.GetManyAsync(Actor, chunk, Aborted))
                contactNames[view.Id] = UiText.ContactName(view);
    }

    /// <summary>Set when a page's fresh, per-request authorization (for example an Access listing) refused the viewer.</summary>
    protected bool Denied { get; set; }

    /// <summary>
    /// Runs an authorized Access listing through <see cref="IAccessAdministrator"/> (a fresh <c>access:manage</c> decision per request, never
    /// the cached navigation snapshot). A refusal sets <see cref="Denied"/> and returns <see langword="null"/>.
    /// </summary>
    protected async Task<T?> AccessListAsync<T>(Func<IAccessAdministrator, Task<AccessListResult<T>>> list) where T : class
    {
        if (Service<IAccessAdministrator>() is not { } administrator)
        {
            Denied = true;
            return null;
        }
        switch (await list(administrator))
        {
            case AccessListed<T> listed:
                return listed.Value;
            case AccessChangeRefused:
                Denied = true;
                return null;
        }
        return null;
    }

    protected static string Short(string id) => id.Length > 8 ? id[..8] : id;

    protected static string StatusToken(ConversationStatus status) => UiFormat.Status(status.Kind);
}
