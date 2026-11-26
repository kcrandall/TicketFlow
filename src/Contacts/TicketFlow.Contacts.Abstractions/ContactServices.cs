using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>An identity as shown to a principal. When <see cref="Masked"/>, <see cref="Value"/> is <see langword="null"/>.</summary>
public sealed record ContactIdentityView(ContactIdentityKind Kind, string Namespace, string? Value, IdentityVerification Verification, bool Masked, bool Revealable)
{
    public override string ToString() => $"ContactIdentityView {{ Kind = {Kind}, Masked = {Masked} }}";
}

/// <summary>An attribute as shown to a principal. When <see cref="Masked"/>, <see cref="Value"/> is <see langword="null"/>.</summary>
public sealed record ContactAttributeView(string Key, ContactAttributeType Type, bool Sensitive, ContactAttributeValue? Value, bool Masked, bool Revealable)
{
    public override string ToString() => $"ContactAttributeView {{ Key = {Key}, Masked = {Masked} }}";
}

/// <summary>
/// A server-side masked projection of a contact. Masked identity and attribute values are removed, not obfuscated;
/// <see cref="MaskedFields"/> lists every masked path (ordered) and whether it can be revealed. Attributes without a definition are
/// omitted.
/// </summary>
public sealed record ContactView(
    ContactId Id,
    long Revision,
    ContactProfile Profile,
    IReadOnlyList<ContactIdentityView> Identities,
    IReadOnlyList<ContactAttributeView> Attributes,
    IReadOnlyList<LabelId> Labels,
    bool Blocked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MaskedField> MaskedFields)
{
    public override string ToString() => $"ContactView {{ Id = {Id}, Revision = {Revision} }}";
}

/// <summary>A single edit applied by <see cref="IContactService.UpdateAsync"/>. Closed: switch over every case without a discard arm.</summary>
public closed record ContactEdit
{
    private protected ContactEdit()
    {
    }
}

/// <summary>Replaces the whole profile.</summary>
public sealed record SetContactProfile(ContactProfile Profile) : ContactEdit;

/// <summary>Sets a defined attribute; the value type must match the definition.</summary>
public sealed record SetContactAttribute(string Key, ContactAttributeValue Value) : ContactEdit;

public sealed record RemoveContactAttribute(string Key) : ContactEdit;

public sealed record AddContactLabel(LabelId Label) : ContactEdit;

public sealed record RemoveContactLabel(LabelId Label) : ContactEdit;

public sealed record SetContactBlocked(bool Blocked) : ContactEdit;

/// <summary>Adds an identity or replaces the verification of the same address already on the contact.</summary>
public sealed record AddContactIdentity(ContactIdentity Identity) : ContactEdit;

public sealed record RemoveContactIdentity(ContactIdentityAddress Address) : ContactEdit;

/// <summary>Input for creating a contact. A <see langword="null"/> identifier is generated.</summary>
public sealed record ContactDraft(
    ContactProfile Profile,
    IReadOnlyList<ContactIdentity>? Identities = null,
    IReadOnlyDictionary<string, ContactAttributeValue>? Attributes = null,
    IReadOnlyList<LabelId>? Labels = null,
    bool Blocked = false,
    ContactId? Id = null);

public enum ContactRefusal
{
    /// <summary>Access denied the operation.</summary>
    Forbidden = 1,

    /// <summary>The input is invalid; see <see cref="ContactRefused.Errors"/>.</summary>
    Invalid = 2,

    NotFound = 3,

    /// <summary>The expected revision did not match; see <see cref="ContactRefused.CurrentRevision"/>.</summary>
    Conflict = 4,

    /// <summary>A verified identity is already held by another contact.</summary>
    IdentityTaken = 5,

    /// <summary>A store limit or contract rule refused the write.</summary>
    Rejected = 6,

    /// <summary>The contact was merged; see <see cref="ContactRefused.MergedInto"/>.</summary>
    Merged = 7,
}

