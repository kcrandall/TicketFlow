using System.Buffers;
using System.Text.Unicode;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Identity;

/// <summary>
/// An external sign-in identity of a staff member: the identity provider (a lowercase token such as <c>identityflow</c>,
/// <c>google</c> or <c>entra-acme</c>) and the provider's exact, opaque subject. <see cref="ToString"/> redacts the subject.
/// </summary>
public sealed record StaffIdentityRef
{
    public const int MaximumProviderLength = 64;
    public const int MaximumSubjectLength = 256;

    public StaffIdentityRef(string provider, string subject)
    {
        Provider = StaffText.RequireToken(provider, MaximumProviderLength, nameof(provider));
        ArgumentNullException.ThrowIfNull(subject);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > MaximumSubjectLength || !StaffText.IsClean(subject))
            throw new ArgumentException($"Subjects are 1-{MaximumSubjectLength} characters without control characters.", nameof(subject));
        Subject = subject;
    }

    public string Provider { get; }

    /// <summary>The provider subject, compared exactly. Never log it.</summary>
    public string Subject { get; }

    public override string ToString() => $"StaffIdentityRef {{ Provider = {Provider}, <redacted> }}";
}

/// <summary>A mapping from an external identity to a TicketFlow user (many identities may map to one user).</summary>
public sealed record StaffIdentityLink
{
    public StaffIdentityLink(StaffIdentityRef identity, UserId user, DateTimeOffset linkedAt, AccessActor linkedBy)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        User = user;
        LinkedAt = StaffText.Instant(linkedAt);
        LinkedBy = linkedBy ?? throw new ArgumentNullException(nameof(linkedBy));
    }

    public StaffIdentityRef Identity { get; }
    public UserId User { get; }
    public DateTimeOffset LinkedAt { get; }
    public AccessActor LinkedBy { get; }
}

/// <summary>A link with its scope (mappings are workspace-scoped, like the principals they produce).</summary>
public sealed record StoredStaffIdentityLink(TicketScope Scope, StaffIdentityLink Link);

/// <summary>
/// Optional presentation and notification details of an agent, keyed by user. Routing capacities, skills and presence live in
/// <c>TicketFlow.Routing</c> (<c>AgentRoutingSettings</c>); this profile does not duplicate them. Personal data: never logged.
/// </summary>
public sealed record StaffProfile
{
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumAvatarRefLength = 2048;
    public const int MaximumEmailLength = 254;

    public StaffProfile(UserId user, string? displayName = null, string? avatarRef = null, string? notificationEmail = null)
    {
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        User = user;
        DisplayName = StaffText.OptionalText(displayName, MaximumDisplayNameLength, nameof(displayName));
        AvatarRef = StaffText.OptionalText(avatarRef, MaximumAvatarRefLength, nameof(avatarRef));
        if (notificationEmail is not null)
        {
            var email = notificationEmail.AsSpan().Trim();
            var at = email.LastIndexOf('@');
            if (email.Length is 0 or > MaximumEmailLength || at <= 0 || at == email.Length - 1 || StaffText.ContainsWhiteSpace(email) || !StaffText.IsClean(email))
                throw new ArgumentException("The notification email is invalid.", nameof(notificationEmail));
            notificationEmail = email.Length == notificationEmail.Length ? notificationEmail : new string(email);
        }
        NotificationEmail = notificationEmail;
    }

    public UserId User { get; }
    public string? DisplayName { get; }
    public string? AvatarRef { get; }
    public string? NotificationEmail { get; }

    public override string ToString() => $"StaffProfile {{ User = {User}, <redacted> }}";
}

/// <summary>A profile with its scope, revision (first is 1) and last update instant.</summary>
public sealed record StoredStaffProfile(TicketScope Scope, StaffProfile Profile, DateTimeOffset UpdatedAt, long Revision);

/// <summary>A profile as shown to a principal; <see cref="NotificationEmail"/> is <see langword="null"/> when <see cref="EmailMasked"/>.</summary>
public sealed record StaffProfileView(UserId User, string? DisplayName, string? AvatarRef, string? NotificationEmail, bool EmailMasked, DateTimeOffset UpdatedAt, long Revision)
{
    public override string ToString() => $"StaffProfileView {{ User = {User}, Revision = {Revision} }}";
}

/// <summary>Permissions registered by <c>AddStaffIdentity()</c>.</summary>
public static class StaffIdentityPermissions
{
    /// <summary>Map external sign-in identities to users and edit other users' profiles (sensitive).</summary>
    public static Permission Manage { get; } = new("staff-identity:manage");

    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Manage, true, "Map external sign-in identities to staff users and edit staff profiles."),
    ];
}

