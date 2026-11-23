using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

public sealed partial class ContactService
{
    private const int MaximumResolveAttempts = 3;
    private const int MaximumCursorLength = 4096;

    public async Task<ContactResolution> ResolveAsync(PrincipalRef actor, ContactResolveRequest request, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        var claimedMatching = request.ClaimedMatching ?? options.ClaimedIdentityMatching;
        for (var attempt = 1; ; attempt++)
        {
            var strong = new SortedSet<string>(StringComparer.Ordinal);
            var weak = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var evidence in request.Evidence)
            {
                foreach (var owner in await store.FindByIdentityAsync(scope, evidence.Address, cancellationToken).ConfigureAwait(false))
                {
                    if (evidence.Verification.IsVerified && owner.Verification.IsVerified)
                        strong.Add(owner.Contact.Value);
                    else
                        weak.Add(owner.Contact.Value);
                }
            }
            weak.ExceptWith(strong);

            if (strong.Count > 1)
                return await AmbiguousAsync(actor, strong, cancellationToken).ConfigureAwait(false);
            if (strong.Count == 1)
                return await MatchAsync(actor, new ContactId(strong.Min!), ContactMatchStrength.Verified, cancellationToken).ConfigureAwait(false);
            if (claimedMatching == ClaimedIdentityMatching.WhenUnique && weak.Count > 1)
                return await AmbiguousAsync(actor, weak, cancellationToken).ConfigureAwait(false);
            if (claimedMatching == ClaimedIdentityMatching.WhenUnique && weak.Count == 1)
                return await MatchAsync(actor, new ContactId(weak.Min!), ContactMatchStrength.Claimed, cancellationToken).ConfigureAwait(false);

            var now = Now;
            var contact = new Contact(options.IdFactory(), request.ProfileForCreate ?? ContactProfile.Empty, request.Evidence.Select(evidence => evidence.ToIdentity()),
                [], [], false, now, now);
            if (!(await AuthorizeAsync(actor, ContactPermissions.Create, ContactAccess.Resource(scope, contact), cancellationToken).ConfigureAwait(false)).IsPermitted)
                return ContactRefused.Forbidden;
            var batch = new ContactWriteBatch(puts: [new ContactPut(contact, null)], changes: [Change(ContactChangeKind.Created, contact.Id, now)]);
            var result = await store.WriteAsync(scope, batch, cancellationToken).ConfigureAwait(false);
            if (result.Value is ContactWriteApplied applied)
            {
                await NotifyAsync(applied.Changes, cancellationToken).ConfigureAwait(false);
                return new ContactCreated(await ViewAsync(actor, applied.Contacts[0], cancellationToken).ConfigureAwait(false));
            }
            // A concurrent writer took a verified identity: resolve again so the winner is matched.
            if (result.Value is ContactIdentityTaken && attempt < MaximumResolveAttempts)
                continue;
            return result.Value is ContactIdentityTaken taken ? await TakenAsync(actor, taken, cancellationToken).ConfigureAwait(false) : Refusal(result);
        }
    }

    /// <summary>Lists only candidates the actor may read, so ambiguity never discloses unreadable contact identifiers.</summary>
    private async Task<ContactResolution> AmbiguousAsync(PrincipalRef actor, IEnumerable<string> candidates, CancellationToken cancellationToken)
    {
        var ids = new List<ContactId>();
        foreach (var candidate in candidates)
            ids.Add(new ContactId(candidate));
        if (ids.Count == 0)
            return ContactRefused.Forbidden;
        var loadedTask = store.GetManyAsync(actor.Scope, ids, cancellationToken);
        var decisionsTask = evaluator.LoadDecisionContextAsync(actor, actor.Scope, [ContactPermissions.Read], cancellationToken);
        await Task.WhenAll(loadedTask, decisionsTask).ConfigureAwait(false);
        var loaded = loadedTask.Result;
        var decisions = decisionsTask.Result;
        var readable = new List<ContactId>(loaded.Count);
        foreach (var id in ids)
        {
            if (loaded.TryGetValue(id, out var stored)
                && (await decisions.DecideAsync(ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
                readable.Add(stored.Id);
        }
        return readable.Count == 0 ? ContactRefused.Forbidden : new ContactAmbiguous(readable);
    }

    private async Task<ContactResolution> MatchAsync(PrincipalRef actor, ContactId id, ContactMatchStrength strength, CancellationToken cancellationToken)
    {
        var stored = await store.GetAsync(actor.Scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ContactRefused.NotFound;
        var decision = await AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
        if (!decision.IsPermitted)
            return ContactRefused.Forbidden;
        var masking = await MaskingAsync(actor.Scope, ContactPermissions.Read, cancellationToken).ConfigureAwait(false);
        return new ContactMatched(masking.Project(stored, decision), strength);
    }

    public async Task<ContactSearchResult> SearchAsync(PrincipalRef actor, ContactSearch search, string? cursor = null, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(search);
        if (cursor is not null)
        {
            if (DecodeCursor(cursor, search) is not { } after)
                return ContactRefused.Invalid("The cursor is invalid for this search.");
            search = search.WithAfter(after);
        }
        if (search.Mode == ContactSearchMode.Identity)
        {
            // Searching by a masked identity kind would turn prefix matches into an oracle for the masked values.
            var workspace = await AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Workspace(scope), cancellationToken).ConfigureAwait(false);
            var field = ContactFieldPaths.ForIdentity(search.Kind!.Value);
            var searchable = workspace switch
            {
                AccessAllowed => true,
                AccessMasked masked => masked.Fields.All(item => item.Field != field),
                AccessDenied => false,
            };
            if (!searchable)
                return new ContactRefused(ContactRefusal.Forbidden, ["Identity search is not permitted for a masked or unauthorized identity kind."]);
        }

        var filter = await planner.PlanAsync(new AccessQuery(actor, ContactPermissions.Read, scope), cancellationToken).ConfigureAwait(false);
        var page = await store.SearchAsync(scope, search, filter, cancellationToken).ConfigureAwait(false);
        if (page.Contacts.Count == 0)
            return new ContactSearchPage([], page.Next is { } emptyNext ? EncodeCursor(search, emptyNext) : null);
        var masking = await MaskingAsync(scope, ContactPermissions.Read, cancellationToken).ConfigureAwait(false);
        var decisions = Decisions(actor, ContactPermissions.Read);
        var views = new List<ContactView>(page.Contacts.Count);
        foreach (var stored in page.Contacts)
        {
            if (stored.Scope != scope || stored.Contact.MergedInto is not null)
                throw new InvalidOperationException("The contact store returned a record outside the search contract.");
            var decision = await decisions.DecideAsync(stored, cancellationToken).ConfigureAwait(false);
            if (decision.IsPermitted)
                views.Add(masking.Project(stored, decision));
        }
        return new ContactSearchPage(views, page.Next is { } next ? EncodeCursor(search, next) : null);
    }

    public async Task<ContactExportResult> ExportAsync(PrincipalRef actor, ContactExportRequest request, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaximumContacts > options.MaximumExportContacts)
            return ContactRefused.Invalid($"Exports are limited to {options.MaximumExportContacts} contacts.");
        if (!(await AuthorizeAsync(actor, ContactPermissions.Export, ContactAccess.Workspace(scope), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        var filter = await planner.PlanAsync(new AccessQuery(actor, ContactPermissions.Export, scope), cancellationToken).ConfigureAwait(false);
        var auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), Now, scope, ContactAuditKind.Exported, AccessActor.From(actor),
            count: request.MaximumContacts, justification: request.Justification);
        await audit.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
        return new ContactExportStarted(ExportRowsAsync(actor, scope, filter, request, CancellationToken.None), auditEvent.Id);
    }

    /// <summary>
    /// Streams the export. Per-contact decisions for the sensitive <c>contact:export</c> permission are never cached, so Access audits
    /// each exported contact; when the stream completes or is disposed, an <see cref="ContactAuditKind.ExportCompleted"/> event records
    /// the number of contacts actually emitted.
    /// </summary>
    private async IAsyncEnumerable<ContactView> ExportRowsAsync(PrincipalRef actor, TicketScope scope, AccessFilter filter, ContactExportRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var emitted = 0;
        try
        {
            var masking = await MaskingAsync(scope, ContactPermissions.Export, cancellationToken).ConfigureAwait(false);
            var decisions = Decisions(actor, ContactPermissions.Export);
            var search = ContactSearch.ByName(limit: options.ExportPageSize);
            while (emitted < request.MaximumContacts)
            {
                var page = await store.SearchAsync(scope, search, filter, cancellationToken).ConfigureAwait(false);
                foreach (var stored in page.Contacts)
                {
                    if (stored.Scope != scope || stored.Contact.MergedInto is not null)
                        throw new InvalidOperationException("The contact store returned a record outside the search contract.");
                    var decision = await decisions.DecideAsync(stored, cancellationToken).ConfigureAwait(false);
                    if (!decision.IsPermitted)
                        continue;
                    var view = masking.Project(stored, decision);
                    emitted++;
                    yield return view;
                    if (emitted == request.MaximumContacts)
                        yield break;
                }
                if (page.Next is null)
                    yield break;
                search = search.WithAfter(page.Next);
            }
        }
        finally
        {
            await audit.RecordAsync(new ContactAuditEvent(Guid.CreateVersion7(), Now, scope, ContactAuditKind.ExportCompleted, AccessActor.From(actor),
                count: emitted, justification: request.Justification), CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<ContactResult<ContactMergeOutcome>> MergeAsync(PrincipalRef actor, ContactMergeRequest request, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target == request.Source)
            return ContactRefused.Invalid("A contact cannot be merged into itself.");
        var loaded = await store.GetManyAsync(scope, [request.Target, request.Source], cancellationToken).ConfigureAwait(false);
        if (!loaded.TryGetValue(request.Target, out var target) || !loaded.TryGetValue(request.Source, out var source))
            return ContactRefused.NotFound;
        if (target.Revision != request.ExpectedTargetRevision)
            return new ContactRefused(ContactRefusal.Conflict, ["The target revision changed."], target.Revision);
        if (source.Revision != request.ExpectedSourceRevision)
            return new ContactRefused(ContactRefusal.Conflict, ["The source revision changed."], source.Revision);
        var decisions = await evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Merge], cancellationToken).ConfigureAwait(false);
        if (!(await decisions.DecideAsync(ContactPermissions.Merge, ContactAccess.Resource(target), cancellationToken).ConfigureAwait(false)).IsPermitted
            || !(await decisions.DecideAsync(ContactPermissions.Merge, ContactAccess.Resource(source), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        if (target.Contact.MergedInto is { } targetMerged)
            return new ContactRefused(ContactRefusal.Merged, ["The target was merged."], target.Revision, targetMerged);
        if (source.Contact.MergedInto is { } sourceMerged)
            return new ContactRefused(ContactRefusal.Merged, ["The source was merged."], source.Revision, sourceMerged);

        var now = Now;
        var mergeId = Guid.CreateVersion7();
        var actorRecord = AccessActor.From(actor);
        ContactMerger.MergeComputation computation;
        try
        {
            computation = ContactMerger.Merge(mergeId, target.Contact, source.Contact, request.AttributeConflict, actorRecord, now);
        }
        catch (ArgumentException)
        {
            return new ContactRefused(ContactRefusal.Rejected, ["The merged contact would exceed contact limits."]);
        }
        if (!(await decisions.DecideAsync(ContactPermissions.Merge, ContactAccess.Resource(scope, computation.Target), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        var auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Merged, actorRecord, request.Source, request.Target, mergeId,
            justification: request.Justification);
        var batch = new ContactWriteBatch(
            puts: [new ContactPut(computation.Target, target.Revision), new ContactPut(computation.Tombstone, source.Revision)],
            changes: [Change(ContactChangeKind.Merged, request.Source, now, request.Target, mergeId), Change(ContactChangeKind.Updated, request.Target, now, merge: mergeId)],
            createMerge: computation.Record,
            audit: auditEvent);
        return await WriteAsync(actor, scope, batch, async applied =>
            new ContactMergeOutcome(await ViewAsync(actor, applied.Contacts[0], cancellationToken).ConfigureAwait(false), mergeId, auditEvent.Id), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContactResult<ContactUnmergeOutcome>> UnmergeAsync(PrincipalRef actor, Guid mergeId, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (mergeId == Guid.Empty)
            throw new ArgumentException("A merge identifier is required.", nameof(mergeId));
        var record = await store.GetMergeAsync(scope, mergeId, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return ContactRefused.NotFound;
        var loaded = await store.GetManyAsync(scope, [record.Target, record.Source], cancellationToken).ConfigureAwait(false);
        if (!loaded.TryGetValue(record.Target, out var target) || !loaded.TryGetValue(record.Source, out var source))
            return ContactRefused.NotFound;
        var decisions = await evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Merge, ContactPermissions.Read], cancellationToken).ConfigureAwait(false);
        if (!(await decisions.DecideAsync(ContactPermissions.Merge, ContactAccess.Resource(target), cancellationToken).ConfigureAwait(false)).IsPermitted
            || !(await decisions.DecideAsync(ContactPermissions.Merge, ContactAccess.Resource(scope, record.SourceSnapshot), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        if (target.Contact.MergedInto is { } targetMerged)
            return new ContactRefused(ContactRefusal.Merged, ["Unmerge the target from its own merge first."], target.Revision, targetMerged);
        if (source.Contact.MergedInto != record.Target)
            return new ContactRefused(ContactRefusal.Conflict, ["The source is no longer merged into the target."], source.Revision);

        var now = Now;
        var (restoredTarget, restoredSource) = ContactMerger.Unmerge(record, target.Contact, now);
        if (!(await decisions.DecideAsync(ContactPermissions.Merge, ContactAccess.Resource(scope, restoredTarget), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        var auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Unmerged, AccessActor.From(actor), record.Source, record.Target, mergeId);
        var batch = new ContactWriteBatch(
            puts: [new ContactPut(restoredTarget, target.Revision), new ContactPut(restoredSource, source.Revision)],
            changes: [Change(ContactChangeKind.Unmerged, record.Source, now, record.Target, mergeId), Change(ContactChangeKind.Updated, record.Target, now, merge: mergeId)],
            deleteMerge: mergeId,
            audit: auditEvent);
        return await WriteAsync(actor, scope, batch, async applied =>
            {
                var masking = await MaskingAsync(scope, ContactPermissions.Read, cancellationToken).ConfigureAwait(false);
                var targetDecision = await decisions.DecideAsync(ContactPermissions.Read, ContactAccess.Resource(applied.Contacts[0]), cancellationToken).ConfigureAwait(false);
                var sourceDecision = await decisions.DecideAsync(ContactPermissions.Read, ContactAccess.Resource(applied.Contacts[1]), cancellationToken).ConfigureAwait(false);
                return new ContactUnmergeOutcome(
                    masking.Project(applied.Contacts[0], targetDecision),
                    masking.Project(applied.Contacts[1], sourceDecision),
                    auditEvent.Id);
            }, cancellationToken)
            .ConfigureAwait(false);
    }

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? cursorBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? cursorWriter;

    private static string EncodeCursor(ContactSearch search, ContactSearchCursor next)
    {
        var buffer = cursorBuffer;
        if (buffer is null)
            cursorBuffer = buffer = new ArrayBufferWriter<byte>(256);
        else
            buffer.Clear();
        var writer = cursorWriter;
        if (writer is null)
            cursorWriter = writer = new Utf8JsonWriter(buffer);
        else
            writer.Reset(buffer);
        try
        {
            writer.WriteStartObject();
            writer.WriteNumber("v"u8, 1);
            writer.WriteNumber("m"u8, (int)search.Mode);
            writer.WriteNumber("k"u8, search.Kind is { } kind ? (int)kind : 0);
            writer.WriteString("ns"u8, search.Namespace);
            writer.WriteString("p"u8, search.Prefix);
            writer.WriteString("s"u8, next.SortKey);
            writer.WriteString("id"u8, next.Contact.Value);
            writer.WriteEndObject();
            writer.Flush();
            return Base64Url.EncodeToString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Clear();
            writer.Reset(buffer);
        }
    }

    private static ContactSearchCursor? DecodeCursor(string cursor, ContactSearch search)
    {
        if (cursor.Length is 0 or > MaximumCursorLength)
            return null;
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var expectedKind = search.Kind is { } kind ? (int)kind : 0;
            uint seen = 0;
            string? sortKey = null, id = null;
            foreach (var prop in root.EnumerateObject())
            {
                int bit;
                if (prop.NameEquals("v"u8))
                {
                    if (prop.Value.GetInt32() != 1)
                        return null;
                    bit = 0;
                }
                else if (prop.NameEquals("m"u8))
                {
                    if (prop.Value.GetInt32() != (int)search.Mode)
                        return null;
                    bit = 1;
                }
                else if (prop.NameEquals("k"u8))
                {
                    if (prop.Value.GetInt32() != expectedKind)
                        return null;
                    bit = 2;
                }
                else if (prop.NameEquals("ns"u8))
                {
                    if (prop.Value.GetString() != search.Namespace)
                        return null;
                    bit = 3;
                }
                else if (prop.NameEquals("p"u8))
                {
                    if (prop.Value.GetString() != search.Prefix)
                        return null;
                    bit = 4;
                }
                else if (prop.NameEquals("s"u8))
                {
                    sortKey = prop.Value.GetString();
                    if (sortKey is null)
                        return null;
                    bit = 5;
                }
                else if (prop.NameEquals("id"u8))
                {
                    id = prop.Value.GetString();
                    if (id is null)
                        return null;
                    bit = 6;
                }
                else
                    return null;

                var mask = 1u << bit;
                if ((seen & mask) != 0)
                    return null;
                seen |= mask;
            }
            return seen == 0b1111111u ? new ContactSearchCursor(sortKey!, new ContactId(id!)) : null;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}
