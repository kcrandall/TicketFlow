using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// An application account: an issuer/namespace token (for example <c>identityflow</c> or <c>acme-portal</c>) and the opaque, exact
/// external identifier the application authenticated. Becomes a verified <see cref="ContactIdentityKind.ExternalUser"/> identity.
/// Personal data: <see cref="ToString"/> redacts the identifier.
/// </summary>
public sealed record ExternalAccountRef
{
    public ExternalAccountRef(string @namespace, string externalId)
    {
        ArgumentNullException.ThrowIfNull(@namespace);
        ArgumentNullException.ThrowIfNull(externalId);
        Address = ContactIdentityAddress.Normalize(ContactIdentityKind.ExternalUser, externalId, @namespace) switch
        {
            ContactIdentityAddress address => address,
            ContactAddressInvalid invalid => throw new ArgumentException($"The external account is invalid: {invalid.Error}."),
        };
    }

    public string Namespace => Address.Namespace;
    public string ExternalId => Address.Value;

    /// <summary>The normalized <see cref="ContactIdentityKind.ExternalUser"/> address.</summary>
    public ContactIdentityAddress Address { get; }

    public override string ToString() => $"ExternalAccountRef {{ Namespace = {Namespace}, <redacted> }}";
}

/// <summary>
/// Links <see cref="Account"/> to a contact. The contact is named by <see cref="Contact"/> or found through <see cref="Evidence"/>
/// (exactly one; for example the anonymous widget visitor identity of the session that just signed in). The account is attached as a
/// verified identity whose source is <see cref="VerificationSource"/>. The caller asserts that it authenticated the account.
/// </summary>
public sealed record ContactLinkRequest
{
    public ContactLinkRequest(ExternalAccountRef account, string verificationSource, ContactId? contact = null, ContactIdentityAddress? evidence = null,
        string? justification = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        VerificationSource = ContactText.RequireToken(verificationSource, IdentityVerification.MaximumSourceLength, nameof(verificationSource), "Verification sources");
        if ((contact is null) == (evidence is null))
            throw new ArgumentException("Name exactly one of a contact or evidence.");
        if (contact == default(ContactId))
            throw new ArgumentException("Contact identifiers must be initialized.", nameof(contact));
        if (evidence is not null && evidence == account.Address)
            throw new ArgumentException("The evidence cannot be the account itself.", nameof(evidence));
        Contact = contact;
        Evidence = evidence;
        Justification = ContactText.OptionalText(justification, ContactAuditEvent.MaximumJustificationLength, nameof(justification));
    }

    public ExternalAccountRef Account { get; }
    public string VerificationSource { get; }
    public ContactId? Contact { get; }
    public ContactIdentityAddress? Evidence { get; }
    public string? Justification { get; }

    public override string ToString() => "ContactLinkRequest { <redacted> }";
}

/// <summary>The account identity was attached to <see cref="Contact"/> (which had no account holder competing for it).</summary>
public sealed record ContactLinked(ContactView Contact, Guid AuditEventId);

/// <summary>
/// Another contact already held the verified account, so <see cref="Source"/> (the named or evidenced contact) was merged into
/// <see cref="Target"/> through the audited merge; conversations follow through the contact change log. Undo with
/// <see cref="IContactService.UnmergeAsync"/> and <see cref="MergeId"/>.
/// </summary>
public sealed record ContactMergedInto(ContactView Target, ContactId Source, Guid MergeId);

/// <summary>The contact already holds the verified account (also after an earlier merge); nothing was written.</summary>
public sealed record ContactAlreadyLinked(ContactView Contact);

public enum ContactLinkConflictReason
{
    /// <summary>The contact holds a different verified account in the same namespace; two accounts are never combined.</summary>
    OtherAccount = 1,

    /// <summary>The evidence is held by several contacts; <see cref="ContactLinkConflict.Candidates"/> lists the readable ones.</summary>
    AmbiguousEvidence = 2,

