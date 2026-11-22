using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Core;
using P = TicketFlow.Mcp.TicketFlowMcpPermissions;

namespace TicketFlow.Mcp.Tools;

/// <summary>Contact tools over <see cref="IContactService"/> and <see cref="IContactLinkingService"/>. Every contact is masked server-side.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.Contacts)]
public sealed class ContactTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    private IContactService Service => context.Service<IContactService>();

    [McpServerTool(Name = "contacts_search", Title = "Search contacts", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<ContactOutput>))]
    [Description("Searches contacts the caller may read by display-name prefix, or by identity prefix for one kind (email, phone, whatsApp, widgetVisitor, socialHandle, externalUser...). Identity searches on a kind masked for the caller are refused. Returns masked contacts and a 'next' cursor." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.ContactRead)]
    public Task<CallToolResult> Search(
        [Description("Name or identity prefix (empty lists every contact by name).")] string? query = null,
        [Description("Identity kind to search instead of the name.")] string? kind = null,
        [Description("Identity namespace restriction (identity searches only).")] string? @namespace = null,
        [Description("Page size, 1-100 (default 25).")] int limit = 25,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var search = kind is null
                ? ContactSearch.ByName(query, limit)
                : ContactSearch.ByIdentity(TicketFlowToolJson.Parse<ContactIdentityKind>(kind, nameof(kind)), query ?? string.Empty, @namespace, limit);
            return await Service.SearchAsync(principal, search, cursor, cancellationToken) switch
            {
                ContactSearchPage page => new ToolPage<ContactOutput>([.. page.Contacts.Select(Contact)], page.NextCursor),
                ContactRefused refused => ToolViews.Refused(refused),
            };
        }, cancellationToken);

    [McpServerTool(Name = "contacts_get", Title = "Get contact", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ContactOutput))]
    [Description("Reads one contact, masked for the caller (masked identity and attribute values are absent and listed in maskedFields). A merged contact returns 'merged' with mergedInto." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.ContactRead)]
    public Task<CallToolResult> Get(
        [Description("Contact id.")] string contactId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.GetAsync(principal, new ContactId(contactId), cancellationToken), Contact), cancellationToken);

    [McpServerTool(Name = "contacts_create", Title = "Create contact", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ContactOutput))]
    [Description("Creates a contact with a profile and optional labels (identities are recorded by channels and account linking, not here).")]
    [TicketFlowTool(P.ContactCreate)]
    public Task<CallToolResult> Create(
        [Description("Display name.")] string? displayName = null,
        [Description("Company.")] string? company = null,
        [Description("BCP 47 locale, for example 'en-US'.")] string? locale = null,
        [Description("IANA time zone, for example 'Europe/Berlin'.")] string? timeZone = null,
        [Description("Labels.")] string[]? labels = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.CreateAsync(principal,
            new ContactDraft(new ContactProfile(displayName, locale: locale, timeZone: timeZone, company: company), Labels: labels?.Select(label => new LabelId(label)).ToList()),
            cancellationToken), Contact), cancellationToken);

    [McpServerTool(Name = "contacts_update_profile", Title = "Update contact profile", Destructive = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ContactOutput))]
    [Description("Updates profile fields (omitted fields keep their current values), fenced on the contact revision.")]
    [TicketFlowTool(P.ContactUpdate)]
    public Task<CallToolResult> UpdateProfile(
        [Description("Contact id.")] string contactId,
        [Description("The current contact revision.")] long expectedRevision,
        [Description("New display name.")] string? displayName = null,
        [Description("New company.")] string? company = null,
        [Description("New BCP 47 locale.")] string? locale = null,
        [Description("New IANA time zone.")] string? timeZone = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var id = new ContactId(contactId);
            var read = await Service.GetAsync(principal, id, cancellationToken);
            if (read is not ContactApplied<ContactView> applied)
                return Map(read, Contact);
            var current = applied.Value;
            var profile = new ContactProfile(displayName ?? current.Profile.DisplayName, current.Profile.AvatarRef, locale ?? current.Profile.Locale,
                timeZone ?? current.Profile.TimeZone, company ?? current.Profile.Company);
            return Map(await Service.UpdateAsync(principal, id, expectedRevision, [new SetContactProfile(profile)], cancellationToken), Contact);
        }, cancellationToken);

    [McpServerTool(Name = "contacts_reveal_field", Title = "Reveal masked contact field", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ContactRevealOutput))]
    [Description("Reveals ONE masked contact field (for example 'contact.email') when Access allows it. Every call, granted or refused, records an audit event; give a justification. Only reveal what the user explicitly needs." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.ContactReveal)]
    public Task<CallToolResult> RevealField(
        [Description("Contact id.")] string contactId,
        [Description("Masked field path from maskedFields, for example 'contact.email'.")] string field,
        [Description("Why the value is needed (recorded in the audit trail).")] string? justification = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => await Service.RevealAsync(principal, new ContactId(contactId), new FieldPath(field), justification, cancellationToken) switch
        {
            ContactFieldRevealed revealed => new ContactRevealOutput(revealed.Field.Value,
                [.. revealed.Identities.Select(identity => new RevealedIdentityOutput(TicketFlowToolJson.Token(identity.Kind), identity.Namespace, context.Untrusted(ToolViews.Contact, identity.Value)))],
                revealed.Attribute is { } attribute ? ToolViews.Value(attribute, context) : null, revealed.AuditEventId),
            ContactRevealRefused refused => new TicketFlowToolError("reveal-refused", "Access refused revealing this field; the refusal was audited.",
                [TicketFlowToolJson.Token(refused.Reason)]) { AuditEventId = refused.AuditEventId },
            ContactRefused refused => ToolViews.Refused(refused),
        }, cancellationToken);

    [McpServerTool(Name = "contacts_merge", Title = "Merge contacts", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ContactMergeOutput))]
    [Description("Merges the source contact into the target (identities, attributes and conversations follow the target), fenced on both revisions; audited and reversible with contacts_unmerge.")]
    [TicketFlowTool(P.ContactMerge)]
    public Task<CallToolResult> Merge(
        [Description("Surviving contact id.")] string targetId,
        [Description("Current target revision.")] long targetRevision,
        [Description("Contact id merged away.")] string sourceId,
        [Description("Current source revision.")] long sourceRevision,
        [Description("Attribute conflict resolution: preferTarget (default) or preferSource.")] string? attributeConflict = null,
        [Description("Justification recorded in the audit trail.")] string? justification = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var request = new ContactMergeRequest(new ContactId(targetId), targetRevision, new ContactId(sourceId), sourceRevision,
                attributeConflict is null ? ContactAttributeConflict.PreferTarget : TicketFlowToolJson.Parse<ContactAttributeConflict>(attributeConflict, nameof(attributeConflict)), justification);
            return Map(await Service.MergeAsync(principal, request, cancellationToken), outcome => new ContactMergeOutput(Contact(outcome.Target), outcome.MergeId, outcome.AuditEventId));
        }, cancellationToken);

    [McpServerTool(Name = "contacts_unmerge", Title = "Unmerge contacts", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ContactUnmergeOutput))]
    [Description("Reverses a merge by its merge id; audited.")]
    [TicketFlowTool(P.ContactMerge)]
    public Task<CallToolResult> Unmerge(
        [Description("Merge id returned by contacts_merge.")] Guid mergeId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.UnmergeAsync(principal, mergeId, cancellationToken),
            outcome => new ContactUnmergeOutput(Contact(outcome.Target), Contact(outcome.Source), outcome.AuditEventId)), cancellationToken);

    [McpServerTool(Name = "contacts_list_link_suggestions", Title = "List account-link suggestions", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<LinkSuggestionOutput>))]
    [Description("Lists suggestions to merge a contact into the contact holding a verified customer account (evidence kind only, never the address).")]
    [TicketFlowTool(P.ContactLink, Service = typeof(IContactLinkingService))]
    public Task<CallToolResult> ListLinkSuggestions(
        [Description("Status: pending (default), accepted, rejected or superseded.")] string? status = null,
        [Description("Only suggestions involving this contact.")] string? contactId = null,
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var query = new ContactLinkSuggestionQuery(status is null ? ContactLinkSuggestionStatus.Pending : TicketFlowToolJson.Parse<ContactLinkSuggestionStatus>(status, nameof(status)),
                contactId is null ? null : new ContactId(contactId), limit, cursor is null ? null : Guid.Parse(TicketFlowToolJson.DecodeCursor(cursor, 1)[0]));
            return Map(await context.Service<IContactLinkingService>().ListSuggestionsAsync(principal, query, cancellationToken), page => new ToolPage<LinkSuggestionOutput>(
                [.. page.Suggestions.Select(Suggestion)], page.Next is { } next ? TicketFlowToolJson.EncodeCursor(next.ToString("D")) : null));
        }, cancellationToken);

    [McpServerTool(Name = "contacts_accept_link_suggestion", Title = "Accept account-link suggestion", Destructive = true, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(LinkSuggestionAcceptedOutput))]
    [Description("Accepts a suggestion: merges the candidate contact into the account contact (audited). Suggestions resting on claimed (unverified) evidence need confirmClaimedEvidence = true, after confirming with the user.")]
    [TicketFlowTool(P.ContactLink, P.ContactMerge, Service = typeof(IContactLinkingService))]
    public Task<CallToolResult> AcceptLinkSuggestion(
        [Description("Suggestion id.")] Guid suggestionId,
        [Description("Current suggestion revision.")] long expectedRevision,
        [Description("Confirm accepting a suggestion that rests on claimed (unverified) evidence.")] bool confirmClaimedEvidence = false,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => await context.Service<IContactLinkingService>().AcceptSuggestionAsync(principal, suggestionId, expectedRevision,
            confirmClaimedEvidence, cancellationToken) switch
        {
            ContactLinkSuggestionAccepted accepted => new LinkSuggestionAcceptedOutput(Suggestion(accepted.Suggestion), Contact(accepted.Merge.Target), accepted.Merge.MergeId),
            ContactLinkConflict conflict => new TicketFlowToolError("link-conflict", "The contacts cannot be combined.", [TicketFlowToolJson.Token(conflict.Reason)]),
            ContactRefused refused => ToolViews.Refused(refused),
        }, cancellationToken);

    [McpServerTool(Name = "contacts_reject_link_suggestion", Title = "Reject account-link suggestion", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(LinkSuggestionOutput))]
    [Description("Rejects a suggestion (audited).")]
    [TicketFlowTool(P.ContactLink, Service = typeof(IContactLinkingService))]
    public Task<CallToolResult> RejectLinkSuggestion(
        [Description("Suggestion id.")] Guid suggestionId,
        [Description("Current suggestion revision.")] long expectedRevision,
        [Description("Justification recorded in the audit trail.")] string? justification = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IContactLinkingService>().RejectSuggestionAsync(principal, suggestionId, expectedRevision,
            justification, cancellationToken), Suggestion), cancellationToken);

    private ContactOutput Contact(ContactView view) => ToolViews.ContactView(view, context);

    private static LinkSuggestionOutput Suggestion(ContactLinkSuggestionView view) => new(view.Id, view.Revision, view.Candidate.Value, view.Account.Value,
        TicketFlowToolJson.Token(view.EvidenceKind), view.EvidenceVerified, TicketFlowToolJson.Token(view.Status), view.CreatedAt, view.UpdatedAt, view.Merge);

    private static object Map<T>(ContactResult<T> result, Func<T, object> success) => result switch
    {
        ContactApplied<T> applied => success(applied.Value),
        ContactRefused refused => ToolViews.Refused(refused),
    };
}