/// <summary>Access field paths for staff profiles.</summary>
public static class StaffFieldPaths
{
    /// <summary>The notification email, masked in profile reads by field policies applying to <c>team:read</c>.</summary>
    public static FieldPath NotificationEmail { get; } = new("staff.email");
}

/// <summary>Audited staff identity operations. Values are persisted; never renumber.</summary>
public enum StaffIdentityAuditKind
{
    Linked = 1,
    Unlinked = 2,
    ProfileChanged = 3,
}

/// <summary>An audited staff identity change. Carries the provider and user identifiers, never the subject or profile values.</summary>
public sealed record StaffIdentityAuditEvent
{
    public const int MaximumJustificationLength = 1024;

    public StaffIdentityAuditEvent(Guid id, DateTimeOffset occurredAt, TicketScope scope, StaffIdentityAuditKind kind, AccessActor actor, UserId user, string? provider = null,
        string? justification = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An event identifier is required.", nameof(id));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        Id = id;
        OccurredAt = StaffText.Instant(occurredAt);
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Kind = kind;
        Actor = actor ?? throw new ArgumentNullException(nameof(actor));
        User = user;
        Provider = provider is null ? null : StaffText.RequireToken(provider, StaffIdentityRef.MaximumProviderLength, nameof(provider));
        Justification = StaffText.OptionalText(justification, MaximumJustificationLength, nameof(justification));
    }

    public Guid Id { get; }
    public DateTimeOffset OccurredAt { get; }
    public TicketScope Scope { get; }
    public StaffIdentityAuditKind Kind { get; }
    public AccessActor Actor { get; }
    public UserId User { get; }
    public string? Provider { get; }
    public string? Justification { get; }
}

public sealed record StaffIdentityWriteApplied(StoredStaffIdentityLink Link, bool Changed = true);

/// <summary>The identity is linked to <see cref="User"/> (another user). Nothing was written.</summary>
public sealed record StaffIdentityWriteTaken(UserId User);

public sealed record StaffIdentityWriteNotFound
{
    private StaffIdentityWriteNotFound()
    {
    }

    public static StaffIdentityWriteNotFound Instance { get; } = new();
}

/// <summary>A store capacity limit refused the write. Nothing was written.</summary>
public sealed record StaffIdentityWriteRejected
{
    private StaffIdentityWriteRejected()
    {
    }

    public static StaffIdentityWriteRejected CapacityExceeded { get; } = new();
}

/// <summary>Outcome of a link or unlink. Switch over every case without a discard arm.</summary>
public union StaffIdentityWriteResult(StaffIdentityWriteApplied, StaffIdentityWriteTaken, StaffIdentityWriteNotFound, StaffIdentityWriteRejected);

public sealed record StaffIdentityProfileWriteApplied(StoredStaffProfile Profile);

public sealed record StaffIdentityWriteConflict(long CurrentRevision);

/// <summary>Outcome of a profile write. Switch over every case without a discard arm.</summary>
public union StaffIdentityProfileWriteResult(StaffIdentityProfileWriteApplied, StaffIdentityWriteNotFound, StaffIdentityWriteConflict);

/// <summary>
/// Durable staff identity storage. Contract:
/// <list type="bullet">
/// <item>Every operation is confined to one <see cref="TicketScope"/>.</item>
/// <item>(scope, provider, subject) maps to at most one user, enforced atomically; linking an identity already linked to the same
/// user is an idempotent no-op (<see cref="StaffIdentityWriteApplied.Changed"/> false, no audit), to another user
/// <see cref="StaffIdentityWriteTaken"/>. A user holds at most <see cref="MaximumIdentitiesPerUser"/> identities per scope (atomic).</item>
/// <item>Unlink removes the mapping only when it still names the expected user.</item>
/// <item>Profiles: create at revision 1 (<see langword="null"/> expected revision; conflict when present), compare-and-set afterwards.</item>
/// <item>The supplied audit event is recorded in the same transaction as the change it describes.</item>
/// </list>
/// </summary>
public interface IStaffIdentityStore
{
    public const int MaximumIdentitiesPerUser = 32;

    Task<StoredStaffIdentityLink?> FindAsync(TicketScope scope, StaffIdentityRef identity, CancellationToken cancellationToken = default);

