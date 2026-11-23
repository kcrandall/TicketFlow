using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>Access-checked SLA policy management. Reads require <c>sla:read</c>, writes <c>sla:manage</c>, at workspace level.</summary>
public interface ISlaPolicyService
{
    Task<TicketResult<StoredSlaPolicy>> GetAsync(PrincipalRef actor, SlaPolicyId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<StoredSlaPolicy>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredSlaPolicy>> CreateAsync(PrincipalRef actor, SlaPolicy policy, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredSlaPolicy>> UpdateAsync(PrincipalRef actor, SlaPolicy policy, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredSlaPolicy>> DeleteAsync(PrincipalRef actor, SlaPolicyId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// One clock evaluated at <see cref="SlaStatusView.EvaluatedAt"/>: measured <see cref="Elapsed"/> time, <see cref="Remaining"/>
/// measured time until the target (negative once breached), the predicted breach instant <see cref="DueAt"/> (while running) and
/// the at-risk/breached flags.
/// </summary>
public sealed record SlaClockView(
    SlaMetric Metric,
    int Cycle,
    SlaClockState State,
    SlaClockOutcome? Outcome,
    TimeSpan Target,
    TimeSpan Elapsed,
    TimeSpan Remaining,
    DateTimeOffset StartedAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? BreachedAt,
    DateTimeOffset? CompletedAt,
    bool AtRisk,
    bool Breached);

/// <summary>A conversation's SLA status and breach prediction. <see cref="Policy"/> is <see langword="null"/> when no rule applies.</summary>
public sealed record SlaStatusView(
    ConversationId Conversation,
    InboxId Inbox,
    SlaPolicyId? Policy,
    string? RuleName,
    SlaClockBasis? Basis,
    bool Paused,
    IReadOnlyList<SlaClockView> Clocks,
    bool AtRisk,
    bool Breached,
    DateTimeOffset? DueAt,
    DateTimeOffset EvaluatedAt);

/// <summary>One page of at-risk/breached conversations; <see cref="NextCursor"/> is opaque, bound to the query, <see langword="null"/> at the end.</summary>
public sealed record SlaRiskPage(IReadOnlyList<SlaStatusView> Items, string? NextCursor);

/// <summary>
/// SLA status and risk queries. <see cref="GetStatusAsync"/> requires <c>sla:read</c> on the conversation resource;
/// <see cref="ListAsync"/> plans <c>sla:read</c> into the store query and point-checks every returned conversation.
/// </summary>
public interface ISlaService
{
    Task<TicketResult<SlaStatusView>> GetStatusAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default);

    Task<TicketResult<SlaRiskPage>> ListAsync(PrincipalRef actor, SlaRiskQuery query, string? cursor = null, CancellationToken cancellationToken = default);
}
