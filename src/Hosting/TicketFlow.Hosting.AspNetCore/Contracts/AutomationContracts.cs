using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>
/// A trigger event for dry runs: <see cref="Kind"/> is <c>conversationCreated</c>, <c>messageReceived</c>, <c>messageSent</c>,
/// <c>conversationUpdated</c> (<see cref="Field"/>), <c>statusChanged</c> (<see cref="Status"/>), <c>assignmentChanged</c>,
/// <c>slaEvent</c> (<see cref="Sla"/>, <see cref="Metric"/>), <c>scheduled</c> or <c>contactUpdated</c>.
/// </summary>
public sealed record AutomationEventInput(string Kind, string? Field = null, string? Status = null, string? Sla = null, string? Metric = null);

/// <summary>Dry-run input; <see cref="Draft"/> is an unsaved rule in the automation rule codec format (evaluated instead of <see cref="Rules"/>).</summary>
public sealed record AutomationPreviewRequest(string ConversationId, AutomationEventInput Event, IReadOnlyList<string>? Rules = null, JsonElement? Draft = null, string? MessageBody = null);

/// <summary>What one action would do; <see cref="Action"/> is the action in the automation codec format.</summary>
public sealed record AutomationActionPreviewResponse(int Index, JsonElement Action, string? RenderedText, string? Note);

/// <summary><see cref="Match"/> is <c>matched</c>, <c>notMatched</c>, <c>unknown</c>, <c>notTriggered</c> or <c>disabled</c>.</summary>
public sealed record AutomationRulePreviewResponse(string RuleId, string Name, string Match, IReadOnlyList<AutomationActionPreviewResponse> Actions);

public sealed record AutomationDryRunResponse(string ConversationId, IReadOnlyList<AutomationRulePreviewResponse> Rules, string? Stopped, DateTimeOffset EvaluatedAt);

public sealed record AutomationReplayWindowRequest(string ConversationId, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<string>? Rules = null);

public sealed record AutomationCauseResponse(Guid Execution, string RuleId, int Depth);

/// <summary>An action outcome: <see cref="Kind"/> is <c>applied</c> (<see cref="Sequences"/>), <c>skipped</c>, <c>refused</c> or <c>failed</c> (<see cref="Reason"/>).</summary>
public sealed record AutomationActionOutcomeResponse(string Kind, IReadOnlyList<long>? Sequences = null, string? Reason = null);

public sealed record AutomationExecutionResponse(
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
    IReadOnlyList<AutomationActionOutcomeResponse> Outcomes);

public sealed record AutomationReplayItemResponse(long Sequence, DateTimeOffset OccurredAt, string EventKind, AutomationCauseResponse? Cause, IReadOnlyList<AutomationRulePreviewResponse> Previews,
    IReadOnlyList<AutomationExecutionResponse> Executions);

public sealed record AutomationReplayResponse(string ConversationId, IReadOnlyList<AutomationReplayItemResponse> Items, bool Truncated);

public sealed record MacroRunRequest(string ConversationId);

public sealed record MacroRunResponse(string MacroId, string ConversationId, Guid Run, IReadOnlyList<AutomationActionOutcomeResponse> Outcomes, bool Completed);

public sealed record CannedResponseRenderRequest(string ConversationId, string? Locale = null);

public sealed record CannedResponseRenderResponse(string ResponseId, string Locale, string ContentType, string Body);
