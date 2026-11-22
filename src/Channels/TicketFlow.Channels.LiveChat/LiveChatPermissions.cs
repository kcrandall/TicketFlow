using TicketFlow.Access;

namespace TicketFlow.Channels.LiveChat;

/// <summary>
/// Permissions registered by <c>AddLiveChat()</c>. Widget configurations are authorized on the bound inbox
/// (<c>WorkspaceResources.Inbox</c>), so inbox-scoped grants apply. Visitors are not principals: visitor work runs through the
/// channel worker principal (<c>ChannelsOptions.WorkerPrincipal</c>).
/// </summary>
public static class LiveChatPermissions
{
    /// <summary>Read widget configurations.</summary>
    public static Permission Read { get; } = new("livechat:read");

    /// <summary>Create, change and delete widget configurations (sensitive: origins and identity policy guard visitor sessions).</summary>
    public static Permission Manage { get; } = new("livechat:manage");

    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Read, false, "Read live-chat widget configurations."),
        new(Manage, true, "Manage live-chat widget configurations."),
    ];
}
