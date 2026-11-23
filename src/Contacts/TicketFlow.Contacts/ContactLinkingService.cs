using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// Default <see cref="IContactLinkingService"/>. Identity attachments are single atomic contact batches carrying a
/// <see cref="ContactAuditKind.Linked"/> audit event; merges go through <see cref="IContactService.MergeAsync"/> so they keep its
/// authorization (<c>contact:merge</c> on both contacts and the result), merge record, audit and change events (conversations follow
/// through the contact merge follower). Verified identity uniqueness in the store makes concurrent links converge: a loser retries and
/// merges into the winner. Never logs addresses or identifiers.
/// </summary>
public sealed class ContactLinkingService : IContactLinkingService
{
    private const int MaximumAttempts = 8;
    private const int MaximumChainLength = 16;
    private const string LinkJustification = "Verified account link.";
    private const string AutoMergeJustification = "Automatic account link from verified evidence.";
    private const string AcceptJustification = "Accepted contact link suggestion.";

    private readonly IContactStore store;
    private readonly IContactLinkingStore linking;
    private readonly IContactService contacts;
    private readonly IContactAttributeDefinitionStore definitions;
    private readonly IContactAuditSink audit;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;
    private readonly IAccessFieldPolicyStore policies;
    private readonly IReadOnlyList<IContactChangeObserver> observers;
    private readonly ContactsOptions options;
    private readonly TimeProvider time;

    public ContactLinkingService(
        IContactStore store,
        IContactLinkingStore linking,
        IContactService contacts,
        IContactAttributeDefinitionStore definitions,
        IContactAuditSink audit,
        IAccessEvaluator evaluator,
        IAccessQueryPlanner planner,
        IAccessFieldPolicyStore policies,
        IEnumerable<IContactChangeObserver> observers,
        IOptions<ContactsOptions> options,
        TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.linking = linking ?? throw new ArgumentNullException(nameof(linking));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        ArgumentNullException.ThrowIfNull(observers);
        this.observers = [.. observers];
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    // ---- Explicit links ----

    public async Task<ContactLinkResult> LinkAccountAsync(PrincipalRef actor, ContactLinkRequest request, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!await FieldsVisibleAsync(actor, scope, [request.Account.Address, .. request.Evidence is { } evidence ? [evidence] : Array.Empty<ContactIdentityAddress>()], cancellationToken)
            .ConfigureAwait(false))
            return ContactRefused.Forbidden;
        return await LinkCoreAsync(actor, scope, request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContactLinkResult> LinkCoreAsync(PrincipalRef actor, TicketScope scope, ContactLinkRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            if (await LinkOnceAsync(actor, scope, request, cancellationToken).ConfigureAwait(false) is { } result)
                return result;
        }
        return new ContactLinkConflict(ContactLinkConflictReason.Contended, []);
    }

    /// <summary>One optimistic attempt; <see langword="null"/> asks the caller to retry after a concurrent change.</summary>
    private async Task<ContactLinkResult?> LinkOnceAsync(PrincipalRef actor, TicketScope scope, ContactLinkRequest request, CancellationToken cancellationToken)
    {
        var account = request.Account.Address;
        var decisions = await evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Link, ContactPermissions.Read], cancellationToken).ConfigureAwait(false);
        StoredContact? subject;
        var subjectLinkPermitted = false;
        if (request.Contact is { } id)
        {
            var named = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
            if (named is null)
                return ContactRefused.NotFound;
            if (!(await decisions.DecideAsync(ContactPermissions.Link, ContactAccess.Resource(named), cancellationToken).ConfigureAwait(false)).IsPermitted)
                return ContactRefused.Forbidden;
            subject = await LiveAsync(scope, named, cancellationToken).ConfigureAwait(false);
            if (subject is null)
                return ContactRefused.NotFound;
            subjectLinkPermitted = subject.Id == named.Id;
        }
        else
        {
            var rawOwners = await store.FindByIdentityAsync(scope, request.Evidence!, cancellationToken).ConfigureAwait(false);
            var owners = new List<ContactId>(rawOwners.Count);
            if (rawOwners.Count > 0)
            {
                var seenOwners = new HashSet<ContactId>();
                foreach (var owner in rawOwners)
                    if (seenOwners.Add(owner.Contact))
                        owners.Add(owner.Contact);
                if (owners.Count > 1)
                {
                    owners.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
                    return await AmbiguousAsync(actor, scope, owners, cancellationToken).ConfigureAwait(false);
                }
            }
            subject = owners.Count == 0 ? null : await store.GetAsync(scope, owners[0], cancellationToken).ConfigureAwait(false);
            if (owners.Count == 1 && (subject is null || subject.Contact.MergedInto is not null))
                return null;
        }

