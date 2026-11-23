using TicketFlow.Core;

namespace TicketFlow.Workspaces;

public enum WorkspaceWriteRejection
{
    /// <summary>A per-workspace capacity in <see cref="WorkspaceLimits"/> would be exceeded.</summary>
    CapacityExceeded = 1,

    /// <summary>A member would belong to more than <see cref="WorkspaceLimits.MaximumTeamsPerUser"/> teams in the workspace.</summary>
    TeamMembershipLimitExceeded = 2,

    /// <summary>Another inbox in the workspace is already bound to the same channel kind and configuration.</summary>
    ChannelBindingInUse = 3,
}

/// <summary>One page of a list ordered by identifier (ordinal). <see cref="HasMore"/> means a further page exists.</summary>
public sealed record WorkspacePage<T>(IReadOnlyList<T> Items, bool HasMore);

/// <summary>A keyset page request: at most <see cref="Limit"/> items whose identifier is ordinally greater than <see cref="After"/>.</summary>
public sealed record WorkspacePageRequest
{
    public WorkspacePageRequest(int limit = 50, string? after = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, WorkspaceLimits.MaximumPageSize);
        Limit = limit;
        After = after is null ? null : TicketScope.RequireIdentifier(after, nameof(after));
    }

    public int Limit { get; }
    public string? After { get; }
}

/// <summary>Capacity limits every workspace store enforces atomically with the write.</summary>
public static class WorkspaceLimits
{
    public const int MaximumInboxesPerWorkspace = 1000;
    public const int MaximumTeamsPerWorkspace = 1000;
    public const int MaximumCalendarsPerWorkspace = 256;
    public const int MaximumPageSize = 200;

    /// <summary>Equal to <see cref="UserPrincipal.MaximumTeamMemberships"/>, so resolved team snapshots always fit a principal.</summary>
    public const int MaximumTeamsPerUser = UserPrincipal.MaximumTeamMemberships;
}
