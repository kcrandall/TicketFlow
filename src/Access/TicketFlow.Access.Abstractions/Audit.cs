using TicketFlow.Core;

namespace TicketFlow.Access;

public enum AccessAuditKind
{
    /// <summary>A point decision on a sensitive permission, or one that used a break-glass grant.</summary>
    Decision = 1,

    /// <summary>A query filter planned for a sensitive permission, or one that included a break-glass grant.</summary>
    FilterPlanned = 2,

    Reveal = 3,
    GrantCreated = 4,
    GrantUpdated = 5,
    GrantRevoked = 6,
    RoleCreated = 7,
    RoleUpdated = 8,
    RoleDeleted = 9,
    FieldPolicyCreated = 10,
    FieldPolicyUpdated = 11,
    FieldPolicyDeleted = 12,
    BreakGlassActivated = 13,
    ServiceAccountCreated = 14,
    ServiceAccountUpdated = 15,
    ServiceAccountEnabled = 16,
    ServiceAccountDisabled = 17,
    ServiceAccountDeleted = 18,
    ApiKeyCreated = 19,
    ApiKeyRevoked = 20,
    RoleFederationMappingCreated = 21,
    RoleFederationMappingUpdated = 22,
    RoleFederationMappingDeleted = 23,
}

public enum AccessAuditOutcome
{
    Allowed = 1,
    Masked = 2,
    Denied = 3,
}

public enum AccessActorKind
{
    User = 1,
    ApiClient = 2,
    Automation = 3,
    System = 4,
}

/// <summary>The acting principal's kind and identifier; never credentials.</summary>
public sealed record AccessActor
{
    public AccessActor(AccessActorKind kind, string id)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        Id = TicketScope.RequireIdentifier(id, nameof(id));
    }

    public AccessActorKind Kind { get; }
    public string Id { get; }

    public static AccessActor From(PrincipalRef principal) => principal switch
    {
        UserPrincipal user => new(AccessActorKind.User, user.UserId.Value),
        ApiClientPrincipal client => new(AccessActorKind.ApiClient, client.ClientId.Value),
        AutomationPrincipal automation => new(AccessActorKind.Automation, automation.AutomationId),
        SystemPrincipal system => new(AccessActorKind.System, system.Component),
    };
}

/// <summary>
/// A sensitive access event. Events carry identifiers, permission and field names, outcome and an optional operator justification;
/// they never carry message bodies, contact addresses, attribute values, masked values, credentials or tokens.
/// </summary>
public sealed record AccessAuditEvent
{
    public AccessAuditEvent(
        Guid id,
        DateTimeOffset occurredAt,
        TicketScope scope,
        AccessAuditKind kind,
        AccessAuditOutcome outcome,
        AccessActor actor,
        Permission? permission = null,
        string? resourceId = null,
        TeamId? team = null,
        InboxId? inbox = null,
        GrantId? grant = null,
        RoleId? role = null,
        FieldPath? field = null,
        bool usedBreakGlass = false,
        AccessDenialReason? denialReason = null,
        string? justification = null)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("An event identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        if (denialReason is { } reason && !Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(denialReason));
        if (permission is { IsInitialized: false } || team == default(TeamId) || inbox == default(InboxId) || grant is { IsInitialized: false }
            || role is { IsInitialized: false } || field is { IsInitialized: false })
            throw new ArgumentException("Supplied identifiers must be initialized.", nameof(grant));
        Id = id;
        OccurredAt = new DateTimeOffset(occurredAt.UtcTicks - occurredAt.UtcTicks % 10, TimeSpan.Zero);
        Scope = scope;
        Kind = kind;
        Outcome = outcome;
        Actor = actor;
        Permission = permission;
        ResourceId = resourceId is null ? null : TicketScope.RequireIdentifier(resourceId, nameof(resourceId));
        TeamId = team;
        InboxId = inbox;
        GrantId = grant;
        RoleId = role;
        Field = field;
        UsedBreakGlass = usedBreakGlass;
        DenialReason = denialReason;
        Justification = justification is null ? null : AccessText.RequireText(justification, AccessGrant.MaximumJustificationLength, nameof(justification));
    }

    public Guid Id { get; }
    public DateTimeOffset OccurredAt { get; }

    /// <summary>The scope the event happened in (the actor's workspace scope).</summary>
    public TicketScope Scope { get; }

    public AccessAuditKind Kind { get; }
    public AccessAuditOutcome Outcome { get; }
    public AccessActor Actor { get; }
    public Permission? Permission { get; }
    public string? ResourceId { get; }
    public TeamId? TeamId { get; }
    public InboxId? InboxId { get; }
    public GrantId? GrantId { get; }
    public RoleId? RoleId { get; }
    public FieldPath? Field { get; }
    public bool UsedBreakGlass { get; }
    public AccessDenialReason? DenialReason { get; }
    public string? Justification { get; }
}

/// <summary>
/// Records sensitive access events. <see cref="RecordAsync"/> must persist the event before completing; failures must throw, and
/// the engine then fails the operation instead of returning an unaudited decision. Each event is recorded exactly once per call.
/// </summary>
public interface IAccessAuditSink
{
    Task RecordAsync(AccessAuditEvent auditEvent, CancellationToken cancellationToken = default);
}