/// <summary>The operation was refused. <see cref="Errors"/> are non-sensitive messages (no names, addresses or values).</summary>
public sealed record ContactRefused(ContactRefusal Reason, IReadOnlyList<string> Errors, long? CurrentRevision = null, ContactId? MergedInto = null)
{
    public static ContactRefused Forbidden { get; } = new(ContactRefusal.Forbidden, []);
    public static ContactRefused NotFound { get; } = new(ContactRefusal.NotFound, []);

    public static ContactRefused Invalid(string error) => new(ContactRefusal.Invalid, [error]);
}

public enum ContactMatchStrength
{
    /// <summary>A verified identity of the contact matched verified or claimed evidence.</summary>
    Verified = 1,

    /// <summary>Only claimed identities matched, and the claimed-match policy allowed it.</summary>
    Claimed = 2,
}

public sealed record ContactMatched(ContactView Contact, ContactMatchStrength Strength);

public sealed record ContactCreated(ContactView Contact);

/// <summary>Evidence pointed at several contacts; nothing was created. Candidates are ordered by identifier.</summary>
public sealed record ContactAmbiguous(IReadOnlyList<ContactId> Candidates);

/// <summary>Outcome of inbound resolve-or-create. Switch over every case without a discard arm.</summary>
public union ContactResolution(ContactMatched, ContactCreated, ContactAmbiguous, ContactRefused);

/// <summary>How claimed identities participate in resolution when no verified identity matches.</summary>
public enum ClaimedIdentityMatching
{
    /// <summary>Claimed identities never match; a new contact is created.</summary>
    Never = 1,

    /// <summary>A single claimed holder matches; several are ambiguous.</summary>
    WhenUnique = 2,
}

/// <summary>Inbound channel evidence (1-16 identities) plus an optional profile used only when a contact is created.</summary>
public sealed record ContactResolveRequest
{
    public const int MaximumEvidence = 16;

    public ContactResolveRequest(IEnumerable<ContactIdentityEvidence> evidence, ContactProfile? profileForCreate = null, ClaimedIdentityMatching? claimedMatching = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        Evidence = [.. evidence];
        if (Evidence.Count is 0 or > MaximumEvidence || Evidence.Any(item => item is null))
            throw new ArgumentException($"Resolution requires 1-{MaximumEvidence} evidence items.", nameof(evidence));
        if (Evidence.Select(item => item.Address).Distinct().Count() != Evidence.Count)
            throw new ArgumentException("Evidence addresses must be distinct.", nameof(evidence));
        if (claimedMatching is { } matching && !Enum.IsDefined(matching))
            throw new ArgumentOutOfRangeException(nameof(claimedMatching));
        ProfileForCreate = profileForCreate;
        ClaimedMatching = claimedMatching;
    }

    public IReadOnlyList<ContactIdentityEvidence> Evidence { get; }
    public ContactProfile? ProfileForCreate { get; }

    /// <summary>Overrides the configured policy for this request.</summary>
    public ClaimedIdentityMatching? ClaimedMatching { get; }
}

/// <summary>One page of masked contacts; <see cref="NextCursor"/> is an opaque token, <see langword="null"/> at the end.</summary>
public sealed record ContactSearchPage(IReadOnlyList<ContactView> Contacts, string? NextCursor);

public union ContactSearchResult(ContactSearchPage, ContactRefused);

/// <summary>The reveal was granted and audited. Identity values for identity paths, or the attribute value for attribute paths.</summary>
public sealed record ContactFieldRevealed(FieldPath Field, IReadOnlyList<ContactIdentityAddress> Identities, ContactAttributeValue? Attribute, Guid AuditEventId)
{
    public override string ToString() => $"ContactFieldRevealed {{ Field = {Field}, <redacted> }}";
}

/// <summary>Access refused the reveal; the refusal is audited.</summary>
public sealed record ContactRevealRefused(FieldPath Field, RevealRefusal Reason, Guid AuditEventId);

