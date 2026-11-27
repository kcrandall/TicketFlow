using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Identity;

/// <summary>
/// Default <see cref="IStaffIdentityService"/>. A mapping from an external identity to user U makes whoever controls that identity act
/// as U, so linking or unlinking requires <c>staff-identity:manage</c> in the workspace plus
/// <see cref="IAccessAdministrator.CheckCanConferAsync"/> over every grant U holds there (user grants, team grants of U's teams from
/// <see cref="ITeamMembershipResolver"/>, and tenant grants, which require tenant-scoped authority). Never logs subjects.
/// </summary>
public sealed class StaffIdentityService(
    IStaffIdentityStore store,
    IAccessEvaluator evaluator,
    IAccessAdministrator administrator,
    IAccessGrantStore grants,
    ITeamMembershipResolver teams,
    TimeProvider time) : IStaffIdentityService
{
    public async Task<TicketResult<StoredStaffIdentityLink>> LinkAsync(PrincipalRef actor, StaffIdentityRef identity, UserId user, string? justification = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(identity);
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        if (await AuthorizeChangeAsync(actor, scope, user, checkManage: true, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        var now = time.GetUtcNow();
        StaffIdentityAuditEvent auditEvent;
        try
        {
            auditEvent = new StaffIdentityAuditEvent(Guid.CreateVersion7(), now, scope, StaffIdentityAuditKind.Linked, AccessActor.From(actor), user, identity.Provider, justification);
        }
        catch (ArgumentException)
        {
            return Invalid<StoredStaffIdentityLink>("The justification is invalid.");
        }
        return await store.LinkAsync(scope, new StaffIdentityLink(identity, user, now, AccessActor.From(actor)), auditEvent, cancellationToken).ConfigureAwait(false) switch
        {
            StaffIdentityWriteApplied applied => new TicketValue<StoredStaffIdentityLink>(applied.Link),
            StaffIdentityWriteTaken => TicketFailure.Conflict(errors: ["The identity is linked to another user; unlink it first."], code: "taken"),
            StaffIdentityWriteNotFound => TicketFailure.NotFound,
            StaffIdentityWriteRejected => new TicketFailure(TicketFailureKind.Rejected, [$"A user can hold at most {IStaffIdentityStore.MaximumIdentitiesPerUser} identities."]),
        };
    }

    public async Task<TicketResult<UserId>> UnlinkAsync(PrincipalRef actor, StaffIdentityRef identity, string? justification = null, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(identity);
        if (!await PermittedAsync(actor, StaffIdentityPermissions.Manage, new AccessResource(scope), cancellationToken, requireStandardGrant: true).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var existing = await store.FindAsync(scope, identity, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return TicketFailure.NotFound;
        var user = existing.Link.User;
        if (await AuthorizeChangeAsync(actor, scope, user, checkManage: false, cancellationToken).ConfigureAwait(false) is { } refused)
            return refused;
        StaffIdentityAuditEvent auditEvent;
        try
        {
            auditEvent = new StaffIdentityAuditEvent(Guid.CreateVersion7(), time.GetUtcNow(), scope, StaffIdentityAuditKind.Unlinked, AccessActor.From(actor), user, identity.Provider,
                justification);
        }
        catch (ArgumentException)
        {
            return Invalid<UserId>("The justification is invalid.");
        }
        return await store.UnlinkAsync(scope, identity, user, auditEvent, cancellationToken).ConfigureAwait(false) switch
        {
            StaffIdentityWriteApplied => new TicketValue<UserId>(user),
            StaffIdentityWriteTaken => new TicketFailure(TicketFailureKind.Conflict, ["The identity was relinked concurrently; retry."]),
            StaffIdentityWriteNotFound => TicketFailure.NotFound,
            StaffIdentityWriteRejected => new TicketFailure(TicketFailureKind.Rejected, []),
        };
    }

    public async Task<TicketResult<IReadOnlyList<StoredStaffIdentityLink>>> ListAsync(PrincipalRef actor, UserId user, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        if (!await SelfOrManagerAsync(actor, scope, user, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return new TicketValue<IReadOnlyList<StoredStaffIdentityLink>>(await store.ListForUserAsync(scope, user, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StaffProfileView>> GetProfileAsync(PrincipalRef actor, UserId user, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, AccessPermissions.TeamRead, new AccessResource(scope)), cancellationToken).ConfigureAwait(false);
        if (!decision.IsPermitted)
            return TicketFailure.Forbidden;
        var stored = await store.GetProfileAsync(scope, user, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return TicketFailure.NotFound;
        var masked = !IsSelf(actor, user) && decision switch
        {
            AccessAllowed => false,
            AccessMasked mask => mask.Fields.Any(field => field.Field == StaffFieldPaths.NotificationEmail),
            AccessDenied => true,
        };
        return new TicketValue<StaffProfileView>(View(stored, masked));
    }

    public async Task<TicketResult<StaffProfileView>> SetProfileAsync(PrincipalRef actor, StaffProfile profile, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(profile);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!await SelfOrManagerAsync(actor, scope, profile.User, cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var now = time.GetUtcNow();
        var auditEvent = new StaffIdentityAuditEvent(Guid.CreateVersion7(), now, scope, StaffIdentityAuditKind.ProfileChanged, AccessActor.From(actor), profile.User);
        return await store.PutProfileAsync(scope, profile, now, expectedRevision, auditEvent, cancellationToken).ConfigureAwait(false) switch
        {
            StaffIdentityProfileWriteApplied applied => new TicketValue<StaffProfileView>(View(applied.Profile, masked: false)),
            StaffIdentityWriteNotFound => TicketFailure.NotFound,
            StaffIdentityWriteConflict conflict => new TicketFailure(TicketFailureKind.Conflict, [], conflict.CurrentRevision),
        };
    }

    /// <summary>
    /// <c>staff-identity:manage</c> in the workspace and authority to confer every grant <paramref name="user"/> holds there, from
    /// standard grants only (the Access administrator's escalation rule).
    /// </summary>
    private async Task<TicketFailure?> AuthorizeChangeAsync(PrincipalRef actor, TicketScope scope, UserId user, bool checkManage = true, CancellationToken cancellationToken = default)
    {
        if (checkManage && !await PermittedAsync(actor, StaffIdentityPermissions.Manage, new AccessResource(scope), cancellationToken, requireStandardGrant: true).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var userTeams = await teams.GetTeamsAsync(scope, user, cancellationToken).ConfigureAwait(false);
        var subjects = new List<GrantSubject>(userTeams.Count + 1) { user };
        foreach (var team in userTeams)
            subjects.Add(team);
        var held = await grants.ListForSubjectsAsync(AccessPartition.From(scope), scope.WorkspaceId, subjects, cancellationToken).ConfigureAwait(false);
        if (held.Count > 0)
        {
            var conferrals = new GrantConferral[held.Count];
            for (var i = 0; i < held.Count; i++)
                conferrals[i] = new GrantConferral(held[i].Grant);
            if (await administrator.CheckCanConferAsync(actor, conferrals, cancellationToken).ConfigureAwait(false) is TicketFailure)
                return new TicketFailure(TicketFailureKind.Forbidden, ["The user holds grants the actor cannot confer."]);
        }
        if (userTeams.Count > 0
            && await administrator.CheckCanConferTeamFederatedRolesAsync(actor, user, userTeams, cancellationToken).ConfigureAwait(false) is TicketFailure)
            return new TicketFailure(TicketFailureKind.Forbidden, ["The user holds grants the actor cannot confer."]);
        return null;
    }

    private async Task<bool> SelfOrManagerAsync(PrincipalRef actor, TicketScope scope, UserId user, CancellationToken cancellationToken) =>
        IsSelf(actor, user)
            ? await PermittedAsync(actor, AccessPermissions.WorkspaceRead, new AccessResource(scope), cancellationToken).ConfigureAwait(false)
            : await PermittedAsync(actor, StaffIdentityPermissions.Manage, new AccessResource(scope), cancellationToken, requireStandardGrant: true).ConfigureAwait(false);

    private static bool IsSelf(PrincipalRef actor, UserId user) => actor is UserPrincipal principal && principal.UserId == user;

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken, bool requireStandardGrant = false)
    {
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false);
        return decision switch
        {
            AccessAllowed allowed => !requireStandardGrant || !allowed.UsedBreakGlass,
            AccessMasked masked => !requireStandardGrant || !masked.UsedBreakGlass,
            AccessDenied => false,
        };
    }

    private static StaffProfileView View(StoredStaffProfile stored, bool masked) =>
        new(stored.Profile.User, stored.Profile.DisplayName, stored.Profile.AvatarRef, masked ? null : stored.Profile.NotificationEmail,
            masked && stored.Profile.NotificationEmail is not null, stored.UpdatedAt, stored.Revision);

    private static TicketResult<T> Invalid<T>(string error) => new TicketFailure(TicketFailureKind.Invalid, [error]);

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }
}

/// <summary>Default <see cref="IStaffIdentityResolver"/> over <see cref="IStaffIdentityStore"/>.</summary>
public sealed class StoreStaffIdentityResolver(IStaffIdentityStore store) : IStaffIdentityResolver
{
    public async Task<UserId?> ResolveUserAsync(TicketScope scope, StaffIdentityRef identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(identity);
        return (await store.FindAsync(scope, identity, cancellationToken).ConfigureAwait(false))?.Link.User;
    }
}