    /// <summary>Concurrent writers kept changing the contacts; retry.</summary>
    Contended = 3,
}

/// <summary>The link was not applied. Candidates are identifiers the actor may read, ordered by identifier.</summary>
public sealed record ContactLinkConflict(ContactLinkConflictReason Reason, IReadOnlyList<ContactId> Candidates);

/// <summary>Outcome of <see cref="IContactLinkingService.LinkAccountAsync"/>. Switch over every case without a discard arm.</summary>
public union ContactLinkResult(ContactLinked, ContactMergedInto, ContactAlreadyLinked, ContactLinkConflict, ContactRefused);

/// <summary>An address the login provider asserted for the account, with whether the provider verified it (for example <c>email_verified</c>).</summary>
public sealed record CustomerClaim
{
    public CustomerClaim(ContactIdentityAddress address, bool verified)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        if (address.Kind is not (ContactIdentityKind.Email or ContactIdentityKind.Phone or ContactIdentityKind.WhatsApp or ContactIdentityKind.SocialHandle))
            throw new ArgumentException("Claims are email, phone, WhatsApp or social handle addresses.", nameof(address));
        Verified = verified;
    }

    public ContactIdentityAddress Address { get; }
    public bool Verified { get; }

    public override string ToString() => $"CustomerClaim {{ Kind = {Address.Kind}, Verified = {Verified} }}";
}

/// <summary>
/// Ensures the contact of a signed-in customer: the account contact is found (or created with <see cref="DisplayName"/>), optionally
/// <see cref="LinkContact"/> or <see cref="LinkEvidence"/> (the anonymous contact of the same session) is linked/merged into it, the
/// claims are attached (verified claims as verified when no other contact holds them verified) and the workspace
/// <see cref="ContactLinkingPolicy"/> is applied to other contacts holding the verified claims as claimed evidence.
/// </summary>
public sealed record CustomerResolveRequest
{
    public const int MaximumClaims = 8;

    public CustomerResolveRequest(ExternalAccountRef account, string verificationSource, IEnumerable<CustomerClaim>? claims = null, string? displayName = null,
        ContactId? linkContact = null, ContactIdentityAddress? linkEvidence = null)
    {
        Account = account ?? throw new ArgumentNullException(nameof(account));
        VerificationSource = ContactText.RequireToken(verificationSource, IdentityVerification.MaximumSourceLength, nameof(verificationSource), "Verification sources");
        Claims = [.. claims ?? []];
        if (Claims.Count > MaximumClaims || Claims.Any(claim => claim is null) || Claims.Select(claim => claim.Address).Distinct().Count() != Claims.Count)
            throw new ArgumentException($"At most {MaximumClaims} distinct claims are allowed.", nameof(claims));
        DisplayName = ContactText.OptionalText(displayName, ContactProfile.MaximumDisplayNameLength, nameof(displayName));
        if (linkContact is not null && linkEvidence is not null)
            throw new ArgumentException("Name at most one of a contact or evidence to link.");
        if (linkContact == default(ContactId))
            throw new ArgumentException("Contact identifiers must be initialized.", nameof(linkContact));
        if (linkEvidence is not null && linkEvidence == account.Address)
            throw new ArgumentException("The evidence cannot be the account itself.", nameof(linkEvidence));
        LinkContact = linkContact;
        LinkEvidence = linkEvidence;
    }

    public ExternalAccountRef Account { get; }
    public string VerificationSource { get; }
    public IReadOnlyList<CustomerClaim> Claims { get; }
    public string? DisplayName { get; }
    public ContactId? LinkContact { get; }
    public ContactIdentityAddress? LinkEvidence { get; }

    public override string ToString() => "CustomerResolveRequest { <redacted> }";
}

