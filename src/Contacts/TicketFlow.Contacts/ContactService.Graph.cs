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
            return Refusal(result);
        }
    }

    /// <summary>Lists only candidates the actor may read, so ambiguity never discloses unreadable contact identifiers.</summary>
    private async Task<ContactResolution> AmbiguousAsync(PrincipalRef actor, IEnumerable<string> candidates, CancellationToken cancellationToken)
    {
        var readable = new List<ContactId>();
        foreach (var candidate in candidates)
        {
            var stored = await store.GetAsync(actor.Scope, new ContactId(candidate), cancellationToken).ConfigureAwait(false);
            if (stored is not null
                && (await AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
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
        var masking = await MaskingAsync(scope, ContactPermissions.Read, cancellationToken).ConfigureAwait(false);
        var decisions = new ContactDecisionCache(evaluator, actor, ContactPermissions.Read);
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
        return new ContactExportStarted(ExportRowsAsync(actor, scope, filter, request.MaximumContacts, CancellationToken.None), auditEvent.Id);
    }

    private async IAsyncEnumerable<ContactView> ExportRowsAsync(PrincipalRef actor, TicketScope scope, AccessFilter filter, int maximum,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var masking = await MaskingAsync(scope, ContactPermissions.Export, cancellationToken).ConfigureAwait(false);
        var decisions = new ContactDecisionCache(evaluator, actor, ContactPermissions.Export);
        var search = ContactSearch.ByName(limit: options.ExportPageSize);
        var emitted = 0;
        while (emitted < maximum)
        {
            var page = await store.SearchAsync(scope, search, filter, cancellationToken).ConfigureAwait(false);
            foreach (var stored in page.Contacts)
            {
                if (stored.Scope != scope || stored.Contact.MergedInto is not null)
                    throw new InvalidOperationException("The contact store returned a record outside the search contract.");
                var decision = await decisions.DecideAsync(stored, cancellationToken).ConfigureAwait(false);
                if (!decision.IsPermitted)
                    continue;
                yield return masking.Project(stored, decision);
                if (++emitted == maximum)
                    yield break;
            }
            if (page.Next is null)
                yield break;
            search = search.WithAfter(page.Next);
        }
    }

    public async Task<ContactResult<ContactMergeOutcome>> MergeAsync(PrincipalRef actor, ContactMergeRequest request, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Target == request.Source)
            return ContactRefused.Invalid("A contact cannot be merged into itself.");
        var target = await store.GetAsync(scope, request.Target, cancellationToken).ConfigureAwait(false);
        var source = await store.GetAsync(scope, request.Source, cancellationToken).ConfigureAwait(false);
        if (target is null || source is null)
            return ContactRefused.NotFound;
        if (target.Revision != request.ExpectedTargetRevision)
            return new ContactRefused(ContactRefusal.Conflict, ["The target revision changed."], target.Revision);
        if (source.Revision != request.ExpectedSourceRevision)
            return new ContactRefused(ContactRefusal.Conflict, ["The source revision changed."], source.Revision);
        if (!(await AuthorizeAsync(actor, ContactPermissions.Merge, ContactAccess.Resource(target), cancellationToken).ConfigureAwait(false)).IsPermitted
            || !(await AuthorizeAsync(actor, ContactPermissions.Merge, ContactAccess.Resource(source), cancellationToken).ConfigureAwait(false)).IsPermitted)
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
        if (!(await AuthorizeAsync(actor, ContactPermissions.Merge, ContactAccess.Resource(scope, computation.Target), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        var auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Merged, actorRecord, request.Source, request.Target, mergeId,
            justification: request.Justification);
        var batch = new ContactWriteBatch(
            puts: [new ContactPut(computation.Target, target.Revision), new ContactPut(computation.Tombstone, source.Revision)],
            changes: [Change(ContactChangeKind.Merged, request.Source, now, request.Target, mergeId), Change(ContactChangeKind.Updated, request.Target, now, merge: mergeId)],
            createMerge: computation.Record,
            audit: auditEvent);
        return await WriteAsync(scope, batch, async applied =>
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
        var target = await store.GetAsync(scope, record.Target, cancellationToken).ConfigureAwait(false);
        var source = await store.GetAsync(scope, record.Source, cancellationToken).ConfigureAwait(false);
        if (target is null || source is null)
            return ContactRefused.NotFound;
        if (!(await AuthorizeAsync(actor, ContactPermissions.Merge, ContactAccess.Resource(target), cancellationToken).ConfigureAwait(false)).IsPermitted
            || !(await AuthorizeAsync(actor, ContactPermissions.Merge, ContactAccess.Resource(scope, record.SourceSnapshot), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        if (target.Contact.MergedInto is { } targetMerged)
            return new ContactRefused(ContactRefusal.Merged, ["Unmerge the target from its own merge first."], target.Revision, targetMerged);
        if (source.Contact.MergedInto != record.Target)
            return new ContactRefused(ContactRefusal.Conflict, ["The source is no longer merged into the target."], source.Revision);

        var now = Now;
        var (restoredTarget, restoredSource) = ContactMerger.Unmerge(record, target.Contact, now);
        var auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Unmerged, AccessActor.From(actor), record.Source, record.Target, mergeId);
        var batch = new ContactWriteBatch(
            puts: [new ContactPut(restoredTarget, target.Revision), new ContactPut(restoredSource, source.Revision)],
            changes: [Change(ContactChangeKind.Unmerged, record.Source, now, record.Target, mergeId), Change(ContactChangeKind.Updated, record.Target, now, merge: mergeId)],
            deleteMerge: mergeId,
            audit: auditEvent);
        return await WriteAsync(scope, batch, async applied => new ContactUnmergeOutcome(
                await ViewAsync(actor, applied.Contacts[0], cancellationToken).ConfigureAwait(false),
                await ViewAsync(actor, applied.Contacts[1], cancellationToken).ConfigureAwait(false),
                auditEvent.Id), cancellationToken)
            .ConfigureAwait(false);
    }

    private static string EncodeCursor(ContactSearch search, ContactSearchCursor next)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteNumber("m", (int)search.Mode);
            writer.WriteNumber("k", search.Kind is { } kind ? (int)kind : 0);
            writer.WriteString("ns", search.Namespace);
            writer.WriteString("p", search.Prefix);
            writer.WriteString("s", next.SortKey);
            writer.WriteString("id", next.Contact.Value);
            writer.WriteEndObject();
        }
        return Base64Url.EncodeToString(buffer.ToArray());
    }

    private static ContactSearchCursor? DecodeCursor(string cursor, ContactSearch search)
    {
        if (cursor.Length is 0 or > MaximumCursorLength)
            return null;
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor), new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 7 || root.GetProperty("v").GetInt32() != 1
                || root.GetProperty("m").GetInt32() != (int)search.Mode || root.GetProperty("k").GetInt32() != (search.Kind is { } kind ? (int)kind : 0)
                || root.GetProperty("ns").GetString() != search.Namespace || root.GetProperty("p").GetString() != search.Prefix)
                return null;
            return new ContactSearchCursor(root.GetProperty("s").GetString()!, new ContactId(root.GetProperty("id").GetString()!));
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}
