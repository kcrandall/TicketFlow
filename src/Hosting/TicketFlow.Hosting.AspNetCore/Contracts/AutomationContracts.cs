using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>
/// A trigger event for dry runs: <see cref="Kind"/> is <c>conversationCreated</c>, <c>messageReceived</c>, <c>messageSent</c>,
/// <c>conversationUpdated</c> (<see cref="Field"/>), <c>statusChanged</c> (<see cref="Status"/>), <c>assignmentChanged</c>,
/// <c>slaEvent</c> (<see cref="Sla"/>, <see cref="Metric"/>), <c>scheduled</c> or <c>contactUpdated</c>.
/// </summary>
public sealed record AutomationEventDto(string Kind, string? Field = null, string? Status = null, string? Sla = null, string? Metric = null);

/// <summary>Dry-run input; <see cref="Draft"/> is an unsaved rule in the automation rule codec format (evaluated instead of <see cref="Rules"/>).</summary>
public sealed record AutomationDryRunRequestDto(string ConversationId, AutomationEventDto Event, IReadOnlyList<string>? Rules = null, JsonElement? Draft = null, string? MessageBody = null);

/// <summary>What one action would do; <see cref="Action"/> is the action in the automation codec format.</summary>
public sealed record AutomationActionPreviewDto(int Index, JsonElement Action, string? RenderedText, string? Note);

/// <summary><see cref="Match"/> is <c>matched</c>, <c>notMatched</c>, <c>unknown</c>, <c>notTriggered</c> or <c>disabled</c>.</summary>
public sealed record AutomationRulePreviewDto(string RuleId, string Name, string Match, IReadOnlyList<AutomationActionPreviewDto> Actions);

public sealed record AutomationDryRunResultDto(string ConversationId, IReadOnlyList<AutomationRulePreviewDto> Rules, string? Stopped, DateTimeOffset EvaluatedAt);

public sealed record AutomationReplayRequestDto(string ConversationId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<string>? Rules = null);

public sealed record AutomationCauseDto(Guid Execution, string RuleId, int Depth);

/// <summary>An action outcome: <see cref="Kind"/> is <c>applied</c> (<see cref="Sequences"/>), <c>skipped</c>, <c>refused</c> or <c>failed</c> (<see cref="Reason"/>).</summary>
public sealed record AutomationActionOutcomeDto(string Kind, IReadOnlyList<long>? Sequences = null, string? Reason = null);

public sealed record AutomationExecutionDto(
    Guid Id,
    string RuleId,
    string? ConversationId,
    string TriggerKey,
    string EventKind,
    int Depth,
    string Status,
    string? Suppression,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int ActionCount,
    IReadOnlyList<AutomationActionOutcomeDto> Outcomes);

public sealed record AutomationReplayItemDto(long Sequence, DateTimeOffset OccurredAt, string EventKind, AutomationCauseDto? Cause, IReadOnlyList<AutomationRulePreviewDto> Previews,
    IReadOnlyList<AutomationExecutionDto> Executions);

public sealed record AutomationReplayResultDto(string ConversationId, IReadOnlyList<AutomationReplayItemDto> Items, bool Truncated);

public sealed record MacroRunRequest(string ConversationId);

public sealed record MacroRunResultDto(string MacroId, string ConversationId, Guid Run, IReadOnlyList<AutomationActionOutcomeDto> Outcomes, bool Completed);

public sealed record CannedResponseRenderRequest(string ConversationId, string? Locale = null);

public sealed record RenderedCannedResponseDto(string ResponseId, string Locale, string ContentType, string Body);
