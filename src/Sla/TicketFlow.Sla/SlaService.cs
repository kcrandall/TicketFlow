using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Sla;

/// <summary>
/// SLA status and risk queries. Status requires <c>sla:read</c> on the live conversation resource (existence is checked first, like
/// the conversation service). Risk lists plan <c>sla:read</c> into the tracker store query (over the mirrored conversation Access
/// resource) and point-check every returned row against the live conversation, so stale mirrors never leak a row.
/// </summary>
public sealed class SlaService : ISlaService
{
    private readonly IConversationStore conversations;
    private readonly ISlaTrackerStore trackers;
    private readonly IBusinessCalendarStore calendars;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;
    private readonly TimeProvider time;

    public SlaService(IConversationStore conversations, ISlaTrackerStore trackers, IBusinessCalendarStore calendars, IAccessEvaluator evaluator, IAccessQueryPlanner planner,
        TimeProvider time)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.trackers = trackers ?? throw new ArgumentNullException(nameof(trackers));
        this.calendars = calendars ?? throw new ArgumentNullException(nameof(calendars));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<SlaResult<SlaStatusView>> GetStatusAsync(PrincipalRef actor, ConversationId conversation, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        var stored = await conversations.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return SlaNotFound.Instance;
        if (!await PermittedAsync(actor, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false))
            return SlaRefused.Forbidden;
        var now = time.GetUtcNow();
        var tracker = await trackers.GetAsync(scope, conversation, cancellationToken).ConfigureAwait(false);
        if (tracker is null)
            return new SlaValue<SlaStatusView>(new SlaStatusView(conversation, stored.Conversation.Inbox, null, null, null, false, [], false, false, null, now));
        return new SlaValue<SlaStatusView>(await ViewAsync(scope, tracker.Tracker, now, calendarCache: null, cancellationToken).ConfigureAwait(false));
    }

    public async Task<SlaResult<SlaRiskPage>> ListAsync(PrincipalRef actor, SlaRiskQuery query, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        string? fingerprint = null;
        SlaRiskCursor? after = null;
        if (cursor is not null)
        {
            fingerprint = Fingerprint(query);
            if (!TryDecodeCursor(cursor, fingerprint, out after))
                return SlaRefused.Invalid("The cursor is invalid or belongs to a different query.");
        }
        var now = time.GetUtcNow();
        var filter = await planner.PlanAsync(new AccessQuery(actor, SlaPermissions.Read, scope), cancellationToken).ConfigureAwait(false);
        var page = await trackers.ListAsync(scope, query.WithAfter(after ?? query.After), now, filter, cancellationToken).ConfigureAwait(false);
        if (page.Trackers.Count == 0)
            return new SlaValue<SlaRiskPage>(new SlaRiskPage([], page.Next is { } emptyNext ? EncodeCursor(emptyNext, fingerprint ?? Fingerprint(query)) : null));

        var ids = new ConversationId[page.Trackers.Count];
        for (var i = 0; i < page.Trackers.Count; i++)
        {
            if (page.Trackers[i].Scope != scope)
                throw new InvalidOperationException("The SLA tracker store returned a record outside the requested scope.");
            ids[i] = page.Trackers[i].Conversation;
        }
        var convTask = conversations.GetManyAsync(scope, ids, cancellationToken);
        var contextTask = evaluator.LoadDecisionContextAsync(actor, scope, [SlaPermissions.Read], cancellationToken);
        await Task.WhenAll(convTask, contextTask).ConfigureAwait(false);
        var loaded = convTask.Result;
        var decisionContext = contextTask.Result;
        var calendarCache = new Dictionary<BusinessCalendarId, SlaClockTime?>();
        var items = new List<SlaStatusView>(page.Trackers.Count);
        for (var i = 0; i < page.Trackers.Count; i++)
        {
            var tracker = page.Trackers[i];
            if (!loaded.TryGetValue(tracker.Conversation, out var stored))
                continue;
            if (!(await decisionContext.DecideAsync(SlaPermissions.Read, ConversationAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
                continue;
            items.Add(await ViewAsync(scope, tracker.Tracker, now, calendarCache, cancellationToken).ConfigureAwait(false));
        }
        return new SlaValue<SlaRiskPage>(new SlaRiskPage(items, page.Next is { } next ? EncodeCursor(next, fingerprint ?? Fingerprint(query)) : null));
    }

    private async Task<SlaStatusView> ViewAsync(TicketScope scope, SlaTracker tracker, DateTimeOffset now, Dictionary<BusinessCalendarId, SlaClockTime?>? calendarCache,
        CancellationToken cancellationToken)
    {
        SlaClockTime? business = null;
        if (tracker.Applied?.Calendar is { } calendarId)
        {
            if (calendarCache is not null && calendarCache.TryGetValue(calendarId, out var cached))
                business = cached;
            else
            {
                if (await calendars.GetAsync(scope, calendarId, cancellationToken).ConfigureAwait(false) is { } calendar)
                    business = SlaClockTime.Business(new BusinessCalendarCalculator(calendar.Calendar));
                if (calendarCache is not null)
                    calendarCache[calendarId] = business;
            }
        }
        return SlaEngine.View(tracker, id => id == tracker.Applied?.Calendar ? business : null, now);
    }

    private async Task<bool> PermittedAsync(PrincipalRef actor, AccessResource resource, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, SlaPermissions.Read, resource), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    private static string Fingerprint(SlaRiskQuery query)
    {
        var inbox = query.Inbox?.Value ?? "";
        var byteLen = Encoding.UTF8.GetByteCount(inbox) + 32;
        Span<byte> buf = byteLen <= 512 ? stackalloc byte[byteLen] : new byte[byteLen];
        Utf8Formatter.TryFormat((int)query.Filter, buf, out var pos);
        buf[pos++] = 0x1F;
        if (inbox.Length > 0)
            pos += Encoding.UTF8.GetBytes(inbox.AsSpan(), buf[pos..]);
        buf[pos++] = 0x1F;
        Utf8Formatter.TryFormat(query.Limit, buf[pos..], out var limLen);
        pos += limLen;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buf[..pos], hash);
        return Convert.ToHexStringLower(hash[..8]);
    }

    private static string EncodeCursor(SlaRiskCursor cursor, string fingerprint) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"v1|{fingerprint}|{cursor.UrgencyAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{cursor.Conversation.Value}"));

    private static bool TryDecodeCursor(string token, string fingerprint, out SlaRiskCursor? cursor)
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
            cursor = new SlaRiskCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new ConversationId(parts[3]));
            return true;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
