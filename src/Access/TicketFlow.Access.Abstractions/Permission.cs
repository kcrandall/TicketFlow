namespace TicketFlow.Access;

/// <summary>
/// A validated <c>resource:action</c> permission such as <c>conversation:reply</c>. Each segment is 1-64 characters of a-z, 0-9,
/// '-', '_' or '.', starting with a letter. <c>default</c> is uninitialized and never granted.
/// </summary>
public readonly record struct Permission : IComparable<Permission>
{
    /// <summary>Maximum length of the resource or action segment.</summary>
    public const int MaximumSegmentLength = 64;

    /// <summary>Maximum total length including the separator.</summary>
    public const int MaximumLength = MaximumSegmentLength * 2 + 1;

    private readonly string? value;

    public Permission(string value)
    {
        if (!TryValidate(value))
            throw new ArgumentException("Permissions must have the form resource:action with 1-64 character lowercase segments.", nameof(value));
        this.value = value;
    }

    public Permission(string resource, string action)
        : this(Join(resource, action))
    {
    }

    /// <summary>The exact <c>resource:action</c> text.</summary>
    /// <exception cref="InvalidOperationException">The permission is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The Permission is uninitialized.");

    public string Resource => Value[..Value.IndexOf(':')];

    public string Action => Value[(Value.IndexOf(':') + 1)..];

    public bool IsInitialized => value is not null;

    public static Permission Parse(string value) => new(value);

    public static bool TryParse(string? value, out Permission permission)
    {
        permission = TryValidate(value) ? new Permission(value!) : default;
        return permission.IsInitialized;
    }

    public int CompareTo(Permission other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;

    private static string Join(string resource, string action)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(action);
        return resource + ":" + action;
    }

    private static bool TryValidate(string? value)
    {
        if (value is null || value.Length > MaximumLength)
            return false;
        var separator = value.IndexOf(':');
        return separator > 0
            && AccessText.IsToken(value.AsSpan(0, separator), MaximumSegmentLength)
            && AccessText.IsToken(value.AsSpan(separator + 1), MaximumSegmentLength);
    }
}

/// <summary>A catalog entry. Sensitive permissions (exports, merges, reveals, access administration) audit every decision.</summary>
public sealed record PermissionDefinition
{
    public const int MaximumDescriptionLength = 256;

    public PermissionDefinition(Permission permission, bool isSensitive = false, string? description = null)
    {
        if (!permission.IsInitialized)
            throw new ArgumentException("An initialized permission is required.", nameof(permission));
        Permission = permission;
        IsSensitive = isSensitive;
        Description = description is null ? null : AccessText.RequireText(description, MaximumDescriptionLength, nameof(description));
    }

    public PermissionDefinition(string permission, bool isSensitive = false, string? description = null)
        : this(new Permission(permission), isSensitive, description)
    {
    }

    public Permission Permission { get; }
    public bool IsSensitive { get; }
    public string? Description { get; }
}

/// <summary>Permissions owned by TicketFlow itself. Feature packages register additional permissions in the catalog.</summary>
public static class AccessPermissions
{
    public static Permission ConversationRead { get; } = new("conversation:read");
    public static Permission ConversationReply { get; } = new("conversation:reply");
    public static Permission ConversationAssign { get; } = new("conversation:assign");
    public static Permission ConversationResolve { get; } = new("conversation:resolve");
    public static Permission ConversationDelete { get; } = new("conversation:delete");
    public static Permission ConversationManage { get; } = new("conversation:manage");
    public static Permission NoteRead { get; } = new("note:read");
    public static Permission NoteWrite { get; } = new("note:write");
    public static Permission ContactRead { get; } = new("contact:read");
    public static Permission ContactUpdate { get; } = new("contact:update");
    public static Permission ContactExport { get; } = new("contact:export");
    public static Permission ContactMerge { get; } = new("contact:merge");
    public static Permission ContactReveal { get; } = new("contact:reveal");
    public static Permission ReportView { get; } = new("report:view");
    public static Permission InboxRead { get; } = new("inbox:read");
    public static Permission InboxManage { get; } = new("inbox:manage");
    public static Permission TeamRead { get; } = new("team:read");
    public static Permission TeamManage { get; } = new("team:manage");
    public static Permission WorkspaceRead { get; } = new("workspace:read");
    public static Permission WorkspaceManage { get; } = new("workspace:manage");
    public static Permission CalendarManage { get; } = new("calendar:manage");
    public static Permission AccessManage { get; } = new("access:manage");
    public static Permission AccessManageTenant { get; } = new("access:manage-tenant");
    public static Permission AccessAuditRead { get; } = new("access:audit-read");
    public static Permission AccessBreakGlass { get; } = new("access:break-glass");
    public static Permission RoutingManage { get; } = new("routing:manage");
    public static Permission AgentStatusUpdate { get; } = new("agent-status:update");
    public static Permission AgentStatusManage { get; } = new("agent-status:manage");

    /// <summary>The built-in definitions, ordered by permission.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(AccessAuditRead, true, "Read access-control audit records."),
        new(AccessBreakGlass, true, "Activate a time-bound break-glass grant."),
        new(AccessManage, true, "Manage workspace roles, grants and field policies."),
        new(AccessManageTenant, true, "Manage tenant-wide grants."),
        new(AgentStatusManage, false, "Manage other agents' availability, capacity and skills."),
        new(AgentStatusUpdate, false, "Set one's own availability status and presence."),
        new(CalendarManage, false, "Manage business calendars."),
        new(ContactExport, true, "Export contacts."),
        new(ContactMerge, true, "Merge or unmerge contacts."),
        new(ContactRead, false, "Read contacts."),
        new(ContactReveal, true, "Reveal masked contact fields."),
        new(ContactUpdate, false, "Update contacts."),
        new(ConversationAssign, false, "Assign conversations."),
        new(ConversationDelete, true, "Delete conversations."),
        new(ConversationManage, false, "Change conversation priority, labels, attributes and participants."),
        new(ConversationRead, false, "Read conversations."),
        new(ConversationReply, false, "Reply to conversations."),
        new(ConversationResolve, false, "Resolve or reopen conversations."),
        new(InboxManage, false, "Manage inboxes."),
        new(InboxRead, false, "Read inbox definitions."),
        new(NoteRead, false, "Read private notes."),
        new(NoteWrite, false, "Write private notes."),
        new(ReportView, false, "View reports."),
        new(RoutingManage, false, "Manage routing policies."),
        new(TeamManage, false, "Manage teams and membership."),
        new(TeamRead, false, "Read teams and membership."),
        new(WorkspaceManage, true, "Manage workspace settings."),
        new(WorkspaceRead, false, "Read workspace settings and business calendars."),
    ];
}