public union ContactRevealResult(ContactFieldRevealed, ContactRevealRefused, ContactRefused);

/// <summary>An export of at most <see cref="MaximumContacts"/> contacts, justified for the audit trail.</summary>
public sealed record ContactExportRequest
{
    public ContactExportRequest(int maximumContacts, string justification)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumContacts, 1);
        MaximumContacts = maximumContacts;
        Justification = ContactText.RequireText(justification, ContactAuditEvent.MaximumJustificationLength, nameof(justification));
    }

    public int MaximumContacts { get; }
    public string Justification { get; }
}

/// <summary>
/// The export was authorized and audited. <see cref="Contacts"/> streams masked contacts (masking per <c>contact:export</c>) in
/// bounded keyset pages and stops after the requested maximum.
/// </summary>
public sealed record ContactExportStarted(IAsyncEnumerable<ContactView> Contacts, Guid AuditEventId);

public union ContactExportResult(ContactExportStarted, ContactRefused);

/// <summary>Merge <see cref="Source"/> into <see cref="Target"/>, fenced on both revisions.</summary>
public sealed record ContactMergeRequest
{
    public ContactMergeRequest(ContactId target, long expectedTargetRevision, ContactId source, long expectedSourceRevision,
        ContactAttributeConflict attributeConflict = ContactAttributeConflict.PreferTarget, string? justification = null)
    {
        if (target == default || source == default)
            throw new ArgumentException("Contact identifiers are required.", target == default ? nameof(target) : nameof(source));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedTargetRevision, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedSourceRevision, 1);
        if (!Enum.IsDefined(attributeConflict))
            throw new ArgumentOutOfRangeException(nameof(attributeConflict));
        Target = target;
        ExpectedTargetRevision = expectedTargetRevision;
        Source = source;
        ExpectedSourceRevision = expectedSourceRevision;
        AttributeConflict = attributeConflict;
        Justification = ContactText.OptionalText(justification, ContactAuditEvent.MaximumJustificationLength, nameof(justification));
    }

    public ContactId Target { get; }
    public long ExpectedTargetRevision { get; }
    public ContactId Source { get; }
    public long ExpectedSourceRevision { get; }
    public ContactAttributeConflict AttributeConflict { get; }
    public string? Justification { get; }
}

public sealed record ContactMergeOutcome(ContactView Target, Guid MergeId, Guid AuditEventId);

public sealed record ContactUnmergeOutcome(ContactView Target, ContactView Source, Guid AuditEventId);