/// <summary>
/// The customer's account contact. <see cref="Merged"/> lists contacts merged into it by this call (explicit link or auto-merge
/// policy); <see cref="Suggestions"/> lists link suggestions created or already pending.
/// </summary>
public sealed record CustomerResolved(ContactView Contact, bool Created, IReadOnlyList<ContactMergedInto> Merged, IReadOnlyList<Guid> Suggestions);

/// <summary>Outcome of <see cref="IContactLinkingService.ResolveCustomerAsync"/>. Switch over every case without a discard arm.</summary>
public union CustomerResolution(CustomerResolved, ContactLinkConflict, ContactRefused);

/// <summary>Never act on matching claimed evidence.</summary>
public sealed record LinkingOff
{
    private LinkingOff()
    {
    }

    public static LinkingOff Instance { get; } = new();
}

/// <summary>Store a link suggestion for an agent to accept or reject.</summary>
public sealed record LinkingSuggest
{
    private LinkingSuggest()
    {
    }

    public static LinkingSuggest Instance { get; } = new();
}

/// <summary>
/// Minimum evidence for automatic merges. A candidate (a contact holding a verified claim's address as claimed evidence) is merged
/// into the account contact only when at least <see cref="MinimumMatchingIdentities"/> verified claims of the listed
/// <see cref="Kinds"/> match it and, when <see cref="RequireUnverifiedCandidate"/>, the candidate holds no verified identity at all.
/// Otherwise a suggestion is stored. Claimed-only evidence never auto-merges.
/// </summary>
public sealed record ContactAutoMergeRules
{
    public ContactAutoMergeRules(IEnumerable<ContactIdentityKind> kinds, int minimumMatchingIdentities = 1, bool requireUnverifiedCandidate = true)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var set = new SortedSet<ContactIdentityKind>();
        foreach (var kind in kinds)
        {
            if (kind is not (ContactIdentityKind.Email or ContactIdentityKind.Phone or ContactIdentityKind.WhatsApp or ContactIdentityKind.SocialHandle))
                throw new ArgumentException("Auto-merge kinds are email, phone, WhatsApp or social handle.", nameof(kinds));
            set.Add(kind);
        }
        if (set.Count == 0)
            throw new ArgumentException("At least one kind is required.", nameof(kinds));
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumMatchingIdentities, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumMatchingIdentities, CustomerResolveRequest.MaximumClaims);
        Kinds = [.. set];
        MinimumMatchingIdentities = minimumMatchingIdentities;
        RequireUnverifiedCandidate = requireUnverifiedCandidate;
    }

    /// <summary>Distinct kinds in ascending order.</summary>
    public IReadOnlyList<ContactIdentityKind> Kinds { get; }
    public int MinimumMatchingIdentities { get; }
    public bool RequireUnverifiedCandidate { get; }

    public bool Equals(ContactAutoMergeRules? other) =>
        other is not null && Kinds.SequenceEqual(other.Kinds) && MinimumMatchingIdentities == other.MinimumMatchingIdentities
        && RequireUnverifiedCandidate == other.RequireUnverifiedCandidate;

    public override int GetHashCode() => HashCode.Combine(Kinds.Count, MinimumMatchingIdentities, RequireUnverifiedCandidate);
}

/// <summary>Merge qualifying candidates automatically (see <see cref="ContactAutoMergeRules"/>); suggest the rest.</summary>
public sealed record LinkingAutoMerge(ContactAutoMergeRules Rules)
{
    public ContactAutoMergeRules Rules { get; } = Rules ?? throw new ArgumentNullException(nameof(Rules));
}

/// <summary>
/// Workspace policy for contacts holding a newly verified address as claimed evidence. Never crosses scopes. Persisted through
/// <see cref="ContactLinkingCodec"/>, never as union JSON. Switch over every case without a discard arm.
/// </summary>
public union ContactLinkingPolicy(LinkingOff, LinkingSuggest, LinkingAutoMerge);

/// <summary>The workspace policy with its scope and revision (first is 1).</summary>
public sealed record StoredContactLinkingPolicy(TicketScope Scope, ContactLinkingPolicy Policy, long Revision);

