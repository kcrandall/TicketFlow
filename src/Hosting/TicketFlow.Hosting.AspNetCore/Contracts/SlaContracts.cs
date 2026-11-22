namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>
/// One SLA clock. <see cref="Metric"/> is <c>firstResponse</c>, <c>nextResponse</c> or <c>resolution</c>; <see cref="State"/>
/// <c>running</c>, <c>paused</c> or <c>completed</c>; durations are whole seconds (negative remaining once breached).
/// </summary>
public sealed record SlaClockDto(
    string Metric,
    int Cycle,
    string State,
    string? Outcome,
    long TargetSeconds,
    long ElapsedSeconds,
    long RemainingSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? BreachedAt,
    DateTimeOffset? CompletedAt,
    bool AtRisk,
    bool Breached);

public sealed record SlaStatusDto(
    string ConversationId,
    string InboxId,
    string? PolicyId,
    string? RuleName,
    string? Basis,
    bool Paused,
    IReadOnlyList<SlaClockDto> Clocks,
    bool AtRisk,
    bool Breached,
    DateTimeOffset? DueAt,
    DateTimeOffset EvaluatedAt);
