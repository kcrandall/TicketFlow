using System.Collections.Frozen;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>Validated role identifier: 1-64 characters of a-z, 0-9, '-', '_' or '.', starting with a letter.</summary>
public readonly record struct RoleId : IComparable<RoleId>
{
    public const int MaximumLength = 64;

    private readonly string? value;

    public RoleId(string value) => this.value = AccessText.RequireToken(value, MaximumLength, nameof(value), "Role identifiers");

    /// <exception cref="InvalidOperationException">The identifier is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The RoleId is uninitialized.");

    public bool IsInitialized => value is not null;

    public int CompareTo(RoleId other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// A named set of permissions. Built-in roles are immutable and resolved without a store; custom roles are workspace-scoped and
/// revisioned by <see cref="IAccessRoleStore"/>. Custom roles cannot reuse built-in identifiers.
/// </summary>
public sealed record AccessRole
{
    public const int MaximumPermissions = 512;
    public const int MaximumNameLength = 128;

    /// <summary>Creates a custom role.</summary>
    public AccessRole(RoleId id, string name, IEnumerable<Permission> permissions)
        : this(RequireCustom(id), name, Snapshot(permissions), includesAllPermissions: false, isBuiltIn: false)
    {
    }

    private AccessRole(RoleId id, string name, FrozenSet<Permission> permissions, bool includesAllPermissions, bool isBuiltIn)
    {
        Id = id;
        Name = AccessText.RequireText(name, MaximumNameLength, nameof(name));
        Permissions = permissions;
        IncludesAllPermissions = includesAllPermissions;
        IsBuiltIn = isBuiltIn;
    }

    public RoleId Id { get; }
    public string Name { get; }

    /// <summary>Explicit permissions. Empty for roles that include every catalog permission.</summary>
    public IReadOnlySet<Permission> Permissions { get; }

    /// <summary>True only for the built-in Owner role, which holds every permission registered in the catalog.</summary>
    public bool IncludesAllPermissions { get; }

    public bool IsBuiltIn { get; }

    public bool Includes(Permission permission) => permission.IsInitialized && (IncludesAllPermissions || Permissions.Contains(permission));

    public bool Equals(AccessRole? other) =>
        other is not null && Id == other.Id && Name == other.Name && IncludesAllPermissions == other.IncludesAllPermissions
        && IsBuiltIn == other.IsBuiltIn && Permissions.SetEquals(other.Permissions);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Permissions.Count, IncludesAllPermissions, IsBuiltIn);

    internal static AccessRole BuiltIn(string id, string name, IEnumerable<Permission> permissions, bool includesAll = false) =>
        new(new RoleId(id), name, Snapshot(permissions), includesAll, isBuiltIn: true);

    private static RoleId RequireCustom(RoleId id)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A role identifier is required.", nameof(id));
        if (BuiltInRoles.IsBuiltIn(id))
            throw new ArgumentException("Custom roles cannot reuse a built-in role identifier.", nameof(id));
        return id;
    }

    private static FrozenSet<Permission> Snapshot(IEnumerable<Permission> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var snapshot = new HashSet<Permission>();
        foreach (var permission in permissions)
        {
            if (!permission.IsInitialized)
                throw new ArgumentException("Roles cannot contain uninitialized permissions.", nameof(permissions));
            if (snapshot.Add(permission) && snapshot.Count > MaximumPermissions)
                throw new ArgumentException($"A role can contain at most {MaximumPermissions} permissions.", nameof(permissions));
        }
        return snapshot.ToFrozenSet();
    }
}

/// <summary>The immutable built-in roles.</summary>
public static class BuiltInRoles
{
    private static readonly Permission[] AgentPermissions =
    [
        AccessPermissions.ConversationRead, AccessPermissions.ConversationReply, AccessPermissions.ConversationAssign,
        AccessPermissions.ConversationResolve, AccessPermissions.NoteRead, AccessPermissions.NoteWrite,
        AccessPermissions.ContactRead, AccessPermissions.ContactUpdate,
    ];

    /// <summary>Every permission registered in the catalog, including feature permissions.</summary>
    public static AccessRole Owner { get; } = AccessRole.BuiltIn("owner", "Owner", [], includesAll: true);

    /// <summary>Every built-in permission except tenant-wide grant management and workspace settings.</summary>
    public static AccessRole Admin { get; } = AccessRole.BuiltIn("admin", "Admin", AccessPermissions.Definitions
        .Select(definition => definition.Permission)
        .Where(permission => permission != AccessPermissions.AccessManageTenant && permission != AccessPermissions.WorkspaceManage));

    public static AccessRole Supervisor { get; } = AccessRole.BuiltIn("supervisor", "Supervisor",
    [
        .. AgentPermissions, AccessPermissions.ConversationDelete, AccessPermissions.ContactExport, AccessPermissions.ContactMerge,
        AccessPermissions.ReportView, AccessPermissions.TeamManage, AccessPermissions.AccessAuditRead,
    ]);

    public static AccessRole Agent { get; } = AccessRole.BuiltIn("agent", "Agent", AgentPermissions);

    /// <summary>Reads conversations and works with private notes only.</summary>
    public static AccessRole LightAgent { get; } = AccessRole.BuiltIn("light-agent", "Light agent",
        [AccessPermissions.ConversationRead, AccessPermissions.NoteRead, AccessPermissions.NoteWrite]);

    public static AccessRole Viewer { get; } = AccessRole.BuiltIn("viewer", "Viewer",
        [AccessPermissions.ConversationRead, AccessPermissions.NoteRead, AccessPermissions.ContactRead, AccessPermissions.ReportView]);

    public static IReadOnlyList<AccessRole> All { get; } = [Owner, Admin, Supervisor, Agent, LightAgent, Viewer];

    private static readonly FrozenDictionary<string, AccessRole> ById = All.ToFrozenDictionary(role => role.Id.Value, StringComparer.Ordinal);

    public static bool IsBuiltIn(RoleId id) => id.IsInitialized && ById.ContainsKey(id.Value);

    public static AccessRole? Find(RoleId id) => id.IsInitialized ? ById.GetValueOrDefault(id.Value) : null;
}

/// <summary>A custom role with its workspace scope and revision (first revision is 1).</summary>
public sealed record StoredAccessRole(TicketScope Scope, AccessRole Role, long Revision);