    /// <summary>The user's identities ordered by (provider, subject) ordinal.</summary>
    Task<IReadOnlyList<StoredStaffIdentityLink>> ListForUserAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default);

    Task<StaffIdentityWriteResult> LinkAsync(TicketScope scope, StaffIdentityLink link, StaffIdentityAuditEvent audit, CancellationToken cancellationToken = default);

    Task<StaffIdentityWriteResult> UnlinkAsync(TicketScope scope, StaffIdentityRef identity, UserId expectedUser, StaffIdentityAuditEvent audit, CancellationToken cancellationToken = default);

    Task<StoredStaffProfile?> GetProfileAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default);

    Task<StaffIdentityProfileWriteResult> PutProfileAsync(TicketScope scope, StaffProfile profile, DateTimeOffset updatedAt, long? expectedRevision, StaffIdentityAuditEvent audit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorized staff identity management. Linking and unlinking require <c>staff-identity:manage</c> and that the actor could confer
/// every grant the target user holds in the workspace (directly or through teams), so a mapping can never hand an actor more
/// authority than it already has. Every change is audited.
/// </summary>
public interface IStaffIdentityService
{
    Task<TicketResult<StoredStaffIdentityLink>> LinkAsync(PrincipalRef actor, StaffIdentityRef identity, UserId user, string? justification = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the user the identity was linked to.</summary>
    Task<TicketResult<UserId>> UnlinkAsync(PrincipalRef actor, StaffIdentityRef identity, string? justification = null, CancellationToken cancellationToken = default);

    /// <summary>The actor's own identities (<c>workspace:read</c>) or anyone's (<c>staff-identity:manage</c>).</summary>
    Task<TicketResult<IReadOnlyList<StoredStaffIdentityLink>>> ListAsync(PrincipalRef actor, UserId user, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>team:read</c>; the notification email is masked per <see cref="StaffFieldPaths.NotificationEmail"/> policies except for the user itself.</summary>
    Task<TicketResult<StaffProfileView>> GetProfileAsync(PrincipalRef actor, UserId user, CancellationToken cancellationToken = default);

    /// <summary>The actor's own profile (<c>workspace:read</c>) or anyone's (<c>staff-identity:manage</c>); audited.</summary>
    Task<TicketResult<StaffProfileView>> SetProfileAsync(PrincipalRef actor, StaffProfile profile, long? expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// Trusted, unauthorized resolution for principal resolvers (like <c>ITeamMembershipResolver</c>): maps an authenticated external
/// identity to the canonical <see cref="UserId"/> of the workspace, or <see langword="null"/> when unmapped. The caller must have
/// authenticated the identity; the result is not audited.
/// </summary>
public interface IStaffIdentityResolver
{
    Task<UserId?> ResolveUserAsync(TicketScope scope, StaffIdentityRef identity, CancellationToken cancellationToken = default);
}

internal static class StaffText
{
    private static readonly SearchValues<char> TokenChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-_");
    private static readonly SearchValues<char> ControlChars = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F" +
        "\u007F\u0080\u0081\u0082\u0083\u0084\u0085\u0086\u0087\u0088\u0089\u008A\u008B\u008C\u008D\u008E\u008F" +
        "\u0090\u0091\u0092\u0093\u0094\u0095\u0096\u0097\u0098\u0099\u009A\u009B\u009C\u009D\u009E\u009F");

    public static bool IsClean(ReadOnlySpan<char> value) =>
        !value.ContainsAny(ControlChars) && (!value.ContainsAnyInRange('\uD800', '\uDFFF') || Utf16.IsValid(value));

    public static bool ContainsWhiteSpace(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
            if (char.IsWhiteSpace(ch))
                return true;
        return false;
    }

    public static string RequireToken(string value, int maximumLength, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is 0 || value.Length > maximumLength || value[0] is not (>= 'a' and <= 'z') || value.AsSpan().ContainsAnyExcept(TokenChars))
            throw new ArgumentException($"Tokens are 1-{maximumLength} characters of a-z, 0-9, '-' or '_', starting with a letter.", parameterName);
        return value;
    }

    public static string? OptionalText(string? value, int maximumLength, string parameterName)
    {
        if (value is null)
            return null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || !IsClean(value))
            throw new ArgumentException($"Values are 1-{maximumLength} characters without control characters.", parameterName);
        return value;
    }

    /// <summary>UTC with microsecond precision, matching PostgreSQL <c>timestamptz</c>.</summary>
    public static DateTimeOffset Instant(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
