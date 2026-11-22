using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Assist;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>
/// Assistant tools over <see cref="IAssistService"/>: suggestions built only from what the caller may see (masking applies). Nothing
/// is applied; act on suggestions with the conversation tools. Model output is untrusted.
/// </summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Assist)]
public sealed class AssistTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    private IAssistService Service => context.Service<IAssistService>();

    [McpServerTool(Name = "assist_summarize", Title = "Summarize conversation", Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(AssistSummaryOutput))]
    [Description("Summarizes a conversation (summary, key points, suggested next step) with the workspace's configured assistant model." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.ConversationRead, P.AssistUse)]
    public Task<CallToolResult> Summarize(
        [Description("Conversation id.")] string conversationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.SummarizeAsync(principal, new AssistSummaryRequest(new ConversationId(conversationId)), cancellationToken),
            (summary, usage) => new AssistSummaryOutput(context.Untrusted(ToolViews.Model, summary.Summary), [.. summary.KeyPoints.Select(point => context.Untrusted(ToolViews.Model, point))],
                context.UntrustedOrNull(ToolViews.Model, summary.NextStep), usage)), cancellationToken);

    [McpServerTool(Name = "assist_draft_reply", Title = "Draft reply", Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(AssistDraftOutput))]
    [Description("Drafts a reply, optionally grounded in published help center articles of a portal and locale (citations returned). Nothing is sent." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.ConversationRead, P.AssistUse, Optional = [P.HelpCenterRead])]
    public Task<CallToolResult> DraftReply(
        [Description("Conversation id.")] string conversationId,
        [Description("Extra instructions for the draft.")] string? instructions = null,
        [Description("Ground in this help center portal.")] string? portalId = null,
        [Description("Grounding locale (with portalId).")] string? locale = null,
        [Description("Grounding search query (defaults to the conversation).")] string? groundingQuery = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            AssistGrounding? grounding = portalId is null ? null
                : new AssistGrounding(new PortalId(portalId), new HelpCenterLocale(locale ?? throw new ArgumentException("'locale' is required with 'portalId'.", nameof(locale))), groundingQuery);
            return Map(await Service.DraftReplyAsync(principal, new AssistDraftRequest(new ConversationId(conversationId), grounding, instructions), cancellationToken),
                (draft, usage) => new AssistDraftOutput(context.Untrusted(ToolViews.Model, draft.Body), draft.Grounded,
                    [.. draft.Citations.Select(citation => new AssistCitationOutput(citation.Article.Value, citation.Portal.Value, citation.Locale.Value,
                        context.Untrusted(ToolViews.Article, citation.Title), citation.Slug.Value, citation.RevisionNumber))], usage));
        }, cancellationToken);

    [McpServerTool(Name = "assist_triage", Title = "Triage conversation", Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(AssistTriageOutput))]
    [Description("Suggests labels (from the given options), a team (from the given options) and a priority, with confidences and a rationale. Nothing is applied." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.ConversationRead, P.AssistUse)]
    public Task<CallToolResult> Triage(
        [Description("Conversation id.")] string conversationId,
        [Description("Candidate label ids.")] string[]? labels = null,
        [Description("Candidate team ids.")] string[]? teams = null,
        [Description("Also suggest a priority (default true).")] bool suggestPriority = true,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var request = new AssistTriageRequest(new ConversationId(conversationId), labels?.Select(label => new AssistLabelOption(new LabelId(label), null)),
                teams?.Select(team => new AssistTeamOption(new TeamId(team), null)), suggestPriority);
            return Map(await Service.TriageAsync(principal, request, cancellationToken), (triage, usage) => new AssistTriageOutput(
                [.. triage.Labels.Select(label => new AssistSuggestionOutput(label.Label.Value, label.Confidence))],
                triage.Team is { } team ? new AssistSuggestionOutput(team.Team.Value, team.Confidence) : null,
                triage.Priority is { } priority ? new AssistSuggestionOutput(TicketFlowToolJson.Token(priority.Priority), priority.Confidence) : null,
                context.UntrustedOrNull(ToolViews.Model, triage.Rationale), usage));
        }, cancellationToken);

    private static object Map<T>(AssistResult<T> result, Func<T, AssistUsageOutput, object> project) => result switch
    {
        AssistSucceeded<T> succeeded => project(succeeded.Value,
            new AssistUsageOutput(succeeded.Model, succeeded.Usage.InputTokens, succeeded.Usage.OutputTokens, succeeded.UsageId)),
        AssistRefused refused => refused.Reason switch
        {
            AssistRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            AssistRefusal.NotFound => TicketFlowToolError.NotFound("The conversation"),
            AssistRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            AssistRefusal.Disabled => new TicketFlowToolError("assist-disabled", "The assistant is disabled for this workspace."),
            AssistRefusal.QuotaExceeded => new TicketFlowToolError("quota-exceeded", "The workspace's assistant quota is exhausted.") { RetryAfterSeconds = refused.RetryAfter?.TotalSeconds },
            AssistRefusal.RateLimited => new TicketFlowToolError("rate-limited", "The workspace's assistant rate limit is reached.") { RetryAfterSeconds = refused.RetryAfter?.TotalSeconds },
            AssistRefusal.ModelRefused => new TicketFlowToolError("model-refused", "The model declined the request."),
            AssistRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
        AssistProviderFailed failed => new TicketFlowToolError("provider-failed", "The assistant model provider failed.", [TicketFlowToolJson.Token(failed.Failure)])
        {
            Retryable = failed.Retryable,
            RetryAfterSeconds = failed.RetryAfter?.TotalSeconds,
        },
    };
}