/// <summary>
/// Access-checked contact operations. Every read and mutation authorizes through <see cref="IAccessEvaluator"/>; search and export
/// use <see cref="IAccessQueryPlanner"/> filters inside the store query; every returned contact is masked server-side.
/// </summary>
public interface IContactService
{
    /// <summary>Requires <c>contact:create</c>.</summary>
    Task<TicketResult<ContactView, ContactRefused>> CreateAsync(PrincipalRef actor, ContactDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:read</c>. A merge tombstone yields <see cref="ContactRefusal.Merged"/> with the surviving contact.</summary>
    Task<TicketResult<ContactView, ContactRefused>> GetAsync(PrincipalRef actor, ContactId id, CancellationToken cancellationToken = default);

    /// <summary>Maximum identifiers accepted by <see cref="GetManyAsync"/>.</summary>
    public const int MaximumBatch = 100;

    /// <summary>
    /// Reads up to <see cref="MaximumBatch"/> contacts for list rendering: the readable, unmerged contacts among <paramref name="ids"/>
    /// (masked per caller), omitting missing, merged and forbidden ones. Implementations decide every contact with one decision context
    /// and load masking policies once; the default implementation calls <see cref="GetAsync"/> per identifier.
    /// </summary>
    async Task<IReadOnlyList<ContactView>> GetManyAsync(PrincipalRef actor, IEnumerable<ContactId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct().ToList();
        if (distinct.Count > MaximumBatch)
            throw new ArgumentException($"At most {MaximumBatch} contacts per batch.", nameof(ids));
        var views = new List<ContactView>();
        foreach (var id in distinct)
            if (await GetAsync(actor, id, cancellationToken).ConfigureAwait(false) is TicketValue<ContactView> applied)
                views.Add(applied.Value);
        return views;
    }

    /// <summary>Requires <c>contact:update</c>; edits apply atomically in order, fenced on <paramref name="expectedRevision"/>.</summary>
    Task<TicketResult<ContactView, ContactRefused>> UpdateAsync(PrincipalRef actor, ContactId id, long expectedRevision, IEnumerable<ContactEdit> edits, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:delete</c>; audited. Also removes merge history naming the contact.</summary>
    Task<TicketResult<ContactId, ContactRefused>> DeleteAsync(PrincipalRef actor, ContactId id, long expectedRevision, string? justification = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inbound resolve-or-create. Requires <c>contact:read</c> to match and <c>contact:create</c> to create. Verified holders match
    /// first; claimed holders follow <see cref="ClaimedIdentityMatching"/>. Addresses are correlation evidence, never authentication.
    /// </summary>
    Task<ContactResolution> ResolveAsync(PrincipalRef actor, ContactResolveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requires <c>contact:read</c>; rows are filtered by the planned Access filter. Identity searches on a kind whose field is masked
    /// for the principal are refused. <paramref name="cursor"/> is a token from a previous page of the same search.
    /// </summary>
    Task<ContactSearchResult> SearchAsync(PrincipalRef actor, ContactSearch search, string? cursor = null, CancellationToken cancellationToken = default);

    /// <summary>Reveals one masked field through <see cref="IAccessEvaluator.RevealAsync"/> (always audited by Access).</summary>
    Task<ContactRevealResult> RevealAsync(PrincipalRef actor, ContactId id, FieldPath field, string? justification = null, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:export</c> (sensitive, audited) and a justification; bounded by <see cref="ContactExportRequest.MaximumContacts"/>.</summary>
    Task<ContactExportResult> ExportAsync(PrincipalRef actor, ContactExportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:merge</c> on both contacts; audited; emits change events.</summary>
    Task<TicketResult<ContactMergeOutcome, ContactRefused>> MergeAsync(PrincipalRef actor, ContactMergeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:merge</c> on the target; audited; emits change events.</summary>
    Task<TicketResult<ContactUnmergeOutcome, ContactRefused>> UnmergeAsync(PrincipalRef actor, Guid mergeId, CancellationToken cancellationToken = default);
}

/// <summary>Access-checked attribute definition management. Reads require <c>contact:read</c>; writes require <c>contact-attribute:manage</c>.</summary>
public interface IContactAttributeDefinitionService
{
    Task<TicketResult<IReadOnlyList<StoredContactAttributeDefinition>, ContactRefused>> ListAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    Task<TicketResult<StoredContactAttributeDefinition, ContactRefused>> CreateAsync(PrincipalRef actor, ContactAttributeDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Changes the display name or sensitivity; the type is immutable.</summary>
    Task<TicketResult<StoredContactAttributeDefinition, ContactRefused>> UpdateAsync(PrincipalRef actor, ContactAttributeDefinition definition, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Values stored under a deleted definition stay stored but are omitted from views.</summary>
    Task<TicketResult<StoredContactAttributeDefinition, ContactRefused>> DeleteAsync(PrincipalRef actor, string key, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-process notification of committed contact changes (for example so conversations can follow merges). Observers run after the
/// commit in registration order; if one throws, the change stays applied and the exception propagates. Durable consumers should read
/// <see cref="IContactStore.ReadChangesAsync"/> (the outbox) instead.
/// </summary>
public interface IContactChangeObserver
{
    Task OnChangedAsync(IReadOnlyList<ContactChange> changes, CancellationToken cancellationToken = default);
}
