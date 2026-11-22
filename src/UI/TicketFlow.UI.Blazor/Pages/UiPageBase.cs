using Microsoft.AspNetCore.Components;
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
        if (Service<IInboxService>() is { } inboxes && await inboxes.ListAsync(Actor, new WorkspacePageRequest(100), Aborted) is WorkspaceSucceeded<WorkspacePage<StoredInbox>> inboxPage)
            Inboxes = inboxPage.Value.Items;
        if (Service<ITeamService>() is { } teams && await teams.ListAsync(Actor, new WorkspacePageRequest(100), Aborted) is WorkspaceSucceeded<WorkspacePage<TeamSummary>> teamPage)
            Teams = teamPage.Value.Items;
    }

    protected string InboxName(InboxId inbox) => Inboxes.FirstOrDefault(item => item.Inbox.Id == inbox)?.Inbox.Name ?? inbox.Value;

    protected string InboxChannel(InboxId inbox) => Inboxes.FirstOrDefault(item => item.Inbox.Id == inbox)?.Inbox.Channel.Kind.Value ?? "api";

    protected string TeamName(TeamId? team) => team is not { } id ? "No team" : Teams.FirstOrDefault(item => item.Id == id)?.Name ?? id.Value;

    protected string ContactName(ContactId contact) => contactNames.TryGetValue(contact, out var name) ? name : "Contact " + Short(contact.Value);

    /// <summary>Loads contact display names through the authorized contact service (masked or forbidden contacts keep a placeholder).</summary>
    protected async Task LoadContactsAsync(IEnumerable<ContactId> contacts)
    {
        if (Service<IContactService>() is not { } service)
            return;
        foreach (var contact in contacts.Distinct().Where(contact => !contactNames.ContainsKey(contact)))
            if (await service.GetAsync(Actor, contact, Aborted) is ContactApplied<ContactView> applied)
                contactNames[contact] = UiText.ContactName(applied.Value);
    }

    protected static string Short(string id) => id.Length > 8 ? id[..8] : id;

    protected static string StatusToken(ConversationStatus status) => UiFormat.Status(status.Kind);
}
