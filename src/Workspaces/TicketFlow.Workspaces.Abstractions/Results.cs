using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>The store write was applied; <see cref="Value"/> is the stored state (for deletes, the removed state).</summary>
public sealed record WorkspaceWriteApplied<T>(T Value);

/// <summary>No record exists for the key in the requested scope.</summary>
public sealed record WorkspaceWriteNotFound
{
    private WorkspaceWriteNotFound()
    {
    }

    public static WorkspaceWriteNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match, or a create found an existing record. Nothing was written.</summary>
public sealed record WorkspaceWriteConflict(long CurrentRevision);

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record WorkspaceWriteRejected(WorkspaceWriteRejection Reason);

public enum WorkspaceWriteRejection
{
    /// <summary>A per-workspace capacity in <see cref="WorkspaceLimits"/> would be exceeded.</summary>
    CapacityExceeded = 1,

    /// <summary>A member would belong to more than <see cref="WorkspaceLimits.MaximumTeamsPerUser"/> teams in the workspace.</summary>
    TeamMembershipLimitExceeded = 2,

    /// <summary>Another inbox in the workspace is already bound to the same channel kind and configuration.</summary>
    ChannelBindingInUse = 3,
}

/// <summary>Outcome of a revision-fenced store write. Switch over every case without a discard arm.</summary>
public union WorkspaceWriteResult<T>(WorkspaceWriteApplied<T>, WorkspaceWriteNotFound, WorkspaceWriteConflict, WorkspaceWriteRejected);

/// <summary>The authorized read or change succeeded.</summary>
public sealed record WorkspaceSucceeded<T>(T Value);

/// <summary>The request was refused. <see cref="Errors"/> are non-sensitive messages; <see cref="CurrentRevision"/> accompanies conflicts.</summary>
public sealed record WorkspaceRefused(WorkspaceRefusal Reason, IReadOnlyList<string> Errors, long? CurrentRevision = null);

public enum WorkspaceRefusal
{
    /// <summary>Access control denied the request. Checked before the store is consulted.</summary>
    Forbidden = 1,

    /// <summary>The input failed contextual validation (for example an unknown time zone or calendar reference).</summary>
    Invalid = 2,

    NotFound = 3,

    /// <summary>The expected revision did not match, or the record already exists.</summary>
    Conflict = 4,

    /// <summary>A store limit refused the write.</summary>
    Rejected = 5,
}

/// <summary>Outcome of a workspace service call. Switch over every case without a discard arm.</summary>
public union WorkspaceResult<T>(WorkspaceSucceeded<T>, WorkspaceRefused);

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
