using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// Authorized workspace settings for the actor's own workspace (<see cref="PrincipalRef.Scope"/>). Reads need
/// <c>workspace:read</c>; creates and updates need <c>workspace:manage</c>. Time zones must resolve on the host.
/// </summary>
public interface IWorkspaceSettingsService
{
    Task<TicketResult<StoredWorkspaceSettings>> GetAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredWorkspaceSettings>> CreateAsync(PrincipalRef actor, WorkspaceSettings settings, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredWorkspaceSettings>> UpdateAsync(PrincipalRef actor, WorkspaceSettings settings, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorized inbox administration in the actor's workspace. Reads need <c>inbox:read</c> on the inbox; lists are filtered by the
/// access query planner. Create and delete need workspace-level <c>inbox:manage</c>; update accepts an inbox-scoped grant.
/// Inbox agents are inbox-scoped Access grants written through <see cref="IAccessAdministrator"/>, not a parallel list.
/// </summary>
public interface IInboxService
{
    Task<TicketResult<StoredInbox>> GetAsync(PrincipalRef actor, InboxId id, CancellationToken cancellationToken = default);

    Task<TicketResult<WorkspacePage<StoredInbox>>> ListAsync(PrincipalRef actor, WorkspacePageRequest? page = null, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredInbox>> CreateAsync(PrincipalRef actor, Inbox inbox, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredInbox>> UpdateAsync(PrincipalRef actor, Inbox inbox, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredInbox>> DeleteAsync(PrincipalRef actor, InboxId id, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants <paramref name="agent"/> <paramref name="role"/> (default <see cref="BuiltInRoles.Agent"/>) on the inbox. Requires
    /// <c>inbox:manage</c> on the inbox plus everything <see cref="IAccessAdministrator.CreateGrantAsync"/> requires (including
    /// <c>access:manage</c> on the inbox and no escalation). The grant identifier is <see cref="InboxAgentGrants.For"/>, so adding
    /// twice is a <see cref="TicketFailureKind.Conflict"/>.
    /// </summary>
    Task<TicketResult<StoredAccessGrant>> AddAgentAsync(PrincipalRef actor, InboxId inbox, UserId agent, RoleId? role = null, CancellationToken cancellationToken = default);

    /// <summary>Revokes the grant created by <see cref="AddAgentAsync"/> for the same inbox, agent and role.</summary>
    Task<TicketResult<StoredAccessGrant>> RemoveAgentAsync(PrincipalRef actor, InboxId inbox, UserId agent, RoleId? role = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorized team administration in the actor's workspace. Reads need <c>team:read</c> on the team; lists are filtered by the access
/// query planner. Create and delete need workspace-level <c>team:manage</c>; updates and membership changes accept a team-scoped grant.
/// Adding members to a team that holds allow grants additionally requires the actor to hold every permission those grants confer on
/// their targets, so team membership can never escalate privileges beyond the actor's own.
/// </summary>
public interface ITeamService
{
    Task<TicketResult<StoredTeam>> GetAsync(PrincipalRef actor, TeamId id, CancellationToken cancellationToken = default);

    Task<TicketResult<WorkspacePage<TeamSummary>>> ListAsync(PrincipalRef actor, WorkspacePageRequest? page = null, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredTeam>> CreateAsync(PrincipalRef actor, Team team, CancellationToken cancellationToken = default);

    /// <summary>Replaces name, description and members together (revision-fenced).</summary>
    Task<TicketResult<StoredTeam>> UpdateAsync(PrincipalRef actor, Team team, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredTeam>> DeleteAsync(PrincipalRef actor, TeamId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredTeam>> AddMemberAsync(PrincipalRef actor, TeamId team, UserId member, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredTeam>> RemoveMemberAsync(PrincipalRef actor, TeamId team, UserId member, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorized business calendars in the actor's workspace. Reads need <c>workspace:read</c>; writes need <c>calendar:manage</c>.
/// Time zones must resolve on the host.
/// </summary>
public interface IBusinessCalendarService
{
    Task<TicketResult<StoredBusinessCalendar>> GetAsync(PrincipalRef actor, BusinessCalendarId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<StoredBusinessCalendar>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredBusinessCalendar>> CreateAsync(PrincipalRef actor, BusinessCalendar calendar, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredBusinessCalendar>> UpdateAsync(PrincipalRef actor, BusinessCalendar calendar, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredBusinessCalendar>> DeleteAsync(PrincipalRef actor, BusinessCalendarId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a user's current team memberships within one workspace, for building <see cref="UserPrincipal"/> team snapshots. This
/// is an unauthorized, trusted path used while resolving the authenticated principal itself; never expose it to end users directly.
/// </summary>
public interface ITeamMembershipResolver
{
    /// <summary>Teams in <paramref name="scope"/> containing <paramref name="user"/>, ordered by identifier, at most <see cref="WorkspaceLimits.MaximumTeamsPerUser"/>.</summary>
    Task<IReadOnlyList<TeamId>> GetTeamsAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default);
}

public static class TeamMembershipResolverExtensions
{
    /// <summary>
    /// Builds a <see cref="UserPrincipal"/> for an application-authenticated <paramref name="user"/> with the current team snapshot,
    /// so team-subject grants and team conditions apply. The snapshot does not change when membership later changes.
    /// </summary>
    public static async Task<UserPrincipal> ResolveUserPrincipalAsync(this ITeamMembershipResolver resolver, TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(scope);
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        var teams = await resolver.GetTeamsAsync(scope, user, cancellationToken).ConfigureAwait(false);
        return new UserPrincipal(scope, user, teams);
    }
}

/// <summary>Deterministic grant identifiers for inbox agent membership.</summary>
public static class InboxAgentGrants
{
    /// <summary>Identifier prefix of inbox agent grants.</summary>
    public const string Prefix = "tf-inbox-agent-v1-";

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? cachedBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? cachedWriter;

    /// <summary>
    /// <c>tf-inbox-agent-v1-</c> + lowercase hex SHA-256 of the JSON array [workspace, inbox, agent, role]: fixed length, unique per
    /// workspace/inbox/agent/role within an <see cref="AccessPartition"/>, and free of the raw identifiers.
    /// </summary>
    public static GrantId For(WorkspaceId workspace, InboxId inbox, UserId agent, RoleId role)
    {
        if (workspace == default || inbox == default || agent == default || !role.IsInitialized)
            throw new ArgumentException("Workspace, inbox, agent and role identifiers are required.");
        var buffer = cachedBuffer;
        var writer = cachedWriter;
        cachedBuffer = null;
        cachedWriter = null;
        if (buffer is null)
            buffer = new ArrayBufferWriter<byte>(256);
        else
            buffer.ResetWrittenCount();

        if (writer is null)
            writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);

        Span<byte> hash = stackalloc byte[32];
        try
        {
            writer.WriteStartArray();
            writer.WriteStringValue(workspace.Value);
            writer.WriteStringValue(inbox.Value);
            writer.WriteStringValue(agent.Value);
            writer.WriteStringValue(role.Value);
            writer.WriteEndArray();
            writer.Flush();
            SHA256.HashData(buffer.WrittenSpan, hash);
        }
        finally
        {
            if (buffer.Capacity <= 4096)
            {
                buffer.ResetWrittenCount();
                cachedBuffer = buffer;
                cachedWriter = writer;
            }
            else
                writer.Dispose();
        }

        Span<char> idChars = stackalloc char[Prefix.Length + 64];
        Prefix.CopyTo(idChars);
        Convert.TryToHexStringLower(hash, idChars[Prefix.Length..], out _);
        return new GrantId(new string(idChars));
    }

    /// <summary>The grant <see cref="IInboxService.AddAgentAsync"/> creates.</summary>
    public static AccessGrant Create(TicketScope scope, InboxId inbox, UserId agent, RoleId role)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new AccessGrant(For(scope.WorkspaceId, inbox, agent, role), AccessPartition.From(scope), agent, role, new InboxGrantScope(scope.WorkspaceId, inbox));
    }

    /// <summary>True when <paramref name="grant"/> has exactly the shape <see cref="Create"/> produces.</summary>
    public static bool Matches(AccessGrant grant, TicketScope scope, InboxId inbox, UserId agent, RoleId role)
    {
        if (grant is null || scope is null || inbox == default || agent == default || !role.IsInitialized)
            return false;
        return grant.Effect == GrantEffect.Allow
            && grant.Kind == GrantKind.Standard
            && grant.Condition is null
            && grant.ValidFrom is null
            && grant.ValidUntil is null
            && grant.Justification is null
            && grant.Role == role
            && grant.Scope is InboxGrantScope inboxScope && inboxScope.Workspace == scope.WorkspaceId && inboxScope.Inbox == inbox
            && grant.Subject.Value is UserId subjectUser && subjectUser == agent
            && grant.Partition.Contains(scope)
            && grant.Id == For(scope.WorkspaceId, inbox, agent, role);
    }

}
