using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Mcp.Tools;

/// <summary>Conversation tools over <see cref="IConversationService"/>; every call runs as the caller and is decided by Access.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Conversations)]
public sealed class ConversationTools(TicketFlowToolContext context)
{
    internal const string WorkspaceHelp = "Workspace identifier; defaults to the server's default workspace.";
    internal const string RevisionHelp = "Optional expected conversation revision (from a previous read); a mismatch returns 'conflict' with currentRevision.";

    private IConversationService Service => context.Service<IConversationService>();

    [McpServerTool(Name = "conversations_list", Title = "List conversations", ReadOnly = true, Idempotent = true)]
    [Description("Lists conversations the caller may read (Access-filtered inside the store query), newest activity first, with optional filters. Returns summaries and an opaque 'next' cursor.")]
    [TicketFlowTool("conversation:read")]
    public Task<CallToolResult> List(
        [Description("Only this inbox.")] string? inboxId = null,
        [Description("Only this status: open, pending, snoozed, resolved or closed.")] string? status = null,
        [Description("Only this assignee user id, or 'me' for the calling user.")] string? assignee = null,
        [Description("Only unassigned conversations.")] bool unassigned = false,
        [Description("Only this team.")] string? teamId = null,
        [Description("Only conversations with this label.")] string? label = null,
        [Description("Only this contact.")] string? contactId = null,
        [Description("Page size, 1-100 (default 25).")] int limit = ConversationListQuery.DefaultLimit,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            UserId? assigneeId = assignee switch
            {
                null => null,
                "me" => principal.Value is UserPrincipal user ? user.UserId : throw new ArgumentException("'me' requires a user principal.", nameof(assignee)),
                _ => new UserId(assignee),
            };
            var query = new ConversationListQuery(inboxId is null ? null : new InboxId(inboxId), TicketFlowToolJson.ParseOptional<ConversationStatusKind>(status, nameof(status)),
                assigneeId, unassigned, teamId is null ? null : new TeamId(teamId), label is null ? null : new LabelId(label), contactId is null ? null : new ContactId(contactId), limit);
            return await Service.ListAsync(principal, query, cursor, cancellationToken) switch
            {
                ConversationListPage page => new { items = page.Conversations.Select(ToolViews.ConversationSummary).ToList(), next = page.NextCursor },
                ConversationRefused refused => ToolViews.Refused(refused),
            };
        }, cancellationToken);

    [McpServerTool(Name = "conversations_get", Title = "Get conversation", ReadOnly = true, Idempotent = true)]
    [Description("Reads one conversation (masked server-side: masked attribute values and channel identifiers are absent and listed in maskedFields). The revision fences later writes.")]
    [TicketFlowTool("conversation:read")]
    public Task<CallToolResult> Get(
        [Description("Conversation id.")] string conversationId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => await Service.GetAsync(principal, new ConversationId(conversationId), cancellationToken) switch
        {
            ConversationView view => ToolViews.Conversation(view),
            ConversationNotFound => TicketFlowToolError.NotFound("The conversation"),
            ConversationRefused refused => ToolViews.Refused(refused),
        }, cancellationToken);

    [McpServerTool(Name = "conversations_timeline", Title = "Read timeline", ReadOnly = true, Idempotent = true)]
    [Description("Reads a page of a conversation's timeline (messages, private notes, assignment/status/label changes) in sequence order. Private notes appear only when the caller holds note:read (notesIncluded). Long bodies are truncated (truncated: true).")]
    [TicketFlowTool("conversation:read", Optional = ["note:read"])]
    public Task<CallToolResult> Timeline(
        [Description("Conversation id.")] string conversationId,
        [Description("Return events after this sequence (default 0 = from the start); use nextAfterSequence to continue.")] long afterSequence = 0,
        [Description("Page size, 1-200 (default 50).")] int limit = 50,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => await Service.ReadTimelineAsync(principal, new ConversationId(conversationId), afterSequence, limit, cancellationToken) switch
        {
            TimelinePage page => new
            {
                conversationId = page.Conversation.Value,
                events = page.Events.Select(item => ToolViews.TimelineEvent(item, context)).ToList(),
                nextAfterSequence = page.NextAfterSequence,
                notesIncluded = page.NotesIncluded,
            },
            ConversationNotFound => TicketFlowToolError.NotFound("The conversation"),
            ConversationRefused refused => ToolViews.Refused(refused),
        }, cancellationToken);

    [McpServerTool(Name = "conversations_start", Title = "Start conversation", Destructive = false, Idempotent = false)]
    [Description("Starts a conversation for an existing contact in an enabled inbox (no initial message; send one with conversations_reply).")]
    [TicketFlowTool("conversation:create")]
    public Task<CallToolResult> Start(
        [Description("Inbox id.")] string inboxId,
        [Description("Contact id.")] string contactId,
        [Description("Priority: none, low, medium, high or urgent.")] string? priority = null,
        [Description("Labels to apply.")] string[]? labels = null,
        [Description("Team id.")] string? teamId = null,
        [Description("Assignee user id.")] string? assigneeId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var request = new StartConversationRequest(new InboxId(inboxId), new ContactId(contactId), null,
                priority is null ? ConversationPriority.None : TicketFlowToolJson.Parse<ConversationPriority>(priority, nameof(priority)),
                labels?.Select(label => new LabelId(label)).ToList(), null, teamId is null ? null : new TeamId(teamId), assigneeId is null ? null : new UserId(assigneeId));
            return ToolViews.Map(await Service.StartAsync(principal, request, cancellationToken));
        }, cancellationToken);

    [McpServerTool(Name = "conversations_reply", Title = "Reply", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Sends an outgoing reply to the contact (recorded as pending and delivered by the inbox's channel). This message is visible to the customer.")]
    [TicketFlowTool("conversation:reply")]
    public Task<CallToolResult> Reply(
        [Description("Conversation id.")] string conversationId,
        [Description("Reply body.")] string body,
        [Description("Content type: plainText (default) or markdown.")] string? contentType = null,
        [Description(RevisionHelp)] long? expectedRevision = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => ToolViews.Map(await Service.ReplyAsync(principal, new ConversationId(conversationId),
            new OutgoingMessageDraft(body, contentType is null ? MessageContentType.PlainText : TicketFlowToolJson.Parse<MessageContentType>(contentType, nameof(contentType))),
            expectedRevision, cancellationToken)), cancellationToken);

    [McpServerTool(Name = "conversations_add_note", Title = "Add private note", Destructive = false, Idempotent = false)]
    [Description("Adds a private note (visible to agents with note:read, never to the contact).")]
    [TicketFlowTool("note:write")]
    public Task<CallToolResult> AddNote(
        [Description("Conversation id.")] string conversationId,
        [Description("Note body.")] string body,
        [Description(RevisionHelp)] long? expectedRevision = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => ToolViews.Map(await Service.AddNoteAsync(principal, new ConversationId(conversationId), new NoteDraft(body),
            expectedRevision, cancellationToken)), cancellationToken);

    [McpServerTool(Name = "conversations_assign", Title = "Assign", Destructive = false, Idempotent = true)]
    [Description("Assigns the conversation to a user ('me' for the calling user) or unassigns it (omit assigneeId).")]
    [TicketFlowTool("conversation:assign")]
    public Task<CallToolResult> Assign(
        [Description("Conversation id.")] string conversationId,
        [Description("Assignee user id, 'me', or omit to unassign.")] string? assigneeId = null,
        [Description(RevisionHelp)] long? expectedRevision = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            UserId? assignee = assigneeId switch
            {
                null => null,
                "me" => principal.Value is UserPrincipal user ? user.UserId : throw new ArgumentException("'me' requires a user principal.", nameof(assigneeId)),
                _ => new UserId(assigneeId),
            };
            return ToolViews.Map(await Service.AssignAsync(principal, new ConversationId(conversationId), assignee, expectedRevision, cancellationToken));
        }, cancellationToken);

    [McpServerTool(Name = "conversations_set_team", Title = "Set team", Destructive = false, Idempotent = true)]
    [Description("Moves the conversation to a team, or clears the team (omit teamId).")]
    [TicketFlowTool("conversation:assign")]
    public Task<CallToolResult> SetTeam(
        [Description("Conversation id.")] string conversationId,
        [Description("Team id, or omit to clear.")] string? teamId = null,
        [Description(RevisionHelp)] long? expectedRevision = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => ToolViews.Map(await Service.SetTeamAsync(principal, new ConversationId(conversationId),
            teamId is null ? null : new TeamId(teamId), expectedRevision, cancellationToken)), cancellationToken);

    [McpServerTool(Name = "conversations_set_status", Title = "Set status", Destructive = false, Idempotent = true)]
    [Description("Changes the status: open, pending, snoozed (requires snoozedUntil in the future), resolved or closed (terminal: a closed conversation accepts no further changes).")]
    [TicketFlowTool("conversation:resolve")]
    public Task<CallToolResult> SetStatus(
        [Description("Conversation id.")] string conversationId,
        [Description("open, pending, snoozed, resolved or closed.")] string status,
        [Description("Snooze end (ISO 8601) when status is snoozed.")] DateTimeOffset? snoozedUntil = null,
        [Description(RevisionHelp)] long? expectedRevision = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var kind = TicketFlowToolJson.Parse<ConversationStatusKind>(status, nameof(status));
            if (kind == ConversationStatusKind.Snoozed && snoozedUntil is null)
                throw new ArgumentException("Snoozing requires 'snoozedUntil'.", nameof(snoozedUntil));
            return ToolViews.Map(await Service.ChangeStatusAsync(principal, new ConversationId(conversationId), ConversationStatus.FromKind(kind, snoozedUntil),
                expectedRevision, cancellationToken));
        }, cancellationToken);

    [McpServerTool(Name = "conversations_set_priority", Title = "Set priority", Destructive = false, Idempotent = true)]
    [Description("Sets the priority: none, low, medium, high or urgent.")]
    [TicketFlowTool("conversation:manage")]
    public Task<CallToolResult> SetPriority(
        [Description("Conversation id.")] string conversationId,
        [Description("none, low, medium, high or urgent.")] string priority,
        [Description(RevisionHelp)] long? expectedRevision = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => ToolViews.Map(await Service.SetPriorityAsync(principal, new ConversationId(conversationId),
            TicketFlowToolJson.Parse<ConversationPriority>(priority, nameof(priority)), expectedRevision, cancellationToken)), cancellationToken);

    [McpServerTool(Name = "conversations_update_labels", Title = "Update labels", Destructive = false, Idempotent = true)]
    [Description("Adds and/or removes labels.")]
    [TicketFlowTool("conversation:manage")]
    public Task<CallToolResult> UpdateLabels(
        [Description("Conversation id.")] string conversationId,
        [Description("Labels to add.")] string[]? add = null,
        [Description("Labels to remove.")] string[]? remove = null,
        [Description(RevisionHelp)] long? expectedRevision = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => ToolViews.Map(await Service.UpdateLabelsAsync(principal, new ConversationId(conversationId),
            (add ?? []).Select(label => new LabelId(label)), (remove ?? []).Select(label => new LabelId(label)), expectedRevision, cancellationToken)), cancellationToken);

    [McpServerTool(Name = "conversations_delete", Title = "Delete conversation", Destructive = true, Idempotent = false)]
    [Description("Permanently deletes a conversation and its timeline (sensitive; audited by Access). Requires the current revision.")]
    [TicketFlowTool("conversation:delete")]
    public Task<CallToolResult> Delete(
        [Description("Conversation id.")] string conversationId,
        [Description("The current conversation revision (required).")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => ToolViews.Map(await Service.DeleteAsync(principal, new ConversationId(conversationId), expectedRevision, cancellationToken)),
            cancellationToken);
}
