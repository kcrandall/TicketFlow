using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>How a rule related to an event in a dry run. Never renumber.</summary>
public enum AutomationMatch
{
    /// <summary>The trigger fired and the condition was true: the actions would run.</summary>
    Matched = 1,

    /// <summary>The trigger fired but the condition was false.</summary>
    NotMatched = 2,

    /// <summary>The trigger fired but the condition was unknown (missing or masked data, regex timeout): the rule would not run.</summary>
    Unknown = 3,

    /// <summary>The trigger does not fire for this event.</summary>
    NotTriggered = 4,

    /// <summary>The rule is disabled.</summary>
    Disabled = 5,
}

/// <summary>
/// What an action would do. <see cref="RenderedText"/> is the reply or note body rendered from what the caller may see (masked values
/// omitted); <see cref="Note"/> is a non-sensitive token explaining a would-be skip (for example <c>canned-response-missing</c>).
/// </summary>
public sealed record AutomationActionPreview(int Index, AutomationAction Action, string? RenderedText, string? Note)
{
    public override string ToString() => $"AutomationActionPreview {{ Index = {Index}, Note = {Note} }}";
}

public sealed record AutomationRulePreview(AutomationRuleId Rule, string Name, AutomationMatch Match, IReadOnlyList<AutomationActionPreview> Actions);

/// <summary>
/// Dry-run input: evaluate <see cref="Rules"/> (all enabled and disabled rules of the workspace when <see langword="null"/>) or an unsaved
/// <see cref="Draft"/> against the live conversation for <see cref="Event"/>. <see cref="MessageBody"/> supplies a hypothetical message
/// for message conditions. Nothing is written.
/// </summary>
public sealed record AutomationDryRunRequest(
    ConversationId Conversation,
    AutomationEvent Event,
    IReadOnlyList<AutomationRuleId>? Rules = null,
    AutomationRule? Draft = null,
    string? MessageBody = null)
{
    public override string ToString() => $"AutomationDryRunRequest {{ Conversation = {Conversation}, Event = {Event.Kind} }}";
}

/// <summary>Rules in evaluation order; <see cref="Stopped"/> names the rule whose <see cref="AutomationRule.StopProcessing"/> would end evaluation.</summary>
public sealed record AutomationDryRunResult(ConversationId Conversation, IReadOnlyList<AutomationRulePreview> Rules, AutomationRuleId? Stopped, DateTimeOffset EvaluatedAt);

/// <summary>
/// Replay input: re-derive trigger events from the conversation's timeline in [<see cref="From"/>, <see cref="To"/>) (at most
/// <see cref="MaximumWindow"/>, at most <see cref="MaximumEvents"/> events scanned) and dry-run them against the current conversation.
/// </summary>
public sealed record AutomationReplayRequest
{
    public const int MaximumEvents = 500;
    public static TimeSpan MaximumWindow { get; } = TimeSpan.FromDays(7);

    public AutomationReplayRequest(ConversationId conversation, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<AutomationRuleId>? rules = null)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation is required.", nameof(conversation));
        if (to <= from || to - from > MaximumWindow)
            throw new ArgumentException("Replay windows are positive and at most 7 days.", nameof(to));
        Conversation = conversation;
        From = from;
        To = to;
        Rules = rules;
    }

    public ConversationId Conversation { get; }
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public IReadOnlyList<AutomationRuleId>? Rules { get; }
}

/// <summary>
/// One replayed timeline event: the derived trigger event, whether automation caused it (and at what chain depth), what the rules would
/// do now, and the executions actually recorded for it.
/// </summary>
public sealed record AutomationReplayItem(
    long Sequence,
    DateTimeOffset OccurredAt,
    AutomationEvent Event,
    AutomationCause? Cause,
    IReadOnlyList<AutomationRulePreview> Previews,
    IReadOnlyList<StoredAutomationExecution> Executions);

public sealed record AutomationReplayResult(ConversationId Conversation, IReadOnlyList<AutomationReplayItem> Items, bool Truncated);

