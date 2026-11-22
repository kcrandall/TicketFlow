using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>Create (<see cref="ExpectedRevision"/> <see langword="null"/>) or compare-and-set replace one contact.</summary>
public sealed record ContactPut
{
    public ContactPut(Contact contact, long? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        Contact = contact;
        ExpectedRevision = expectedRevision;
    }

    public Contact Contact { get; }
    public long? ExpectedRevision { get; }
}

/// <summary>
/// Compare-and-set delete. Deleting a contact also removes its identities, every merge record naming it, and (recursively) the merge
/// tombstones merged into it together with their merge records. When <see cref="ExpectedCascade"/> is supplied, the store applies the
/// delete only if the tombstones it would cascade to are exactly that set (otherwise <see cref="ContactWriteConflict"/>), so callers can
/// authorize every removed contact first.
/// </summary>
public sealed record ContactDelete
{
    /// <summary>Maximum tombstones one delete may cascade to.</summary>
    public const int MaximumCascade = 256;

    public ContactDelete(ContactId id, long expectedRevision, IEnumerable<ContactId>? expectedCascade = null)
    {
        if (id == default)
            throw new ArgumentException("A contact identifier is required.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        Id = id;
        ExpectedRevision = expectedRevision;
        if (expectedCascade is not null)
        {
            var cascade = expectedCascade.ToHashSet();
            if (cascade.Count > MaximumCascade || cascade.Contains(id) || cascade.Contains(default))
                throw new ArgumentException($"An expected cascade lists at most {MaximumCascade} other initialized contacts.", nameof(expectedCascade));
            ExpectedCascade = cascade;
        }
    }

    public ContactId Id { get; }
    public long ExpectedRevision { get; }

    /// <summary>The tombstones (excluding <see cref="Id"/>) the delete must cascade to, or <see langword="null"/> to accept any.</summary>
    public IReadOnlySet<ContactId>? ExpectedCascade { get; }
}

public enum ContactChangeKind
{
    Created = 1,
    Updated = 2,
    Deleted = 3,

    /// <summary><see cref="ContactChangeDraft.Contact"/> was merged into <see cref="ContactChangeDraft.Related"/>.</summary>
    Merged = 4,

    /// <summary><see cref="ContactChangeDraft.Contact"/> was split back out of <see cref="ContactChangeDraft.Related"/>.</summary>
    Unmerged = 5,

    /// <summary>A verified external account (<see cref="ContactIdentityKind.ExternalUser"/>) was linked to <see cref="ContactChangeDraft.Contact"/>; appended next to its <see cref="Updated"/>/<see cref="Created"/> change.</summary>
    IdentityLinked = 6,

    /// <summary>A verified external account was unlinked from <see cref="ContactChangeDraft.Contact"/>; appended next to its <see cref="Updated"/> change.</summary>
    IdentityUnlinked = 7,
}

/// <summary>A change event to append in the write transaction. Carries identifiers only.</summary>
public sealed record ContactChangeDraft
{
    public ContactChangeDraft(Guid id, ContactChangeKind kind, ContactId contact, DateTimeOffset occurredAt, ContactId? related = null, Guid? merge = null)
    {
        if (id == Guid.Empty || merge == Guid.Empty)
            throw new ArgumentException("Event and merge identifiers must be non-empty.");
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (contact == default || related == default(ContactId))
            throw new ArgumentException("Contact identifiers must be initialized.");
        Id = id;
        Kind = kind;
        Contact = contact;
        OccurredAt = ContactText.Instant(occurredAt);
        Related = related;
        Merge = merge;
    }

    public Guid Id { get; }
    public ContactChangeKind Kind { get; }
    public ContactId Contact { get; }
    public DateTimeOffset OccurredAt { get; }
    public ContactId? Related { get; }
    public Guid? Merge { get; }
}

/// <summary>A committed change with its gapless, per-scope <see cref="Sequence"/> (first is 1).</summary>
public sealed record ContactChange(TicketScope Scope, long Sequence, ContactChangeDraft Event);

public enum ContactAuditKind
{
    Merged = 1,
    Unmerged = 2,
    /// <summary>An export was authorized and started; <see cref="ContactAuditEvent.Count"/> is the requested maximum.</summary>
    Exported = 3,