        var holder = await HolderAsync(scope, account, cancellationToken).ConfigureAwait(false);
        if (subject is null)
        {
            if (holder is null)
                return ContactRefused.NotFound;
            return (await decisions.DecideAsync(ContactPermissions.Link, ContactAccess.Resource(holder), cancellationToken).ConfigureAwait(false)).IsPermitted
                ? new ContactAlreadyLinked(await ViewAsync(actor, holder, cancellationToken).ConfigureAwait(false))
                : ContactRefused.Forbidden;
        }
        if (!subjectLinkPermitted && !(await decisions.DecideAsync(ContactPermissions.Link, ContactAccess.Resource(subject), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        if (holder?.Id == subject.Id)
            return new ContactAlreadyLinked(await ViewAsync(actor, subject, cancellationToken).ConfigureAwait(false));
        if (HoldsOtherAccount(subject.Contact, account))
            return new ContactLinkConflict(ContactLinkConflictReason.OtherAccount, [subject.Id]);

        if (holder is null)
        {
            var now = Now;
            var identities = subject.Contact.Identities.Where(identity => identity.Address != account)
                .Append(new ContactIdentity(account, new IdentityVerified(request.VerificationSource, now)));
            Contact updated;
            try
            {
                updated = subject.Contact.With(identities: identities, updatedAt: Later(subject.Contact.UpdatedAt, now));
            }
            catch (ArgumentException)
            {
                return new ContactRefused(ContactRefusal.Rejected, ["The contact cannot hold more identities."]);
            }
            var auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Linked, AccessActor.From(actor), subject.Id,
                justification: request.Justification);
            var batch = new ContactWriteBatch(puts: [new ContactPut(updated, subject.Revision)], changes: [Change(ContactChangeKind.Updated, subject.Id, now), Change(ContactChangeKind.IdentityLinked, subject.Id, now)],
                audit: auditEvent);
            var result = await store.WriteAsync(scope, batch, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case ContactWriteApplied applied:
                    await NotifyAsync(applied.Changes, cancellationToken).ConfigureAwait(false);
                    return new ContactLinked(await ViewAsync(actor, applied.Contacts[0], cancellationToken).ConfigureAwait(false), auditEvent.Id);
                case ContactWriteConflict:
                case ContactWriteNotFound:
                case ContactIdentityTaken:
                    return null;
                case ContactWriteRejected rejected:
                    return new ContactRefused(ContactRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]);
            }
            throw new InvalidOperationException("Unknown write outcome.");
        }

        if (!(await decisions.DecideAsync(ContactPermissions.Link, ContactAccess.Resource(holder), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        // Like the auto-merge and accept paths: never combine two different verified accounts of one namespace.
        if (SharesNamespaceWithOtherAccount(subject.Contact, holder.Contact))
            return new ContactLinkConflict(ContactLinkConflictReason.OtherAccount, [subject.Id]);
        switch (await MergeAsync(actor, scope, holder, subject, request.Justification ?? LinkJustification, cancellationToken).ConfigureAwait(false))
        {
            case ContactMergedInto merged:
                await audit.RecordAsync(new ContactAuditEvent(Guid.CreateVersion7(), Now, scope, ContactAuditKind.Linked, AccessActor.From(actor), subject.Id, holder.Id,
                    merged.MergeId, justification: request.Justification), cancellationToken).ConfigureAwait(false);
                return merged;
            case ContactRefused { Reason: ContactRefusal.Conflict or ContactRefusal.Merged or ContactRefusal.NotFound }:
                return null;
            case ContactRefused refused:
                return refused;
        }
        throw new InvalidOperationException("Unknown merge outcome.");
    }

    public async Task<ContactResult<ContactView>> UnlinkAccountAsync(PrincipalRef actor, ContactId contact, long expectedRevision, ExternalAccountRef account, string? justification = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(contact);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        ArgumentNullException.ThrowIfNull(account);
        if (justification is not null && (justification.Length > ContactAuditEvent.MaximumJustificationLength || string.IsNullOrWhiteSpace(justification)))
            return ContactRefused.Invalid("The justification is invalid.");
        var stored = await store.GetAsync(scope, contact, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ContactRefused.NotFound;
        if (stored.Revision != expectedRevision)
            return new ContactRefused(ContactRefusal.Conflict, [], stored.Revision);
        if (!await PermittedAsync(actor, ContactPermissions.Link, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false)
            || !await FieldsVisibleAsync(actor, scope, [account.Address], cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        if (stored.Contact.MergedInto is { } mergedInto)
            return new ContactRefused(ContactRefusal.Merged, [], stored.Revision, mergedInto);
        if (!stored.Contact.Identities.Any(identity => identity.Address == account.Address && identity.Verification.IsVerified))
            return ContactRefused.Invalid("The account is not linked to the contact.");
        var now = Now;
        ContactAuditEvent auditEvent;
        try
        {
            auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Unlinked, AccessActor.From(actor), contact, justification: justification);
        }
        catch (ArgumentException)
        {
            return ContactRefused.Invalid("The justification is invalid.");
        }
        var updated = stored.Contact.With(identities: stored.Contact.Identities.Where(identity => identity.Address != account.Address),
            updatedAt: Later(stored.Contact.UpdatedAt, now));
        var batch = new ContactWriteBatch(puts: [new ContactPut(updated, expectedRevision)], changes: [Change(ContactChangeKind.Updated, contact, now), Change(ContactChangeKind.IdentityUnlinked, contact, now)],
            audit: auditEvent);
        switch (await store.WriteAsync(scope, batch, cancellationToken).ConfigureAwait(false))
        {
            case ContactWriteApplied applied:
                await NotifyAsync(applied.Changes, cancellationToken).ConfigureAwait(false);
                return new ContactApplied<ContactView>(await ViewAsync(actor, applied.Contacts[0], cancellationToken).ConfigureAwait(false));
            case ContactWriteConflict conflict:
                return new ContactRefused(ContactRefusal.Conflict, [], conflict.CurrentRevision);
            case ContactWriteNotFound:
                return ContactRefused.NotFound;
            case ContactIdentityTaken:
                return new ContactRefused(ContactRefusal.Rejected, ["The store refused the write."]);
            case ContactWriteRejected rejected:
                return new ContactRefused(ContactRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]);
        }
        throw new InvalidOperationException("Unknown write outcome.");
    }

    // ---- Login helper ----

    public async Task<CustomerResolution> ResolveCustomerAsync(PrincipalRef actor, CustomerResolveRequest request, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        IEnumerable<ContactIdentityAddress> visible = [request.Account.Address, .. request.Claims.Select(claim => claim.Address),
            .. request.LinkEvidence is { } evidence ? [evidence] : Array.Empty<ContactIdentityAddress>()];
        if (!await PermittedAsync(actor, ContactPermissions.Link, ContactAccess.Workspace(scope), cancellationToken).ConfigureAwait(false)
            || !await FieldsVisibleAsync(actor, scope, visible, cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;

        var merged = new List<ContactMergedInto>();
        StoredContact? account = null;
        var created = false;
        if (request.LinkContact is not null || request.LinkEvidence is not null)
        {
            var link = new ContactLinkRequest(request.Account, request.VerificationSource, request.LinkContact, request.LinkEvidence);
            switch (await LinkCoreAsync(actor, scope, link, cancellationToken).ConfigureAwait(false))
            {
                case ContactLinked linked:
                    account = await store.GetAsync(scope, linked.Contact.Id, cancellationToken).ConfigureAwait(false);
                    break;
                case ContactMergedInto into:
                    merged.Add(into);
                    account = await store.GetAsync(scope, into.Target.Id, cancellationToken).ConfigureAwait(false);
                    break;
                case ContactAlreadyLinked already:
                    account = await store.GetAsync(scope, already.Contact.Id, cancellationToken).ConfigureAwait(false);
                    break;
                case ContactLinkConflict conflict:
                    return conflict;
                case ContactRefused { Reason: ContactRefusal.NotFound }:
                    break;
                case ContactRefused refused:
                    return refused;
            }
        }

        for (var attempt = 0; account is null; attempt++)
        {
            if (attempt == MaximumAttempts)
                return new ContactLinkConflict(ContactLinkConflictReason.Contended, []);
            var holder = await HolderAsync(scope, request.Account.Address, cancellationToken).ConfigureAwait(false);
            if (holder is not null)
            {
                if (!await PermittedAsync(actor, ContactPermissions.Link, ContactAccess.Resource(holder), cancellationToken).ConfigureAwait(false))
                    return ContactRefused.Forbidden;
                account = holder;
                break;
            }
            var now = Now;
            var identities = new List<ContactIdentity>(request.Claims.Count + 1) { new(request.Account.Address, new IdentityVerified(request.VerificationSource, now)) };
            foreach (var claim in request.Claims)
            {
                var verified = claim.Verified && await HolderIdAsync(scope, claim.Address, cancellationToken).ConfigureAwait(false) is null;
                identities.Add(new ContactIdentity(claim.Address, verified ? new IdentityVerified(request.VerificationSource, now) : new IdentityClaimed(request.VerificationSource, now)));
            }
            var contact = new Contact(options.IdFactory(), new ContactProfile(request.DisplayName), identities, [], [], false, now, now);
            if (!await PermittedAsync(actor, ContactPermissions.Create, ContactAccess.Resource(scope, contact), cancellationToken).ConfigureAwait(false)
                || !await PermittedAsync(actor, ContactPermissions.Link, ContactAccess.Resource(scope, contact), cancellationToken).ConfigureAwait(false))
                return ContactRefused.Forbidden;
            var batch = new ContactWriteBatch(puts: [new ContactPut(contact, null)], changes: [Change(ContactChangeKind.Created, contact.Id, now), Change(ContactChangeKind.IdentityLinked, contact.Id, now)],
                audit: new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Linked, AccessActor.From(actor), contact.Id, count: identities.Count));
            switch (await store.WriteAsync(scope, batch, cancellationToken).ConfigureAwait(false))
            {
                case ContactWriteApplied applied:
                    await NotifyAsync(applied.Changes, cancellationToken).ConfigureAwait(false);
                    account = applied.Contacts[0];
                    created = true;
                    break;
                case ContactIdentityTaken:
                case ContactWriteConflict:
                    continue;
                case ContactWriteNotFound:
                    return ContactRefused.NotFound;
                case ContactWriteRejected rejected:
                    return new ContactRefused(ContactRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]);
            }
        }

        if (!created)
        {
            switch (await AttachClaimsAsync(actor, scope, account, request, cancellationToken).ConfigureAwait(false))
            {
                case ContactApplied<StoredContact> attached:
                    account = attached.Value;
                    break;
                case ContactRefused refused:
                    return refused;
            }
        }

        var suggestions = new List<Guid>();
        account = await ApplyPolicyAsync(actor, scope, account, request.Claims, merged, suggestions, cancellationToken).ConfigureAwait(false);
        return new CustomerResolved(await ViewAsync(actor, account, cancellationToken).ConfigureAwait(false), created, merged, suggestions);
    }

    /// <summary>Adds missing claims (verified ones as verified unless another contact holds them verified) in one audited batch.</summary>
    private async Task<ContactResult<StoredContact>> AttachClaimsAsync(PrincipalRef actor, TicketScope scope, StoredContact account, CustomerResolveRequest request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var now = Now;
            var identities = account.Contact.Identities.ToDictionary(identity => identity.Address);
            var added = 0;
            foreach (var claim in request.Claims)
            {
                identities.TryGetValue(claim.Address, out var existing);
                if (existing is { Verification.IsVerified: true } || (existing is not null && !claim.Verified))
                    continue;
                if (claim.Verified)
                {
                    var holderId = await HolderIdAsync(scope, claim.Address, cancellationToken).ConfigureAwait(false);
                    if (holderId is { } existingHolder && existingHolder != account.Id)
                    {
                        if (existing is null)
                        {
                            identities[claim.Address] = new ContactIdentity(claim.Address, new IdentityClaimed(request.VerificationSource, now));
                            added++;
                        }
                        continue;
                    }
                    identities[claim.Address] = new ContactIdentity(claim.Address, new IdentityVerified(request.VerificationSource, now));
                }
                else
                    identities[claim.Address] = new ContactIdentity(claim.Address, new IdentityClaimed(request.VerificationSource, now));
                added++;
            }
            if (added == 0)
                return new ContactApplied<StoredContact>(account);
            Contact updated;
            try
            {
                updated = account.Contact.With(identities: identities.Values, updatedAt: Later(account.Contact.UpdatedAt, now));
            }
            catch (ArgumentException)
            {
                return new ContactApplied<StoredContact>(account);
            }
            var batch = new ContactWriteBatch(puts: [new ContactPut(updated, account.Revision)], changes: [Change(ContactChangeKind.Updated, account.Id, now)],
                audit: new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Linked, AccessActor.From(actor), account.Id, count: added));
            switch (await store.WriteAsync(scope, batch, cancellationToken).ConfigureAwait(false))
            {
                case ContactWriteApplied applied:
                    await NotifyAsync(applied.Changes, cancellationToken).ConfigureAwait(false);
                    return new ContactApplied<StoredContact>(applied.Contacts[0]);
                case ContactWriteConflict:
                case ContactIdentityTaken:
                    var reloaded = await store.GetAsync(scope, account.Id, cancellationToken).ConfigureAwait(false);
                    if (reloaded is null || reloaded.Contact.MergedInto is not null)
                        return new ContactRefused(ContactRefusal.Conflict, ["The account contact changed concurrently; retry."]);
                    account = reloaded;
                    continue;
                case ContactWriteNotFound:
                    return ContactRefused.NotFound;
                case ContactWriteRejected rejected:
                    return new ContactRefused(ContactRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]);
            }
        }
        return new ContactRefused(ContactRefusal.Conflict, ["The account contact changed concurrently; retry."]);
    }

    /// <summary>
    /// Applies the workspace policy to other contacts holding a claimed address: suggestions, or merges for candidates that satisfy
    /// the auto-merge rules through verified claims. Verified holders only ever produce suggestions; claimed claims are ignored unless
    /// the policy opts into claimed evidence (then they only suggest, and accepting needs explicit confirmation). Candidate access
    /// checks share one <see cref="IAccessDecisionContext"/>.
    /// </summary>
    private async Task<StoredContact> ApplyPolicyAsync(PrincipalRef actor, TicketScope scope, StoredContact account, IReadOnlyList<CustomerClaim> claims,
        List<ContactMergedInto> merged, List<Guid> suggestions, CancellationToken cancellationToken)
    {
        var storedPolicy = await linking.GetPolicyAsync(scope, cancellationToken).ConfigureAwait(false);
        ContactLinkingPolicy policy = storedPolicy is null ? LinkingOff.Instance : storedPolicy.Policy;
        var rules = (policy.Value as LinkingAutoMerge)?.Rules;
        if (policy.Value is LinkingOff)
            return account;
        var includeClaimed = (policy.Value as LinkingSuggest)?.IncludeClaimedEvidence ?? (policy.Value as LinkingAutoMerge)?.IncludeClaimedEvidence ?? false;

        var candidates = new SortedDictionary<string, List<(ContactIdentityAddress Address, bool Qualifies)>>(StringComparer.Ordinal);
        foreach (var claim in claims.Where(claim => claim.Verified || includeClaimed))
        {
            foreach (var owner in await store.FindByIdentityAsync(scope, claim.Address, cancellationToken).ConfigureAwait(false))
            {
                if (owner.Contact == account.Id)
                    continue;
                if (!candidates.TryGetValue(owner.Contact.Value, out var evidence))
                    candidates[owner.Contact.Value] = evidence = [];
                evidence.Add((claim.Address, claim.Verified && !owner.Verification.IsVerified && rules?.Kinds.Contains(claim.Address.Kind) == true));
            }
        }

        if (candidates.Count == 0)
            return account;
        var limitedCandidates = new List<KeyValuePair<string, List<(ContactIdentityAddress Address, bool Qualifies)>>>(Math.Min(candidates.Count, IContactStore.MaximumIdentityOwners));
        var candidateIds = new List<ContactId>(Math.Min(candidates.Count, IContactStore.MaximumIdentityOwners));
        foreach (var entry in candidates)
        {
            if (limitedCandidates.Count == IContactStore.MaximumIdentityOwners)
                break;
            limitedCandidates.Add(entry);
            candidateIds.Add(new ContactId(entry.Key));
        }
        var decisionsTask = evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Link], cancellationToken);
        var loadedTask = store.GetManyAsync(scope, candidateIds, cancellationToken);
        await Task.WhenAll(decisionsTask, loadedTask).ConfigureAwait(false);
        var decisions = decisionsTask.Result;
        var loaded = loadedTask.Result;
        foreach (var (candidateKey, evidence) in limitedCandidates)
        {
            if (!loaded.TryGetValue(new ContactId(candidateKey), out var candidate) || candidate.Contact.MergedInto is not null)
                continue;
            var qualifies = rules is not null && evidence.Count(item => item.Qualifies) >= rules.MinimumMatchingIdentities
                && (!rules.RequireUnverifiedCandidate || !candidate.Contact.Identities.Any(identity => identity.Verification.IsVerified))
                && !SharesNamespaceWithOtherAccount(candidate.Contact, account.Contact);
            if (qualifies && await MergeAsync(actor, scope, account, candidate, AutoMergeJustification, cancellationToken).ConfigureAwait(false) is ContactMergedInto into)
            {
                merged.Add(into);
                account = await store.GetAsync(scope, account.Id, cancellationToken).ConfigureAwait(false) ?? account;
                continue;
            }
            if (!(await decisions.DecideAsync(ContactPermissions.Link, ContactAccess.Resource(candidate), cancellationToken).ConfigureAwait(false)).IsPermitted)
                continue;
            var pick = evidence.FirstOrDefault(item => item.Qualifies);
            var address = pick.Address ?? evidence.Select(item => item.Address).FirstOrDefault(item => claims.First(claim => claim.Address == item).Verified) ?? evidence[0].Address;
            var now = Now;
            var suggestion = new ContactLinkSuggestion(Guid.CreateVersion7(), candidate.Id, account.Id, address,
                claims.First(claim => claim.Address == address).Verified, ContactLinkSuggestionStatus.Pending, now, now, AccessActor.From(actor));
            if (await linking.CreateSuggestionAsync(scope, suggestion, cancellationToken).ConfigureAwait(false) is { Value: LinkingWriteApplied<StoredContactLinkSuggestion> stored })
            {
                if (stored.Created)
                    await audit.RecordAsync(new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.LinkSuggested, AccessActor.From(actor), candidate.Id, account.Id)
                        { Suggestion = stored.Value.Suggestion.Id }, cancellationToken).ConfigureAwait(false);
                suggestions.Add(stored.Value.Suggestion.Id);
            }
        }
        return account;
    }

    // ---- Policy ----

    public async Task<ContactResult<StoredContactLinkingPolicy>> GetPolicyAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (!await PermittedAsync(actor, ContactPermissions.Read, ContactAccess.Workspace(scope), cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        return new ContactApplied<StoredContactLinkingPolicy>(await linking.GetPolicyAsync(scope, cancellationToken).ConfigureAwait(false)
            ?? new StoredContactLinkingPolicy(scope, LinkingOff.Instance, 0));
    }

    public async Task<ContactResult<StoredContactLinkingPolicy>> SetPolicyAsync(PrincipalRef actor, ContactLinkingPolicy policy, long? expectedRevision, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        if (policy.Value is null)
            throw new ArgumentException("A policy is required.", nameof(policy));
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (!await PermittedAsync(actor, ContactPermissions.ManageLinking, ContactAccess.Workspace(scope), cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        switch (await linking.PutPolicyAsync(scope, policy, expectedRevision, cancellationToken).ConfigureAwait(false))
        {
            case LinkingWriteApplied<StoredContactLinkingPolicy> applied:
                await audit.RecordAsync(new ContactAuditEvent(Guid.CreateVersion7(), Now, scope, ContactAuditKind.LinkingPolicyChanged, AccessActor.From(actor)), cancellationToken)
                    .ConfigureAwait(false);
                return new ContactApplied<StoredContactLinkingPolicy>(applied.Value);
            case LinkingWriteNotFound:
                return ContactRefused.NotFound;
            case LinkingWriteConflict conflict:
                return new ContactRefused(ContactRefusal.Conflict, [], conflict.CurrentRevision);
            case ContactWriteRejected rejected:
                return new ContactRefused(ContactRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]);
        }
        throw new InvalidOperationException("Unknown write outcome.");
    }

    // ---- Suggestions ----

    public async Task<ContactResult<ContactLinkSuggestionPage>> ListSuggestionsAsync(PrincipalRef actor, ContactLinkSuggestionQuery query, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(query);
        if (!await PermittedAsync(actor, ContactPermissions.Link, ContactAccess.Workspace(scope), cancellationToken).ConfigureAwait(false))
            return ContactRefused.Forbidden;
        var filter = await planner.PlanAsync(new AccessQuery(actor, ContactPermissions.Read, scope), cancellationToken).ConfigureAwait(false);
        var page = await linking.ListSuggestionsAsync(scope, query, filter, cancellationToken).ConfigureAwait(false);
        if (page.Suggestions.Any(stored => stored.Scope != scope || stored.Suggestion.Status != query.Status))
            throw new InvalidOperationException("The linking store returned a suggestion outside the query contract.");
        return new ContactApplied<ContactLinkSuggestionPage>(new([.. page.Suggestions.Select(ContactLinkSuggestionView.From)], page.Next));
    }

    public Task<ContactLinkSuggestionAcceptResult> AcceptSuggestionAsync(PrincipalRef actor, Guid suggestion, long expectedRevision, CancellationToken cancellationToken = default) =>
        AcceptSuggestionAsync(actor, suggestion, expectedRevision, false, cancellationToken);

    public async Task<ContactLinkSuggestionAcceptResult> AcceptSuggestionAsync(PrincipalRef actor, Guid suggestion, long expectedRevision, bool confirmClaimedEvidence,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var (stored, candidate, account, refused) = await LoadPendingAsync(actor, scope, suggestion, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        if (!stored!.Suggestion.EvidenceVerified && !confirmClaimedEvidence)
            return ContactRefused.Invalid("The suggestion rests on claimed (unverified) evidence; accept it with explicit confirmation.");
        if (candidate!.Contact.MergedInto is not null || account!.Contact.MergedInto is not null)
        {
            await DecideAsync(actor, scope, stored!, ContactLinkSuggestionStatus.Superseded, null, cancellationToken).ConfigureAwait(false);
            return new ContactRefused(ContactRefusal.Rejected, ["The suggestion no longer applies: a contact was merged."]);
        }
        if (SharesNamespaceWithOtherAccount(candidate.Contact, account.Contact))
            return new ContactLinkConflict(ContactLinkConflictReason.OtherAccount, [candidate.Id]);
        switch (await MergeAsync(actor, scope, account, candidate, AcceptJustification, cancellationToken).ConfigureAwait(false))
        {
            case ContactMergedInto merged:
                var decided = await DecideAsync(actor, scope, stored!, ContactLinkSuggestionStatus.Accepted, merged.MergeId, cancellationToken).ConfigureAwait(false);
                return new ContactLinkSuggestionAccepted(ContactLinkSuggestionView.From(decided), merged);
            case ContactRefused mergeRefused:
                return mergeRefused;
        }
        throw new InvalidOperationException("Unknown merge outcome.");
    }

    public async Task<ContactResult<ContactLinkSuggestionView>> RejectSuggestionAsync(PrincipalRef actor, Guid suggestion, long expectedRevision, string? justification = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (justification is not null && (justification.Length > ContactAuditEvent.MaximumJustificationLength || string.IsNullOrWhiteSpace(justification)))
            return ContactRefused.Invalid("The justification is invalid.");
        var (stored, _, _, refused) = await LoadPendingAsync(actor, scope, suggestion, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (refused is not null)
            return refused;
        var decided = await DecideAsync(actor, scope, stored!, ContactLinkSuggestionStatus.Rejected, null, cancellationToken, justification).ConfigureAwait(false);
        return decided.Revision == stored!.Revision
            ? new ContactRefused(ContactRefusal.Conflict, [], decided.Revision)
            : new ContactApplied<ContactLinkSuggestionView>(ContactLinkSuggestionView.From(decided));
    }

    private async Task<(StoredContactLinkSuggestion? Stored, StoredContact? Candidate, StoredContact? Account, ContactRefused? Refused)> LoadPendingAsync(
        PrincipalRef actor, TicketScope scope, Guid id, long expectedRevision, CancellationToken cancellationToken)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A suggestion identifier is required.", nameof(id));
        var stored = await linking.GetSuggestionAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return (null, null, null, ContactRefused.NotFound);
        if (stored.Revision != expectedRevision)
            return (null, null, null, new ContactRefused(ContactRefusal.Conflict, [], stored.Revision));
        var loadedTask = store.GetManyAsync(scope, [stored.Suggestion.Candidate, stored.Suggestion.Account], cancellationToken);
        var decisionsTask = evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Read, ContactPermissions.Link], cancellationToken);
        await Task.WhenAll(loadedTask, decisionsTask).ConfigureAwait(false);
        var loaded = loadedTask.Result;
        if (!loaded.TryGetValue(stored.Suggestion.Candidate, out var candidate) || !loaded.TryGetValue(stored.Suggestion.Account, out var account))
            return (null, null, null, ContactRefused.NotFound);
        var decisions = decisionsTask.Result;
        var candidateResource = ContactAccess.Resource(candidate);
        var accountResource = ContactAccess.Resource(account);
        if (!(await decisions.DecideAsync(ContactPermissions.Read, candidateResource, cancellationToken).ConfigureAwait(false)).IsPermitted
            || !(await decisions.DecideAsync(ContactPermissions.Read, accountResource, cancellationToken).ConfigureAwait(false)).IsPermitted
            || !(await decisions.DecideAsync(ContactPermissions.Link, candidateResource, cancellationToken).ConfigureAwait(false)).IsPermitted
            || !(await decisions.DecideAsync(ContactPermissions.Link, accountResource, cancellationToken).ConfigureAwait(false)).IsPermitted)
            return (null, null, null, ContactRefused.Forbidden);
        if (stored.Suggestion.Status != ContactLinkSuggestionStatus.Pending)
            return (null, null, null, ContactRefused.Invalid("The suggestion is not pending."));
        return (stored, candidate, account, null);
    }

    /// <summary>Records the decision (compare-and-set) and its audit event; returns the stored state (unchanged when a concurrent decision won).</summary>
    private async Task<StoredContactLinkSuggestion> DecideAsync(PrincipalRef actor, TicketScope scope, StoredContactLinkSuggestion stored, ContactLinkSuggestionStatus status,
        Guid? merge, CancellationToken cancellationToken, string? justification = null)
    {
        var now = Now;
        var actorRecord = AccessActor.From(actor);
        var decided = stored.Suggestion.Decide(status, status == ContactLinkSuggestionStatus.Superseded ? null : actorRecord, now, merge);
        switch (await linking.UpdateSuggestionAsync(scope, decided, stored.Revision, cancellationToken).ConfigureAwait(false))
        {
            case LinkingWriteApplied<StoredContactLinkSuggestion> applied:
                if (status != ContactLinkSuggestionStatus.Superseded)
                    await audit.RecordAsync(new ContactAuditEvent(Guid.CreateVersion7(), now, scope,
                        status == ContactLinkSuggestionStatus.Accepted ? ContactAuditKind.LinkSuggestionAccepted : ContactAuditKind.LinkSuggestionRejected,
                        actorRecord, stored.Suggestion.Candidate, stored.Suggestion.Account, merge, justification: justification) { Suggestion = stored.Suggestion.Id }, cancellationToken)
                        .ConfigureAwait(false);
                return applied.Value;
            case LinkingWriteNotFound:
            case LinkingWriteConflict:
            case ContactWriteRejected:
                return await linking.GetSuggestionAsync(scope, stored.Suggestion.Id, cancellationToken).ConfigureAwait(false) ?? stored;
        }
        throw new InvalidOperationException("Unknown write outcome.");
    }

    // ---- Helpers ----

    /// <summary>Merges <paramref name="source"/> into <paramref name="target"/> through the audited contact merge.</summary>
    private async Task<ContactMergeAttempt> MergeAsync(PrincipalRef actor, TicketScope scope, StoredContact target, StoredContact source, string justification, CancellationToken cancellationToken)
    {
        var request = new ContactMergeRequest(target.Id, target.Revision, source.Id, source.Revision, ContactAttributeConflict.PreferTarget, justification);
        return await contacts.MergeAsync(actor, request, cancellationToken).ConfigureAwait(false) switch
        {
            ContactApplied<ContactMergeOutcome> applied => new ContactMergedInto(applied.Value.Target, source.Id, applied.Value.MergeId),
            ContactRefused refused => refused,
        };
    }

    private async Task<ContactLinkResult> AmbiguousAsync(PrincipalRef actor, TicketScope scope, IEnumerable<ContactId> candidates, CancellationToken cancellationToken)
    {
        var ids = candidates is IReadOnlyCollection<ContactId> col ? col : [.. candidates];
        if (ids.Count == 0)
            return ContactRefused.Forbidden;
        var loadedTask = store.GetManyAsync(scope, ids, cancellationToken);
        var decisionsTask = evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Read], cancellationToken);
        await Task.WhenAll(loadedTask, decisionsTask).ConfigureAwait(false);
        var loaded = loadedTask.Result;
        var decisions = decisionsTask.Result;
        var readable = new List<ContactId>(loaded.Count);
        foreach (var candidate in ids)
            if (loaded.TryGetValue(candidate, out var stored)
                && (await decisions.DecideAsync(ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
                readable.Add(candidate);
        return readable.Count == 0 ? ContactRefused.Forbidden : new ContactLinkConflict(ContactLinkConflictReason.AmbiguousEvidence, readable);
    }

    /// <summary>The live contact a (possibly tombstoned) contact resolves to along its merge chain.</summary>
    private async Task<StoredContact?> LiveAsync(TicketScope scope, StoredContact stored, CancellationToken cancellationToken)
    {
        var current = stored;
        for (var hop = 0; current.Contact.MergedInto is { } next; hop++)
        {
            if (hop == MaximumChainLength)
                return null;
            current = await store.GetAsync(scope, next, cancellationToken).ConfigureAwait(false);
            if (current is null)
                return null;
        }
        return current;
    }

    private async Task<ContactId?> HolderIdAsync(TicketScope scope, ContactIdentityAddress address, CancellationToken cancellationToken)
    {
        var owners = await store.FindByIdentityAsync(scope, address, cancellationToken).ConfigureAwait(false);
        foreach (var owner in owners)
            if (owner.Verification.IsVerified)
                return owner.Contact;
        return null;
    }

    private async Task<StoredContact?> HolderAsync(TicketScope scope, ContactIdentityAddress address, CancellationToken cancellationToken)
    {
        var holderId = await HolderIdAsync(scope, address, cancellationToken).ConfigureAwait(false);
        return holderId is { } id
            ? await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>True when <paramref name="contact"/> holds a different verified account in the account's namespace.</summary>
    private static bool HoldsOtherAccount(Contact contact, ContactIdentityAddress account) =>
        contact.Identities.Any(identity => identity.Address.Kind == ContactIdentityKind.ExternalUser && identity.Address.Namespace == account.Namespace
            && identity.Address != account && identity.Verification.IsVerified);

    /// <summary>True when the two contacts hold different verified accounts of one namespace (merging would combine two accounts).</summary>
    private static bool SharesNamespaceWithOtherAccount(Contact candidate, Contact account) =>
        account.Identities.Where(identity => identity.Address.Kind == ContactIdentityKind.ExternalUser && identity.Verification.IsVerified)
            .Any(identity => HoldsOtherAccount(candidate, identity.Address));

    /// <summary>The actor may read contacts and none of the addresses' fields is masked for it (no oracle over masked values).</summary>
    private async Task<bool> FieldsVisibleAsync(PrincipalRef actor, TicketScope scope, IEnumerable<ContactIdentityAddress> addresses, CancellationToken cancellationToken)
    {
        var fields = addresses.Select(address => address.Field).ToHashSet();
        return await evaluator.AuthorizeAsync(new AccessRequest(actor, ContactPermissions.Read, ContactAccess.Workspace(scope)), cancellationToken).ConfigureAwait(false) switch
        {
            AccessAllowed => true,
            AccessMasked masked => masked.Fields.All(field => !fields.Contains(field.Field)),
            AccessDenied => false,
        };
    }

    private async Task<bool> PermittedAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken).ConfigureAwait(false)).IsPermitted;

    private async Task<ContactView> ViewAsync(PrincipalRef actor, StoredContact stored, CancellationToken cancellationToken)
    {
        var decisionTask = evaluator.AuthorizeAsync(new AccessRequest(actor, ContactPermissions.Read, ContactAccess.Resource(stored)), cancellationToken);
        var maskingTask = ContactMasking.LoadAsync(stored.Scope, ContactPermissions.Read, definitions, policies, cancellationToken);
        await Task.WhenAll(decisionTask, maskingTask).ConfigureAwait(false);
        return maskingTask.Result.Project(stored, decisionTask.Result);
    }

    private async Task NotifyAsync(IReadOnlyList<ContactChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
            return;
        foreach (var observer in observers)
            await observer.OnChangedAsync(changes, cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset Now => time.GetUtcNow();

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static ContactChangeDraft Change(ContactChangeKind kind, ContactId contact, DateTimeOffset at) => new(Guid.CreateVersion7(), kind, contact, at);

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    private static void RequireId(ContactId id)
    {
        if (id == default)
            throw new ArgumentException("A contact identifier is required.", nameof(id));
    }
}

/// <summary>Outcome of a merge attempted by the linking service.</summary>
internal union ContactMergeAttempt(ContactMergedInto, ContactRefused);