/// <summary>
/// Access-checked automation rule management, dry runs, replays and the execution log. Reads require <c>automation:read</c>, writes
/// <c>automation:manage</c> (sensitive, audited by Access), at workspace level. Dry runs and replays additionally require
/// <c>conversation:read</c> on the conversation and evaluate against the caller's masked view.
/// </summary>
public interface IAutomationRuleService
{
    Task<TicketResult<StoredAutomationRule>> GetAsync(PrincipalRef actor, AutomationRuleId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<StoredAutomationRule>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredAutomationRule>> CreateAsync(PrincipalRef actor, AutomationRule rule, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredAutomationRule>> UpdateAsync(PrincipalRef actor, AutomationRule rule, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredAutomationRule>> DeleteAsync(PrincipalRef actor, AutomationRuleId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<AutomationDryRunResult>> DryRunAsync(PrincipalRef actor, AutomationDryRunRequest request, CancellationToken cancellationToken = default);

    Task<TicketResult<AutomationReplayResult>> ReplayAsync(PrincipalRef actor, AutomationReplayRequest request, CancellationToken cancellationToken = default);

    Task<TicketResult<AutomationExecutionPage>> ListExecutionsAsync(PrincipalRef actor, AutomationExecutionQuery query, CancellationToken cancellationToken = default);
}

/// <summary>The result of a macro run: one outcome per action, in order; execution stops at the first refused or failed action.</summary>
public sealed record MacroRunResult(MacroId Macro, ConversationId Conversation, Guid Run, IReadOnlyList<AutomationActionOutcome> Outcomes, bool Completed);

/// <summary>
/// Access-checked macros. Listing and reading return shared macros plus the caller's personal macros (<c>macro:run</c>); shared
/// macros are managed with <c>macro:manage</c>, personal macros by their owner with <c>macro:run</c>. <see cref="RunAsync"/> requires
/// <c>macro:run</c> on the conversation and executes every action through <see cref="IConversationService"/> as the agent, so each
/// action needs the agent's own permission.
/// </summary>
public interface IMacroService
{
    Task<TicketResult<StoredMacro>> GetAsync(PrincipalRef actor, MacroId id, CancellationToken cancellationToken = default);

    Task<TicketResult<IReadOnlyList<StoredMacro>>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredMacro>> CreateAsync(PrincipalRef actor, Macro macro, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredMacro>> UpdateAsync(PrincipalRef actor, Macro macro, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredMacro>> DeleteAsync(PrincipalRef actor, MacroId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<MacroRunResult>> RunAsync(PrincipalRef actor, MacroId id, ConversationId conversation, CancellationToken cancellationToken = default);
}

/// <summary>A canned response rendered for one conversation and principal. Masked values were never substituted.</summary>
public sealed record RenderedCannedResponse(CannedResponseId Response, string Locale, MessageContentType ContentType, string Body)
{
    public override string ToString() => $"RenderedCannedResponse {{ Response = {Response}, Locale = {Locale} }}";
}

/// <summary>
/// Access-checked canned responses. Reading, listing and rendering require <c>conversation:reply</c> or <c>canned-response:manage</c>
/// at workspace level (rendering additionally reads the conversation and contact as the caller); writes require
/// <c>canned-response:manage</c>.
/// </summary>
public interface ICannedResponseService
{
    Task<TicketResult<StoredCannedResponse>> GetAsync(PrincipalRef actor, CannedResponseId id, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCannedResponse>> FindByShortcutAsync(PrincipalRef actor, string shortcut, CancellationToken cancellationToken = default);

    Task<TicketResult<CannedResponsePage>> ListAsync(PrincipalRef actor, CannedResponseQuery query, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCannedResponse>> CreateAsync(PrincipalRef actor, CannedResponse response, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCannedResponse>> UpdateAsync(PrincipalRef actor, CannedResponse response, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredCannedResponse>> DeleteAsync(PrincipalRef actor, CannedResponseId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<TicketResult<RenderedCannedResponse>> RenderAsync(PrincipalRef actor, CannedResponseId id, ConversationId conversation, string? locale = null,
        CancellationToken cancellationToken = default);
}
