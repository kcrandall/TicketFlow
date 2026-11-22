using TicketFlow.Assist;
using TicketFlow.Core;

namespace TicketFlow.UI.Blazor.Tests;

/// <summary>A deterministic assistant for smoke tests (only drafts and summaries are used by the UI).</summary>
internal sealed class TestAssist : IAssistService
{
    public const string Draft = "Sensitive AI draft for the customer.";

    private static AssistSucceeded<T> Ok<T>(T value) => new(value, AssistUsage.Zero, "test-model", Guid.NewGuid(), AssistRecording.NotApplicable);

    public Task<AssistResult<AssistSummary>> SummarizeAsync(PrincipalRef actor, AssistSummaryRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<AssistResult<AssistSummary>>(Ok(new AssistSummary("Customer was charged twice.", ["Refund started"], "Confirm refund timing")));

    public Task<AssistResult<AssistDraft>> DraftReplyAsync(PrincipalRef actor, AssistDraftRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<AssistResult<AssistDraft>>(Ok(new AssistDraft(Draft, [], false)));

    public Task<AssistResult<AssistTriage>> TriageAsync(PrincipalRef actor, AssistTriageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AssistResult<AssistSentimentAndIntent>> AnalyzeSentimentAsync(PrincipalRef actor, AssistSentimentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AssistResult<AssistTranslation>> TranslateAsync(PrincipalRef actor, AssistTranslateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AssistResult<AssistToneRewrite>> RewriteToneAsync(PrincipalRef actor, AssistToneRewriteRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
