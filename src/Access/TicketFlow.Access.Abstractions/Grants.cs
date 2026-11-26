using System.Runtime.CompilerServices;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Validated opaque grant identifier, unique within an <see cref="AccessPartition"/>.</summary>
public readonly record struct GrantId
{
    private readonly string? value;

    public GrantId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The GrantId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// The app-level partition (tenant → project → environment → app) that owns grants. Workspace, team and inbox grants name their
/// workspace in <see cref="GrantScope"/>; tenant grants apply in every workspace of the partition.
/// </summary>
public sealed record AccessPartition
{
    public AccessPartition(string tenantId, string projectId, string environmentId, string appId)
    {
        TenantId = TicketScope.RequireIdentifier(tenantId, nameof(tenantId));
        ProjectId = TicketScope.RequireIdentifier(projectId, nameof(projectId));
        EnvironmentId = TicketScope.RequireIdentifier(environmentId, nameof(environmentId));
        AppId = TicketScope.RequireIdentifier(appId, nameof(appId));
    }

    public string TenantId { get; }
    public string ProjectId { get; }
    public string EnvironmentId { get; }
    public string AppId { get; }

    public static AccessPartition From(TicketScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId);
    }

    public bool Contains(TicketScope scope) =>
        scope is not null && string.Equals(scope.TenantId, TenantId, StringComparison.Ordinal) && string.Equals(scope.ProjectId, ProjectId, StringComparison.Ordinal) && string.Equals(scope.EnvironmentId, EnvironmentId, StringComparison.Ordinal) && string.Equals(scope.AppId, AppId, StringComparison.Ordinal);

    public TicketScope InWorkspace(WorkspaceId workspace) => new(TenantId, ProjectId, EnvironmentId, AppId, workspace);

    public override string ToString() => "AccessPartition { <redacted> }";
}

