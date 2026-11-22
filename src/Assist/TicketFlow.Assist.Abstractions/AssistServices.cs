using TicketFlow.Core;

namespace TicketFlow.Assist;

/// <summary>
/// Assistant features. Every call authorizes the conversation read through the conversation service (masking applies), then
/// <c>assist:use</c> on the conversation, builds context only from what the actor can see, enforces workspace quotas and returns a
/// suggestion. Nothing is applied: callers act on suggestions through the normal commands. Summaries, drafts and triage are recorded
/// as <c>assist.summary</c>, <c>assist.draft</c> and <c>assist.triage</c> extension events with metadata only.
/// </summary>
public interface IAssistService
{
    Task<AssistResult<AssistSummary>> SummarizeAsync(PrincipalRef actor, AssistSummaryRequest request, CancellationToken cancellationToken = default);

    Task<AssistResult<AssistDraft>> DraftReplyAsync(PrincipalRef actor, AssistDraftRequest request, CancellationToken cancellationToken = default);

    Task<AssistResult<AssistTriage>> TriageAsync(PrincipalRef actor, AssistTriageRequest request, CancellationToken cancellationToken = default);

    Task<AssistResult<AssistSentimentAndIntent>> AnalyzeSentimentAsync(PrincipalRef actor, AssistSentimentRequest request, CancellationToken cancellationToken = default);

    Task<AssistResult<AssistTranslation>> TranslateAsync(PrincipalRef actor, AssistTranslateRequest request, CancellationToken cancellationToken = default);

    Task<AssistResult<AssistToneRewrite>> RewriteToneAsync(PrincipalRef actor, AssistToneRewriteRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Workspace assistant administration. Every call requires <c>assist:manage</c> on the workspace.</summary>
public interface IAssistAdministration
{
    /// <summary>The effective settings: the stored record, or the application defaults at revision 0.</summary>
    Task<AssistAdminResult<StoredAssistSettings>> GetSettingsAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    /// <summary>Stores settings fenced on <paramref name="expectedRevision"/> (0 creates). Every named model must be registered.</summary>
    Task<AssistAdminResult<StoredAssistSettings>> UpdateSettingsAsync(PrincipalRef actor, AssistSettings settings, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AssistAdminResult<AssistUsageSummary>> GetUsageAsync(PrincipalRef actor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}
