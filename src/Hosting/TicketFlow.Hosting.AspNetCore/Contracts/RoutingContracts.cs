namespace TicketFlow.Hosting.AspNetCore.Contracts;

public sealed record AgentSkillDto(string Name, int Level = 1);

/// <summary>
/// An agent's routing profile. <see cref="Status"/> is the effective status (<c>online</c>, <c>busy</c>, <c>away</c>, <c>offline</c>;
/// lapsed presence reads as offline); <see cref="Capacities"/> are per channel kind.
/// </summary>
public sealed record AgentProfileDto(
    string UserId,
    string Status,
    DateTimeOffset StatusChangedAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? PresenceExpiresAt,
    int DefaultCapacity,
    IReadOnlyDictionary<string, int> Capacities,
    IReadOnlyList<AgentSkillDto> Skills,
    bool ReassignmentPending,
    long Revision);

public sealed record AgentStatusRequest(string Status);

public sealed record AgentSettingsRequest(int DefaultCapacity, IReadOnlyDictionary<string, int>? Capacities = null, IReadOnlyList<AgentSkillDto>? Skills = null);

public sealed record QueueItemDto(string ConversationId, string InboxId, string TargetInboxId, string? TeamId, DateTimeOffset WaitingSince, DateTimeOffset QueuedAt, int OverflowStage, bool WaitExceeded);

/// <summary>Collision presence: <see cref="Activity"/> is <c>viewing</c> or <c>composing</c>.</summary>
public sealed record PresenceDto(string UserId, string Activity, DateTimeOffset Since, DateTimeOffset ExpiresAt);

public sealed record PresenceRequest(string Activity);

public sealed record ReplyLockDto(string ConversationId, string HolderId, long Fence, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt);

/// <summary>The lock outcome: acquired (or renewed) by the caller, or held by someone else (<see cref="Lock"/> is the current lock).</summary>
public sealed record ReplyLockResultDto(bool Acquired, bool Renewed, ReplyLockDto Lock);

public sealed record ReplyLockRequest(int? DurationSeconds = null);
