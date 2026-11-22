using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Assist;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Mcp.Tools;

/// <summary>
/// Assistant tools over <see cref="IAssistService"/>: suggestions built only from what the caller may see (masking applies). Nothing
/// is applied; act on suggestions with the conversation tools.
/// </summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Assist)]
public sealed class AssistTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    private IAssistService Service => context.Service<IAssistService>();

    [McpServerTool(Name = "assist_summarize", Title = "Summarize conversation", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Summarizes a conversation (summary, key points, suggested next step) with the workspace's configured assistant model.")]
    [TicketFlowTool("conversation:read", "assist:use")]
    public Task<CallToolResult> Summarize(
        [Description("Conversation id.")] string conversationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.SummarizeAsync(principal, new AssistSummaryRequest(new ConversationId(conversationId)), cancellationToken),
            summary => new { summary = summary.Summary, keyPoints = summary.KeyPoints, nextStep = summary.NextStep }), cancellationToken);

    [McpServerTool(Name = "assist_draft_reply", Title = "Draft reply", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Drafts a reply, optionally grounded in published help center articles of a portal and locale (citations returned). Nothing is sent.")]
    [TicketFlowTool("conversation:read", "assist:use", Optional = ["helpcenter:read"])]
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
            return Map(await Service.DraftReplyAsync(principal, new AssistDraftRequest(new ConversationId(conversationId), grounding, instructions), cancellationToken), draft => new
            {
                body = draft.Body,
                grounded = draft.Grounded,
                citations = draft.Citations.Select(citation => new
                {
                    articleId = citation.Article.Value,
                    portalId = citation.Portal.Value,
                    locale = citation.Locale.Value,
                    title = citation.Title,
                    slug = citation.Slug.Value,
                    revisionNumber = citation.RevisionNumber,
                }).ToList(),
            });
        }, cancellationToken);

    [McpServerTool(Name = "assist_triage", Title = "Triage conversation", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Suggests labels (from the given options), a team (from the given options) and a priority, with confidences and a rationale. Nothing is applied.")]
    [TicketFlowTool("conversation:read", "assist:use")]
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
            return Map(await Service.TriageAsync(principal, request, cancellationToken), triage => new
            {
                labels = triage.Labels.Select(label => new { id = label.Label.Value, confidence = label.Confidence }).ToList(),
                team = triage.Team is { } team ? new { id = team.Team.Value, confidence = team.Confidence } : null,
                priority = triage.Priority is { } priority ? new { id = TicketFlowToolJson.Token(priority.Priority), confidence = priority.Confidence } : null,
                rationale = triage.Rationale,
            });
        }, cancellationToken);

    private static object Map<T>(AssistResult<T> result, Func<T, object> project) => result switch
    {
        AssistSucceeded<T> succeeded => new
        {
            result = project(succeeded.Value),
            usage = new
            {
                model = succeeded.Model,
                inputTokens = succeeded.Usage.InputTokens,
                outputTokens = succeeded.Usage.OutputTokens,
                usageId = succeeded.UsageId,
            },
        },
        AssistRefused refused => refused.Reason switch
        {
            AssistRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            AssistRefusal.NotFound => TicketFlowToolError.NotFound("The conversation"),
            AssistRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            AssistRefusal.Disabled => new TicketFlowToolError("assist-disabled", "The assistant is disabled for this workspace."),
            AssistRefusal.QuotaExceeded => new TicketFlowToolError("quota-exceeded", "The workspace's assistant quota is exhausted.", Details: Retry(refused.RetryAfter)),
            AssistRefusal.RateLimited => new TicketFlowToolError("rate-limited", "The workspace's assistant rate limit is reached.", Details: Retry(refused.RetryAfter)),
            AssistRefusal.ModelRefused => new TicketFlowToolError("model-refused", "The model declined the request."),
            AssistRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
        AssistProviderFailed failed => new TicketFlowToolError("provider-failed", "The assistant model provider failed.", [TicketFlowToolJson.Token(failed.Failure)],
            Details: new Dictionary<string, object?> { ["retryable"] = failed.Retryable, ["retryAfterSeconds"] = failed.RetryAfter?.TotalSeconds }),
    };

    private static Dictionary<string, object?> Retry(TimeSpan? retryAfter) => new() { ["retryAfterSeconds"] = retryAfter?.TotalSeconds };
}