/// <summary>
/// Who a grant applies to: a user, every user whose team snapshot contains a team, or an API client. Stored inline as a
/// non-boxing C# 15 union so authorization lookups never box identifier structs.
/// </summary>
[Union]
public readonly struct GrantSubject : IUnion, IEquatable<GrantSubject>
{
    private readonly byte tag;
    private readonly string? raw;

    public GrantSubject(UserId user)
    {
        tag = 1;
        raw = user == default ? null : user.Value;
    }

    public GrantSubject(TeamId team)
    {
        tag = 2;
        raw = team == default ? null : team.Value;
    }

    public GrantSubject(ApiClientId client)
    {
        tag = 3;
        raw = client == default ? null : client.Value;
    }

    public bool HasValue => tag != 0;

    internal bool IsInitialized => tag != 0 && raw is not null;

    public object? Value => tag switch
    {
        1 => raw is null ? default(UserId) : new UserId(raw),
        2 => raw is null ? default(TeamId) : new TeamId(raw),
        3 => raw is null ? default(ApiClientId) : new ApiClientId(raw),
        _ => null,
    };

    public bool TryGetValue(out UserId value)
    {
        if (tag == 1)
        {
            value = raw is null ? default : new UserId(raw);
            return true;
        }
        value = default;
        return false;
    }

    public bool TryGetValue(out TeamId value)
    {
        if (tag == 2)
        {
            value = raw is null ? default : new TeamId(raw);
            return true;
        }
        value = default;
        return false;
    }

    public bool TryGetValue(out ApiClientId value)
    {
        if (tag == 3)
        {
            value = raw is null ? default : new ApiClientId(raw);
            return true;
        }
        value = default;
        return false;
    }

    public bool Equals(GrantSubject other) => tag == other.tag && string.Equals(raw, other.raw, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is GrantSubject other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(tag, raw is null ? 0 : StringComparer.Ordinal.GetHashCode(raw));

    public override string ToString() => tag switch
    {
        1 => $"UserId {{ Value = {raw} }}",
        2 => $"TeamId {{ Value = {raw} }}",
        3 => $"ApiClientId {{ Value = {raw} }}",
        _ => string.Empty,
    };

    public static bool operator ==(GrantSubject left, GrantSubject right) => left.Equals(right);

    public static bool operator !=(GrantSubject left, GrantSubject right) => !left.Equals(right);

    public static implicit operator GrantSubject(UserId user) => new(user);

    public static implicit operator GrantSubject(TeamId team) => new(team);

    public static implicit operator GrantSubject(ApiClientId client) => new(client);
}

/// <summary>Grant resource scope. Grants inherit downward: tenant → workspace → team/inbox → resources in that team/inbox.</summary>
public closed record GrantScope;

/// <summary>Every workspace in the grant's partition. Only built-in roles can be granted at tenant scope.</summary>
public sealed record TenantGrantScope : GrantScope
{
    private TenantGrantScope()
    {
    }

    public static TenantGrantScope Instance { get; } = new();
}

/// <summary>Every resource in one workspace.</summary>
public sealed record WorkspaceGrantScope : GrantScope
{
    public WorkspaceGrantScope(WorkspaceId workspace)
    {
        if (workspace == default)
            throw new ArgumentException("A workspace identifier is required.", nameof(workspace));
        Workspace = workspace;
    }

    public WorkspaceId Workspace { get; }
}

/// <summary>Resources whose team is <see cref="Team"/> within one workspace.</summary>
public sealed record TeamGrantScope : GrantScope
{
    public TeamGrantScope(WorkspaceId workspace, TeamId team)
    {
        if (workspace == default || team == default)
            throw new ArgumentException("Workspace and team identifiers are required.", workspace == default ? nameof(workspace) : nameof(team));
        Workspace = workspace;
        Team = team;
    }

    public WorkspaceId Workspace { get; }
    public TeamId Team { get; }
}

/// <summary>Resources whose inbox is <see cref="Inbox"/> within one workspace.</summary>
public sealed record InboxGrantScope : GrantScope
{
    public InboxGrantScope(WorkspaceId workspace, InboxId inbox)
    {
        if (workspace == default || inbox == default)
            throw new ArgumentException("Workspace and inbox identifiers are required.", workspace == default ? nameof(workspace) : nameof(inbox));
        Workspace = workspace;
        Inbox = inbox;
    }

    public WorkspaceId Workspace { get; }
    public InboxId Inbox { get; }
}

public enum GrantEffect
{
    Allow = 1,

    /// <summary>Denies the role's permissions. Any applicable deny overrides every allow, at any scope.</summary>
    Deny = 2,
}

public enum GrantKind
{
    Standard = 1,

    /// <summary>Time-bound elevated access with a mandatory justification; activation and every use are audited.</summary>
    BreakGlass = 2,
}

/// <summary>
/// Binds a subject to a role at a resource scope, with an optional condition and validity window
/// [<see cref="ValidFrom"/>, <see cref="ValidUntil"/>). Instants are normalized to UTC microseconds.
/// </summary>
public sealed record AccessGrant
{
    public const int MaximumJustificationLength = 1024;

    public AccessGrant(
        GrantId id,
        AccessPartition partition,
        GrantSubject subject,
        RoleId role,
        GrantScope scope,
        GrantEffect effect = GrantEffect.Allow,
        AccessCondition? condition = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        string? justification = null,
        GrantKind kind = GrantKind.Standard)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A grant identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(partition);
        if (!IsInitialized(subject))
            throw new ArgumentException("An initialized grant subject is required.", nameof(subject));
        if (!role.IsInitialized)
            throw new ArgumentException("A role identifier is required.", nameof(role));
        ArgumentNullException.ThrowIfNull(scope);
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        validFrom = Normalize(validFrom);
        validUntil = Normalize(validUntil);
        if (validFrom is not null && validUntil is not null && validUntil <= validFrom)
            throw new ArgumentException("A grant's validity must end after it starts.", nameof(validUntil));
        justification = justification is null ? null : AccessText.RequireText(justification, MaximumJustificationLength, nameof(justification));
        if (kind == GrantKind.BreakGlass && (effect != GrantEffect.Allow || validUntil is null || justification is null))
            throw new ArgumentException("Break-glass grants must allow, expire and carry a justification.", nameof(kind));

        Id = id;
        Partition = partition;
        Subject = subject;
        Role = role;
        Scope = scope;
        Effect = effect;
        Condition = condition;
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        Justification = justification;
        Kind = kind;
    }

    public GrantId Id { get; }
    public AccessPartition Partition { get; }
    public GrantSubject Subject { get; }
    public RoleId Role { get; }
    public GrantScope Scope { get; }
    public GrantEffect Effect { get; }
    public AccessCondition? Condition { get; }
    public DateTimeOffset? ValidFrom { get; }
    public DateTimeOffset? ValidUntil { get; }

    /// <summary>Operator-supplied reason. Required for break-glass grants; must not contain support content or personal data.</summary>
    public string? Justification { get; }

    public GrantKind Kind { get; }

    /// <summary>The workspace named by <see cref="Scope"/>, or <see langword="null"/> for tenant grants.</summary>
    public WorkspaceId? Workspace => Scope switch
    {
        TenantGrantScope => null,
        WorkspaceGrantScope workspace => workspace.Workspace,
        TeamGrantScope team => team.Workspace,
        InboxGrantScope inbox => inbox.Workspace,
    };

    /// <summary>True when <paramref name="value"/> is non-blank, at most 1024 characters, without control characters and well-formed.</summary>
    public static bool IsValidJustification(string? value) => AccessText.IsValidText(value, MaximumJustificationLength);

    public bool IsValidAt(DateTimeOffset instant) => (ValidFrom is null || instant >= ValidFrom) && (ValidUntil is null || instant < ValidUntil);

    public override string ToString() => "AccessGrant { <redacted> }";

    internal static bool IsInitialized(GrantSubject subject) => subject.IsInitialized;

    private static DateTimeOffset? Normalize(DateTimeOffset? value) =>
        value is { } instant ? new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero) : null;
}

/// <summary>A stored grant with its revision (first revision is 1).</summary>
public sealed record StoredAccessGrant(AccessGrant Grant, long Revision);

/// <summary>The grant satisfies contextual validation.</summary>
public sealed record GrantValid(AccessGrant Grant);

/// <summary>The grant failed contextual validation. Errors are non-sensitive, deterministic and ordered.</summary>
public sealed record GrantInvalid(IReadOnlyList<string> Errors);

/// <summary>Outcome of contextual grant validation. Switch over every case without a discard arm.</summary>
public union GrantValidationResult(GrantValid, GrantInvalid);

/// <summary>Argument validation shared by <see cref="IAccessGrantStore.ListForTargetAsync"/> implementations.</summary>
public static class AccessGrantTargets
{
    public static void Validate(GrantScope target, int limit, GrantId? after)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target is TenantGrantScope)
            throw new ArgumentException("Tenant scopes cannot be listed by target.", nameof(target));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, AccessLimits.MaximumTargetPage);
        if (after is { IsInitialized: false })
            throw new ArgumentException("The cursor must be initialized when supplied.", nameof(after));
    }
}
