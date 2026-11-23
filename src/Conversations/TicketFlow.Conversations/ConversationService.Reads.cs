using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Conversations;

public sealed partial class ConversationService
{
    public async Task<ConversationReadResult> GetAsync(PrincipalRef actor, ConversationId id, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ConversationNotFound.Instance;
        var decision = await DecideAsync(actor, ConversationPermissions.Read, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
        if (!decision.IsPermitted)
            return ConversationRefused.Forbidden;
        var masking = stored.Conversation.Attributes.Count == 0
            ? ConversationMasking.Empty
            : await MaskingAsync(scope, cancellationToken).ConfigureAwait(false);
        return masking.Project(stored, decision);
    }

    public async Task<TimelineReadResult> ReadTimelineAsync(PrincipalRef actor, ConversationId id, long afterSequence = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        if (afterSequence < 0 || limit is < 1 or > IConversationStore.MaximumTimelineRead)
            return ConversationRefused.Invalid($"Timeline reads start at a non-negative sequence and return 1-{IConversationStore.MaximumTimelineRead} events.");
        var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ConversationNotFound.Instance;
        var resource = ConversationAccess.Resource(stored);
        // One access snapshot decides both permissions.
        var access = await evaluator.LoadDecisionContextAsync(actor, scope, [ConversationPermissions.Read, ConversationPermissions.NoteRead], cancellationToken).ConfigureAwait(false);
        var decision = await access.DecideAsync(ConversationPermissions.Read, resource, cancellationToken).ConfigureAwait(false);
        if (!decision.IsPermitted)
            return ConversationRefused.Forbidden;
        var includeNotes = (await access.DecideAsync(ConversationPermissions.NoteRead, resource, cancellationToken).ConfigureAwait(false)).IsPermitted;
        var events = await store.ReadTimelineAsync(scope, id, afterSequence, limit, includeNotes, cancellationToken).ConfigureAwait(false);
        var needsAttributes = false;
        for (var i = 0; i < events.Count; i++)
            if (events[i].Event.Value is AttributeChanged)
            {
                needsAttributes = true;
                break;
            }
        var masking = needsAttributes
            ? await MaskingAsync(scope, cancellationToken).ConfigureAwait(false)
            : ConversationMasking.Empty;
        // Defense in depth: never return a note to a principal without note:read, whatever the store returned.
        var views = new List<TimelineEventView>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            if (includeNotes || item.Event.Kind != ConversationEventKind.Note)
                views.Add(masking.Project(item, decision));
        }
        return new TimelinePage(id, views, events.Count == limit ? events[^1].Sequence : null, includeNotes);
    }