/// <summary>Suggestion lifecycle. Values are persisted; never renumber.</summary>
public enum ContactLinkSuggestionStatus
{
    Pending = 1,
    Accepted = 2,
    Rejected = 3,

    /// <summary>One of the contacts was merged, deleted or already combined; the suggestion no longer applies.</summary>
    Superseded = 4,
}

/// <summary>
/// A proposal to merge <see cref="Candidate"/> into <see cref="Account"/> (the contact holding the verified account) because the
/// candidate holds <see cref="Evidence"/> as claimed evidence. <see cref="EvidenceVerified"/> tells whether the incoming evidence was
/// verified. Contains personal data (the evidence address); views expose only its kind.
/// </summary>
public sealed record ContactLinkSuggestion
{
    public ContactLinkSuggestion(Guid id, ContactId candidate, ContactId account, ContactIdentityAddress evidence, bool evidenceVerified,
        ContactLinkSuggestionStatus status, DateTimeOffset createdAt, DateTimeOffset updatedAt, AccessActor createdBy, AccessActor? decidedBy = null,
        Guid? merge = null)
    {
        if (id == Guid.Empty || merge == Guid.Empty)
            throw new ArgumentException("Suggestion and merge identifiers must be non-empty.");
        if (candidate == default || account == default || candidate == account)
            throw new ArgumentException("Candidate and account must be initialized and distinct.");
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(createdBy);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (status == ContactLinkSuggestionStatus.Pending && (decidedBy is not null || merge is not null))
            throw new ArgumentException("Pending suggestions carry no decision.");
        if (merge is not null && status != ContactLinkSuggestionStatus.Accepted)
            throw new ArgumentException("Only accepted suggestions carry a merge.", nameof(merge));
        Id = id;
        Candidate = candidate;
        Account = account;
        Evidence = evidence;
        EvidenceVerified = evidenceVerified;
        Status = status;
        CreatedAt = ContactText.Instant(createdAt);
        UpdatedAt = ContactText.Instant(updatedAt);
        if (UpdatedAt < CreatedAt)
            throw new ArgumentException("A suggestion cannot be updated before it was created.", nameof(updatedAt));
        CreatedBy = createdBy;
        DecidedBy = decidedBy;
        Merge = merge;
    }

    public Guid Id { get; }
    public ContactId Candidate { get; }
    public ContactId Account { get; }
    public ContactIdentityAddress Evidence { get; }
    public bool EvidenceVerified { get; }
    public ContactLinkSuggestionStatus Status { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public AccessActor CreatedBy { get; }
    public AccessActor? DecidedBy { get; }

    /// <summary>The merge performed when accepted.</summary>
    public Guid? Merge { get; }

    /// <summary>A copy in <paramref name="status"/>, decided by <paramref name="actor"/> at <paramref name="at"/>.</summary>
    public ContactLinkSuggestion Decide(ContactLinkSuggestionStatus status, AccessActor? actor, DateTimeOffset at, Guid? merge = null) =>
        new(Id, Candidate, Account, Evidence, EvidenceVerified, status, CreatedAt, at < UpdatedAt ? UpdatedAt : at, CreatedBy, actor, merge);

    public override string ToString() => $"ContactLinkSuggestion {{ Id = {Id}, Status = {Status} }}";
}

/// <summary>A suggestion with its scope and revision (first is 1).</summary>
public sealed record StoredContactLinkSuggestion(TicketScope Scope, ContactLinkSuggestion Suggestion, long Revision);

/// <summary>A suggestion as shown to a principal: identifiers and the evidence kind only, never the address.</summary>
public sealed record ContactLinkSuggestionView(
    Guid Id,
    long Revision,
    ContactId Candidate,
    ContactId Account,
    ContactIdentityKind EvidenceKind,
    bool EvidenceVerified,
    ContactLinkSuggestionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? Merge)
{
    public static ContactLinkSuggestionView From(StoredContactLinkSuggestion stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var suggestion = stored.Suggestion;
        return new(suggestion.Id, stored.Revision, suggestion.Candidate, suggestion.Account, suggestion.Evidence.Kind, suggestion.EvidenceVerified,
            suggestion.Status, suggestion.CreatedAt, suggestion.UpdatedAt, suggestion.Merge);
    }
}

/// <summary>A keyset-paged suggestion listing ordered by identifier, optionally for one contact (as candidate or account).</summary>
public sealed record ContactLinkSuggestionQuery
{
    public const int MaximumLimit = 100;
    public const int DefaultMaximumRowsExamined = 1000;

