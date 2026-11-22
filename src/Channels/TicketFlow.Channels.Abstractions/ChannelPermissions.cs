using TicketFlow.Access;

namespace TicketFlow.Channels;

/// <summary>
/// Permissions registered by <c>AddChannels()</c>. Channel endpoints are authorized on the bound inbox
/// (<c>WorkspaceResources.Inbox</c>), so inbox-scoped grants apply. Ingestion itself uses the conversation and contact permissions
/// (<c>conversation:create</c>, <c>conversation:ingest</c>, <c>contact:read</c>, <c>contact:create</c>).
/// </summary>
public static class ChannelPermissions
{
    /// <summary>Read channel endpoints and dispatch records of an inbox.</summary>
    public static Permission Read { get; } = new("channel:read");

    /// <summary>Bind, rebind and remove channel endpoints (sensitive: endpoints route customer traffic).</summary>
    public static Permission Manage { get; } = new("channel:manage");

    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Read, false, "Read channel endpoints and dispatch status."),
        new(Manage, true, "Bind and remove channel endpoints."),
    ];
}
