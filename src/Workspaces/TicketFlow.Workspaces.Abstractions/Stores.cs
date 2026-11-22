using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// One settings record per workspace scope. Contract: every operation is confined to exactly one <see cref="TicketScope"/>; creates
/// start at revision 1 and conflict when settings exist; updates apply atomically only when the current revision equals the expected
/// revision.
/// </summary>
public interface IWorkspaceSettingsStore
{
    Task<StoredWorkspaceSettings?> GetAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredWorkspaceSettings>> CreateAsync(TicketScope scope, WorkspaceSettings settings, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredWorkspaceSettings>> UpdateAsync(TicketScope scope, WorkspaceSettings settings, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Workspace-scoped inboxes keyed by <see cref="InboxId"/>. Contract: scope-confined; creates start at revision 1; updates and deletes
/// are compare-and-set on revision; at most <see cref="WorkspaceLimits.MaximumInboxesPerWorkspace"/> inboxes per scope, checked
/// atomically with the insert; at most one inbox per scope binds a given <see cref="ChannelBinding"/> (kind and configuration),
/// otherwise creates and updates are rejected with <see cref="WorkspaceWriteRejection.ChannelBindingInUse"/>. <see cref="ListAsync"/> returns exactly the inboxes for which
/// <see cref="AccessFilterEvaluator.Matches"/>(filter, <see cref="WorkspaceResources.Inbox"/>) is true, ordered by
/// identifier (ordinal), applying the filter in the query rather than post-filtering an unbounded result. A store that cannot apply a
/// filter node must return no rows.
/// </summary>
public interface IInboxStore
{
    Task<StoredInbox?> GetAsync(TicketScope scope, InboxId id, CancellationToken cancellationToken = default);

    Task<WorkspacePage<StoredInbox>> ListAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredInbox>> CreateAsync(TicketScope scope, Inbox inbox, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredInbox>> UpdateAsync(TicketScope scope, Inbox inbox, long expectedRevision, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredInbox>> DeleteAsync(TicketScope scope, InboxId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Workspace-scoped teams with members. Contract: scope-confined; creates start at revision 1; updates (which replace name,
/// description and members together) and deletes are compare-and-set on revision; at most
/// <see cref="WorkspaceLimits.MaximumTeamsPerWorkspace"/> teams per scope; a user belongs to at most
/// <see cref="WorkspaceLimits.MaximumTeamsPerUser"/> teams per scope (<see cref="WorkspaceWriteRejection.TeamMembershipLimitExceeded"/>);
/// both limits are checked atomically with the write. <see cref="ListAsync"/> follows the <see cref="IInboxStore.ListAsync"/> filter
/// contract over <see cref="WorkspaceResources.Team"/>.
/// </summary>
public interface ITeamStore
{
    Task<StoredTeam?> GetAsync(TicketScope scope, TeamId id, CancellationToken cancellationToken = default);

    Task<WorkspacePage<TeamSummary>> ListAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken = default);

    /// <summary>Teams in <paramref name="scope"/> that contain <paramref name="user"/>, ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<TeamId>> ListTeamsForUserAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredTeam>> CreateAsync(TicketScope scope, Team team, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredTeam>> UpdateAsync(TicketScope scope, Team team, long expectedRevision, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredTeam>> DeleteAsync(TicketScope scope, TeamId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Workspace-scoped business calendars. Same scope and revision contract as <see cref="IInboxStore"/>, with at most
/// <see cref="WorkspaceLimits.MaximumCalendarsPerWorkspace"/> calendars per scope. Schedules persist through a versioned encoding
/// such as <see cref="BusinessCalendarCodec"/>.
/// </summary>
public interface IBusinessCalendarStore
{
    Task<StoredBusinessCalendar?> GetAsync(TicketScope scope, BusinessCalendarId id, CancellationToken cancellationToken = default);

    /// <summary>Calendars in <paramref name="scope"/> ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredBusinessCalendar>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredBusinessCalendar>> CreateAsync(TicketScope scope, BusinessCalendar calendar, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredBusinessCalendar>> UpdateAsync(TicketScope scope, BusinessCalendar calendar, long expectedRevision, CancellationToken cancellationToken = default);

    Task<WorkspaceWriteResult<StoredBusinessCalendar>> DeleteAsync(TicketScope scope, BusinessCalendarId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>One page of workspace scopes; <see cref="Next"/> is an opaque continuation token (<see langword="null"/> at the end).</summary>
public sealed record WorkspaceScopePage(IReadOnlyList<TicketScope> Scopes, string? Next);

/// <summary>
/// Store-wide enumeration of the workspaces that have settings, for trusted hosted workers that run per-workspace loops (survey dispatch,
/// change-log followers). Unauthorized: never expose it to end users. Contract: every scope with stored settings appears exactly once in
/// a full enumeration taken while no workspace is created or removed; the order is stable but store-defined and <c>after</c> is a token
/// returned by the previous page; at most <c>limit</c> (1-<see cref="MaximumPage"/>) scopes per page.
/// </summary>
public interface IWorkspaceDirectory
{
    public const int MaximumPage = 1000;

    Task<WorkspaceScopePage> ListWorkspacesAsync(string? after, int limit, CancellationToken cancellationToken = default);
}