    Deleted = 4,

    /// <summary>An export stream finished or was abandoned; <see cref="ContactAuditEvent.Count"/> is the number of contacts emitted.</summary>
    ExportCompleted = 5,

    /// <summary>A verified external account was attached to <see cref="ContactAuditEvent.Contact"/> (or, with a merge, its contact was merged into <see cref="ContactAuditEvent.Related"/>).</summary>
    Linked = 6,

    /// <summary>A verified external account was removed from <see cref="ContactAuditEvent.Contact"/>.</summary>
    Unlinked = 7,

    /// <summary>A link suggestion (candidate <see cref="ContactAuditEvent.Contact"/>, account <see cref="ContactAuditEvent.Related"/>) was stored.</summary>
    LinkSuggested = 8,

    LinkSuggestionAccepted = 9,
    LinkSuggestionRejected = 10,

    /// <summary>The workspace linking policy was created or changed.</summary>
    LinkingPolicyChanged = 11,
}

/// <summary>
/// An audited contact operation. Events carry identifiers, counts and the operator justification; never names, addresses or
/// attribute values.
/// </summary>
public sealed record ContactAuditEvent
{
    public const int MaximumJustificationLength = 1024;

    public ContactAuditEvent(
        Guid id,
        DateTimeOffset occurredAt,
        TicketScope scope,
        ContactAuditKind kind,
        AccessActor actor,
        ContactId? contact = null,
        ContactId? related = null,
        Guid? merge = null,
        int? count = null,
        string? justification = null)
    {
        if (id == Guid.Empty || merge == Guid.Empty)
            throw new ArgumentException("Event and merge identifiers must be non-empty.");
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (contact == default(ContactId) || related == default(ContactId))
            throw new ArgumentException("Contact identifiers must be initialized when supplied.");
        if (count is < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Id = id;
        OccurredAt = ContactText.Instant(occurredAt);
        Scope = scope;
        Kind = kind;
        Actor = actor;
        Contact = contact;
        Related = related;
        Merge = merge;
        Count = count;
        Justification = ContactText.OptionalText(justification, MaximumJustificationLength, nameof(justification));
    }

    public Guid Id { get; }
    public DateTimeOffset OccurredAt { get; }
    public TicketScope Scope { get; }
    public ContactAuditKind Kind { get; }
    public AccessActor Actor { get; }
    public ContactId? Contact { get; }
    public ContactId? Related { get; }
    public Guid? Merge { get; }
    public int? Count { get; }
    public string? Justification { get; }

    /// <summary>The link suggestion the event concerns, when any.</summary>
    public Guid? Suggestion
    {
        get;
        init => field = value == Guid.Empty ? throw new ArgumentException("Suggestion identifiers must be non-empty.", nameof(value)) : value;
    }
}

/// <summary>Records audited contact operations. Must persist before returning and throw on failure.</summary>
public interface IContactAuditSink
{
    Task RecordAsync(ContactAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// One atomic, all-or-nothing store write: contact puts and deletes (each revision-fenced), an optional merge record create or
/// delete, change events appended to the outbox and an optional audit event recorded in the same transaction.
/// </summary>
public sealed record ContactWriteBatch
{
    public const int MaximumContacts = 8;
    public const int MaximumChanges = ContactDelete.MaximumCascade + 16;

    public ContactWriteBatch(
        IEnumerable<ContactPut>? puts = null,
        IEnumerable<ContactDelete>? deletes = null,
        IEnumerable<ContactChangeDraft>? changes = null,
        ContactMergeRecord? createMerge = null,
        Guid? deleteMerge = null,
        ContactAuditEvent? audit = null)
    {
        Puts = [.. puts ?? []];
        Deletes = [.. deletes ?? []];
        Changes = [.. changes ?? []];
        if (Puts.Any(put => put is null) || Deletes.Any(delete => delete is null) || Changes.Any(change => change is null))
            throw new ArgumentException("Batch entries cannot be null.");
        if (Puts.Count + Deletes.Count is 0 or > MaximumContacts)
            throw new ArgumentException($"A batch writes 1-{MaximumContacts} contacts.");
        if (Changes.Count > MaximumChanges)
            throw new ArgumentException($"A batch appends at most {MaximumChanges} changes.");
        var ids = Puts.Select(put => put.Contact.Id).Concat(Deletes.Select(delete => delete.Id)).ToList();
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("A batch writes each contact at most once.");
        if (deleteMerge == Guid.Empty || (createMerge is not null && deleteMerge is not null))
            throw new ArgumentException("A batch creates or deletes at most one merge record.");
        CreateMerge = createMerge;
        DeleteMerge = deleteMerge;
        Audit = audit;
    }

    public IReadOnlyList<ContactPut> Puts { get; }
    public IReadOnlyList<ContactDelete> Deletes { get; }
    public IReadOnlyList<ContactChangeDraft> Changes { get; }
    public ContactMergeRecord? CreateMerge { get; }
    public Guid? DeleteMerge { get; }
    public ContactAuditEvent? Audit { get; }
}

/// <summary>The batch committed. <see cref="Contacts"/> follows the order of the puts; changes carry their assigned sequences.</summary>
public sealed record ContactWriteApplied(IReadOnlyList<StoredContact> Contacts, IReadOnlyList<ContactChange> Changes)
{
    /// <summary>Every contact the batch removed: each deleted root followed by the tombstones it cascaded to (ordered by identifier).</summary>
    public IReadOnlyList<ContactId> Deleted { get; init; } = [];
}

/// <summary>A fenced contact or the merge record to delete does not exist in the scope. Nothing was written.</summary>
public sealed record ContactWriteNotFound(ContactId? Contact = null, Guid? Merge = null);

/// <summary>A revision did not match, or a create found an existing contact. Nothing was written.</summary>
public sealed record ContactWriteConflict(ContactId Contact, long CurrentRevision);

/// <summary>A verified identity is already held (verified) by <see cref="Owner"/>. Nothing was written.</summary>
public sealed record ContactIdentityTaken(ContactIdentityAddress Address, ContactId Owner);

public enum ContactWriteRejection
{
    /// <summary>A store capacity limit would be exceeded.</summary>
    CapacityExceeded = 1,

    /// <summary>A merge record with the same identifier exists.</summary>
    DuplicateMerge = 2,

    /// <summary>An attribute definition update tried to change the attribute type.</summary>
    TypeChanged = 3,
}

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record ContactWriteRejected(ContactWriteRejection Reason);

/// <summary>Outcome of <see cref="IContactStore.WriteAsync"/>. Switch over every case without a discard arm.</summary>
public union ContactWriteResult(ContactWriteApplied, ContactWriteNotFound, ContactWriteConflict, ContactIdentityTaken, ContactWriteRejected);

/// <summary>A contact holding an identity.</summary>
public sealed record ContactIdentityOwner(ContactId Contact, IdentityVerification Verification);

/// <summary>Keyset position: the sort key (folded name or identity value) and contact identifier of the last row read.</summary>
public sealed record ContactSearchCursor
{
    public ContactSearchCursor(string sortKey, ContactId contact)
    {
        ArgumentNullException.ThrowIfNull(sortKey);
        if (sortKey.Length > ContactSearch.MaximumSortKeyLength || !ContactText.IsClean(sortKey))
            throw new ArgumentException("Invalid cursor sort key.", nameof(sortKey));
        if (contact == default)
            throw new ArgumentException("A contact identifier is required.", nameof(contact));
        SortKey = sortKey;
        Contact = contact;
    }

    public string SortKey { get; }
    public ContactId Contact { get; }

    public override string ToString() => "ContactSearchCursor { <redacted> }";
}

public enum ContactSearchMode
{
    /// <summary>By folded display name prefix (empty prefix lists every contact), ordered by (folded name, id).</summary>
    Name = 1,

    /// <summary>By normalized identity value prefix for one kind, ordered by (value, id).</summary>
    Identity = 2,
}

/// <summary>A bounded, keyset-paged contact search. Merge tombstones are never returned.</summary>
public sealed record ContactSearch
{
    public const int MaximumLimit = 100;
    public const int DefaultLimit = 50;
    public const int MaximumPrefixLength = 256;
    public const int MaximumSortKeyLength = 512;

    /// <summary>
    /// Default bound on rows a store examines for one page before applying the access filter. When it is reached with fewer visible
    /// rows than <see cref="Limit"/>, the store returns a short (possibly empty) page whose cursor continues after the last examined row.
    /// </summary>
    public const int DefaultMaximumRowsExamined = 1000;

    private ContactSearch(ContactSearchMode mode, string prefix, ContactIdentityKind? kind, string? @namespace, int limit, ContactSearchCursor? after)
    {
        if (limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limits are 1-{MaximumLimit}.");
        Mode = mode;
        Prefix = prefix;
        Kind = kind;
        Namespace = @namespace;
        Limit = limit;
        After = after;
    }

    public ContactSearchMode Mode { get; }

    /// <summary>The folded (name) or normalized (identity) prefix; possibly empty for name searches.</summary>
    public string Prefix { get; }

    public ContactIdentityKind? Kind { get; }

    /// <summary>Optional namespace restriction for identity searches.</summary>
    public string? Namespace { get; }

    public int Limit { get; }
    public ContactSearchCursor? After { get; }

    /// <summary>The same search continuing after <paramref name="after"/>.</summary>
    public ContactSearch WithAfter(ContactSearchCursor? after) => new(Mode, Prefix, Kind, Namespace, Limit, after);

    public static ContactSearch ByName(string? prefix = null, int limit = DefaultLimit, ContactSearchCursor? after = null)
    {
        var folded = FoldName(prefix ?? string.Empty);
        if (folded.Length > MaximumPrefixLength)
            throw new ArgumentException($"Prefixes can contain at most {MaximumPrefixLength} characters.", nameof(prefix));
        return new(ContactSearchMode.Name, folded, null, null, limit, after);
    }

    public static ContactSearch ByIdentity(ContactIdentityKind kind, string prefix, string? @namespace = null, int limit = DefaultLimit, ContactSearchCursor? after = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (@namespace is not null)
            ContactText.RequireToken(@namespace, ContactIdentityAddress.MaximumNamespaceLength, nameof(@namespace), "Namespaces");
        var folded = FoldIdentityPrefix(kind, prefix);
        if (folded.Length is 0 or > MaximumPrefixLength || !ContactText.IsClean(folded))
            throw new ArgumentException($"Identity prefixes contain 1-{MaximumPrefixLength} characters.", nameof(prefix));
        return new(ContactSearchMode.Identity, folded, kind, @namespace, limit, after);
    }

    /// <summary>The folded form of a display name used for name ordering and prefix search: trimmed, invariant lowercase.</summary>
    public static string FoldName(string? displayName) => (displayName ?? string.Empty).Trim().ToLowerInvariant();

    private static string FoldIdentityPrefix(ContactIdentityKind kind, string prefix)
    {
        var trimmed = prefix.Trim();
        switch (kind)
        {
            case ContactIdentityKind.Email:
                return trimmed.ToLowerInvariant();
            case ContactIdentityKind.SocialHandle:
                return trimmed.TrimStart('@').ToLowerInvariant();
            case ContactIdentityKind.Phone:
            case ContactIdentityKind.WhatsApp:
                var digits = new string([.. trimmed.Where(char.IsAsciiDigit)]);
                return digits.Length == 0 ? string.Empty : "+" + (trimmed.StartsWith("00", StringComparison.Ordinal) && !trimmed.StartsWith('+') ? digits[2..] : digits);
            default:
                return prefix;
        }
    }
}

/// <summary>
/// One page of contacts. <see cref="Next"/> is <see langword="null"/> at the end; pages may be short or empty (bounded scan) while
/// <see cref="Next"/> is set.
/// </summary>
public sealed record ContactPage(IReadOnlyList<StoredContact> Contacts, ContactSearchCursor? Next);

/// <summary>
/// Durable contact storage. Contract:
/// <list type="bullet">
/// <item>Every operation is confined to exactly one <see cref="TicketScope"/>; records never cross scopes.</item>
/// <item><see cref="WriteAsync"/> applies a batch atomically: every put/delete is fenced on its expected revision (create requires
/// absence), creates start at revision 1 and updates increment by one.</item>
/// <item>A verified identity is unique per (scope, kind, namespace, value) across live contacts, checked atomically with the write
/// (<see cref="ContactIdentityTaken"/>); claimed identities may repeat.</item>
/// <item>Change events are appended in the same transaction with a gapless sequence per scope starting at 1.</item>
/// <item>Search applies the supplied <see cref="AccessFilter"/> (over a resource with the contact's scope, identifier and labels,
/// no team, inbox, assignee or attributes) inside the bounded keyset query, never by post-filtering an unbounded result; at most a
/// store-defined number of rows (default <see cref="ContactSearch.DefaultMaximumRowsExamined"/>) is examined per page.</item>
/// </list>
/// </summary>
public interface IContactStore
{
    public const int MaximumIdentityOwners = 32;
    public const int MaximumChangesPerRead = 500;

    /// <summary>The contact, including merge tombstones.</summary>
    Task<StoredContact?> GetAsync(TicketScope scope, ContactId id, CancellationToken cancellationToken = default);

    /// <summary>Up to <see cref="MaximumIdentityOwners"/> holders of <paramref name="address"/>: the verified holder first, then claimed holders by identifier.</summary>
    Task<IReadOnlyList<ContactIdentityOwner>> FindByIdentityAsync(TicketScope scope, ContactIdentityAddress address, CancellationToken cancellationToken = default);

    Task<ContactPage> SearchAsync(TicketScope scope, ContactSearch search, AccessFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Up to <paramref name="limit"/> (1-<see cref="ContactDelete.MaximumCascade"/>) tombstones merged directly into <paramref name="target"/>, ordered by identifier.</summary>
    Task<IReadOnlyList<ContactId>> ListMergedIntoAsync(TicketScope scope, ContactId target, int limit, CancellationToken cancellationToken = default);

    Task<ContactWriteResult> WriteAsync(TicketScope scope, ContactWriteBatch batch, CancellationToken cancellationToken = default);

    Task<ContactMergeRecord?> GetMergeAsync(TicketScope scope, Guid mergeId, CancellationToken cancellationToken = default);

    /// <summary>Changes with a sequence greater than <paramref name="afterSequence"/>, ascending, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<ContactChange>> ReadChangesAsync(TicketScope scope, long afterSequence, int limit, CancellationToken cancellationToken = default);
}

/// <summary>The definition write was applied; for deletes, the removed state.</summary>
public sealed record AttributeDefinitionApplied(StoredContactAttributeDefinition Value);

public sealed record AttributeDefinitionNotFound
{
    private AttributeDefinitionNotFound()
    {
    }

    public static AttributeDefinitionNotFound Instance { get; } = new();
}

public sealed record AttributeDefinitionConflict(long CurrentRevision);

/// <summary>Outcome of a definition write. Switch over every case without a discard arm.</summary>
public union AttributeDefinitionWriteResult(AttributeDefinitionApplied, AttributeDefinitionNotFound, AttributeDefinitionConflict, ContactWriteRejected);

/// <summary>
/// Workspace-scoped attribute definitions keyed by attribute key: creates start at revision 1, updates and deletes are
/// compare-and-set, the type cannot change (<see cref="ContactWriteRejection.TypeChanged"/>) and at most
/// <see cref="MaximumDefinitionsPerWorkspace"/> definitions exist per scope (checked atomically).
/// </summary>
public interface IContactAttributeDefinitionStore
{
    public const int MaximumDefinitionsPerWorkspace = 256;

    Task<StoredContactAttributeDefinition?> GetAsync(TicketScope scope, string key, CancellationToken cancellationToken = default);

    /// <summary>Definitions ordered by key (ordinal).</summary>
    Task<IReadOnlyList<StoredContactAttributeDefinition>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<AttributeDefinitionWriteResult> CreateAsync(TicketScope scope, ContactAttributeDefinition definition, CancellationToken cancellationToken = default);

    Task<AttributeDefinitionWriteResult> UpdateAsync(TicketScope scope, ContactAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AttributeDefinitionWriteResult> DeleteAsync(TicketScope scope, string key, long expectedRevision, CancellationToken cancellationToken = default);
}