    public ContactLinkSuggestionQuery(ContactLinkSuggestionStatus status = ContactLinkSuggestionStatus.Pending, ContactId? contact = null, int limit = 50, Guid? after = null)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (contact == default(ContactId))
            throw new ArgumentException("Contact identifiers must be initialized.", nameof(contact));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);
        if (after == Guid.Empty)
            throw new ArgumentException("Cursors must be non-empty.", nameof(after));
        Status = status;
        Contact = contact;
        Limit = limit;
        After = after;
    }

    public ContactLinkSuggestionStatus Status { get; }
    public ContactId? Contact { get; }
    public int Limit { get; }
    public Guid? After { get; }

    public ContactLinkSuggestionQuery WithAfter(Guid? after) => new(Status, Contact, Limit, after);
}

/// <summary>A store page; may be short or empty (bounded scan) while <see cref="Next"/> is set.</summary>
public sealed record ContactLinkSuggestionStorePage(IReadOnlyList<StoredContactLinkSuggestion> Suggestions, Guid? Next);

/// <summary>A page of suggestion views; <see cref="Next"/> continues the same query.</summary>
public sealed record ContactLinkSuggestionPage(IReadOnlyList<ContactLinkSuggestionView> Suggestions, Guid? Next);

/// <summary>The suggestion was accepted: <see cref="Merge"/> merged the candidate into the account contact.</summary>
public sealed record ContactLinkSuggestionAccepted(ContactLinkSuggestionView Suggestion, ContactMergedInto Merge);

/// <summary>Outcome of accepting a suggestion. Switch over every case without a discard arm.</summary>
public union ContactLinkSuggestionAcceptResult(ContactLinkSuggestionAccepted, ContactLinkConflict, ContactRefused);

public sealed record LinkingWriteApplied<T>(T Value, bool Created = true);

public sealed record LinkingWriteNotFound
{
    private LinkingWriteNotFound()
    {
    }

    public static LinkingWriteNotFound Instance { get; } = new();
}

public sealed record LinkingWriteConflict(long CurrentRevision);

/// <summary>Outcome of a linking store write. Switch over every case without a discard arm.</summary>
public union ContactLinkingWriteResult<T>(LinkingWriteApplied<T>, LinkingWriteNotFound, LinkingWriteConflict, ContactWriteRejected);

/// <summary>
/// Durable linking state: the per-workspace policy and link suggestions. Contract:
/// <list type="bullet">
/// <item>Every operation is confined to one <see cref="TicketScope"/>.</item>
/// <item>Policy puts create at revision 1 (<see langword="null"/> expected revision; conflict when present) and compare-and-set afterwards.</item>
/// <item>At most one <see cref="ContactLinkSuggestionStatus.Pending"/> suggestion exists per (candidate, account) pair, enforced
/// atomically: creating a duplicate returns the existing pending suggestion with <see cref="LinkingWriteApplied{T}.Created"/> false.
/// At most <see cref="MaximumPendingSuggestions"/> pending suggestions exist per scope.</item>
/// <item>Suggestion updates are compare-and-set; the pair, evidence and creation fields are immutable.</item>
/// <item>Listing applies the <see cref="AccessFilter"/> to both the candidate and the account contact (resource shape of
/// <see cref="ContactAccess.Resource(StoredContact)"/>, tombstones included) inside a bounded keyset scan; suggestions whose contacts
/// no longer exist are never returned.</item>
/// </list>
/// </summary>
public interface IContactLinkingStore
{
    public const int MaximumPendingSuggestions = 100_000;