    public async Task<ConversationListResult> ListAsync(PrincipalRef actor, ConversationListQuery query, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        string? fingerprint = null;
        ConversationListCursor? after = null;
        if (cursor is not null)
        {
            fingerprint = Fingerprint(query);
            if (!TryDecodeCursor(cursor, fingerprint, out after))
                return ConversationRefused.Invalid("The cursor is invalid or belongs to a different query.");
        }
        var filter = await planner.PlanAsync(new AccessQuery(actor, ConversationPermissions.Read, scope), cancellationToken).ConfigureAwait(false);
        var page = await store.ListAsync(scope, after is not null ? query.WithAfter(after) : query, filter, cancellationToken).ConfigureAwait(false);
        if (page.Conversations.Count == 0)
            return new ConversationListPage([], page.Next is { } emptyNext ? EncodeCursor(emptyNext, fingerprint ?? Fingerprint(query)) : null);
        var hasAttributes = false;
        for (var i = 0; i < page.Conversations.Count; i++)
            if (page.Conversations[i].Conversation.Attributes.Count > 0)
            {
                hasAttributes = true;
                break;
            }
        ConversationMasking masking;
        IAccessDecisionContext access;
        if (hasAttributes)
        {
            var maskingTask = MaskingAsync(scope, cancellationToken);
            var accessTask = evaluator.LoadDecisionContextAsync(actor, scope, [ConversationPermissions.Read], cancellationToken);
            await Task.WhenAll(maskingTask, accessTask).ConfigureAwait(false);
            masking = maskingTask.Result;
            access = accessTask.Result;
        }
        else
        {
            masking = ConversationMasking.Empty;
            access = await evaluator.LoadDecisionContextAsync(actor, scope, [ConversationPermissions.Read], cancellationToken).ConfigureAwait(false);
        }
        var views = new List<ConversationView>(page.Conversations.Count);
        foreach (var stored in page.Conversations)
        {
            if (stored.Scope != scope)
                throw new InvalidOperationException("The conversation store returned a record outside the requested scope.");
            // Masking depends on the grants that apply to each row, so every row is decided against one access snapshot (no store
            // round trip per row); the filter and the decision agree.
            var decision = await access.DecideAsync(ConversationPermissions.Read, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
            if (decision.IsPermitted)
                views.Add(masking.Project(stored, decision));
        }
        return new ConversationListPage(views, page.Next is { } next ? EncodeCursor(next, fingerprint ?? Fingerprint(query)) : null);
    }

    public async Task<ConversationReadResult> FindByThreadAsync(PrincipalRef actor, InboxId inbox, ChannelThreadRef thread, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (inbox == default)
            throw new ArgumentException("An inbox identifier is required.", nameof(inbox));
        ArgumentNullException.ThrowIfNull(thread);
        var stored = await store.FindByThreadAsync(scope, inbox, thread, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ConversationNotFound.Instance;
        var resource = ConversationAccess.Resource(stored);
        var access = await evaluator.LoadDecisionContextAsync(actor, scope, [ConversationPermissions.Ingest, ConversationPermissions.Read], cancellationToken).ConfigureAwait(false);
        if (!(await access.DecideAsync(ConversationPermissions.Ingest, resource, cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ConversationRefused.Forbidden;
        // Masked per conversation:read; a principal without read sees every maskable field masked.
        var decision = await access.DecideAsync(ConversationPermissions.Read, resource, cancellationToken).ConfigureAwait(false);
        var masking = stored.Conversation.Attributes.Count == 0
            ? ConversationMasking.Empty
            : await MaskingAsync(scope, cancellationToken).ConfigureAwait(false);
        return masking.Project(stored, decision);
    }

    private Task<ConversationMasking> MaskingAsync(TicketScope scope, CancellationToken cancellationToken) =>
        ConversationMasking.LoadAsync(scope, ConversationPermissions.Read, definitions, policies, cancellationToken);

    /// <summary>A short hash of the query criteria so a cursor cannot be replayed against a different query.</summary>
    private static string Fingerprint(ConversationListQuery query)
    {
        var text = string.Join('\u001F', query.Inbox?.Value ?? "", query.Status?.ToString() ?? "", query.Assignee?.Value ?? "", query.Unassigned ? "1" : "0",
            query.Team?.Value ?? "", query.Label?.Value ?? "", query.Contact?.Value ?? "", query.Limit.ToString(CultureInfo.InvariantCulture));
        Span<byte> hash = stackalloc byte[32];
        var maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        if (maxBytes <= 1024)
        {
            Span<byte> utf8 = stackalloc byte[maxBytes];
            var len = Encoding.UTF8.GetBytes(text, utf8);
            SHA256.HashData(utf8[..len], hash);
        }
        else
            SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    private static string EncodeCursor(ConversationListCursor cursor, string fingerprint) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Join('|', "v1", fingerprint,
            cursor.LastActivityAt.UtcTicks.ToString(CultureInfo.InvariantCulture), cursor.Conversation.Value)));

    private static bool TryDecodeCursor(string token, string fingerprint, out ConversationListCursor? cursor)
    {
        cursor = null;
        try
        {
            if (token.Length > 2048)
                return false;
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token)).Split('|', 4);
            if (parts.Length != 4 || parts[0] != "v1" || parts[1] != fingerprint
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                return false;
            cursor = new ConversationListCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new ConversationId(parts[3]));
            return true;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
