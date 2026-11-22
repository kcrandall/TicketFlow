using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// Event stream permissions, registered by <c>AddEvents()</c>; only Owner holds them until custom roles grant them.
/// <c>events:manage</c> and <c>events:content</c> are sensitive (every decision is audited).
/// </summary>
public static class EventPermissions
{
    /// <summary>Read subscriptions and receive events (the subscription principal must hold it).</summary>
    public static Permission Read { get; } = new("events:read");

    /// <summary>Create, change, delete and replay subscriptions and read dead letters (sensitive).</summary>
    public static Permission Manage { get; } = new("events:manage");

    /// <summary>Receive or configure <see cref="EventPrivacy.Content"/> subscriptions (sensitive).</summary>
    public static Permission Content { get; } = new("events:content");

    /// <summary>The feature-owned catalog definitions, ordered by permission.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Content, true, "Receive message bodies and other content in integration events."),
        new(Manage, true, "Manage integration event subscriptions, replay and dead letters."),
        new(Read, false, "Read integration event subscriptions and receive events."),
    ];

    /// <summary>The workspace resource every event-stream permission is decided on.</summary>
    public static AccessResource Workspace(TicketScope scope) => new(scope ?? throw new ArgumentNullException(nameof(scope)));
}