    Task<StoredContactLinkingPolicy?> GetPolicyAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<ContactLinkingWriteResult<StoredContactLinkingPolicy>> PutPolicyAsync(TicketScope scope, ContactLinkingPolicy policy, long? expectedRevision, CancellationToken cancellationToken = default);

    Task<StoredContactLinkSuggestion?> GetSuggestionAsync(TicketScope scope, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Creates a pending suggestion, or returns the pending one for the same pair.</summary>
    Task<ContactLinkingWriteResult<StoredContactLinkSuggestion>> CreateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, CancellationToken cancellationToken = default);

    Task<ContactLinkingWriteResult<StoredContactLinkSuggestion>> UpdateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, long expectedRevision, CancellationToken cancellationToken = default);

    Task<ContactLinkSuggestionStorePage> ListSuggestionsAsync(TicketScope scope, ContactLinkSuggestionQuery query, AccessFilter filter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Account linking for customers. Every operation authorizes through <see cref="IAccessEvaluator"/>; merges run through
/// <see cref="IContactService.MergeAsync"/> (<c>contact:merge</c>, audited, conversations follow). Addresses remain correlation
/// evidence: only the caller-authenticated account is trusted, and claimed evidence never auto-merges.
/// </summary>
public interface IContactLinkingService
{
    /// <summary>
    /// Requires <c>contact:link</c> (and <c>contact:merge</c> when a merge is needed). Idempotent: repeating a completed link returns
    /// <see cref="ContactAlreadyLinked"/>. Concurrent links of one account converge on a single account contact.
    /// </summary>
    Task<ContactLinkResult> LinkAccountAsync(PrincipalRef actor, ContactLinkRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes the verified account identity from the contact (<c>contact:link</c>, audited). Merges are undone with <see cref="IContactService.UnmergeAsync"/>.</summary>
    Task<ContactResult<ContactView>> UnlinkAccountAsync(PrincipalRef actor, ContactId contact, long expectedRevision, ExternalAccountRef account, string? justification = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Application login helper (<c>contact:link</c>; <c>contact:create</c> to create; <c>contact:merge</c> for merges). See
    /// <see cref="CustomerResolveRequest"/>.
    /// </summary>
    Task<CustomerResolution> ResolveCustomerAsync(PrincipalRef actor, CustomerResolveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:read</c>; no stored policy means <see cref="LinkingOff"/>.</summary>
    Task<ContactResult<StoredContactLinkingPolicy>> GetPolicyAsync(PrincipalRef actor, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact-linking:manage</c>; audited. <paramref name="expectedRevision"/> is <see langword="null"/> to create.</summary>
    Task<ContactResult<StoredContactLinkingPolicy>> SetPolicyAsync(PrincipalRef actor, ContactLinkingPolicy policy, long? expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:link</c>; rows are filtered by the planned <c>contact:read</c> filter on both contacts.</summary>
    Task<ContactResult<ContactLinkSuggestionPage>> ListSuggestionsAsync(PrincipalRef actor, ContactLinkSuggestionQuery query, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:read</c>, <c>contact:link</c> and <c>contact:merge</c> on both contacts; merges the candidate into the account contact; audited.</summary>
    Task<ContactLinkSuggestionAcceptResult> AcceptSuggestionAsync(PrincipalRef actor, Guid suggestion, long expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>Requires <c>contact:read</c> and <c>contact:link</c> on both contacts; audited.</summary>
    Task<ContactResult<ContactLinkSuggestionView>> RejectSuggestionAsync(PrincipalRef actor, Guid suggestion, long expectedRevision, string? justification = null,
        CancellationToken cancellationToken = default);
}
