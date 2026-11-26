using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// Default <see cref="IContactService"/>. Revision fences and existence are checked before authorization decisions on stored records
/// (like the access administrator); every mutation authorizes against both the current and the resulting contact resource. Never
/// logs names, addresses or attribute values.
/// </summary>
public sealed partial class ContactService : IContactService
{
    private readonly IContactStore store;
    private readonly IContactAttributeDefinitionStore definitions;
    private readonly IContactAuditSink audit;
    private readonly IAccessEvaluator evaluator;
    private readonly IAccessQueryPlanner planner;
    private readonly IAccessFieldPolicyStore policies;
    private readonly IPermissionCatalog catalog;
    private readonly IReadOnlyList<IContactChangeObserver> observers;
    private readonly ContactsOptions options;
    private readonly TimeProvider time;

    public ContactService(
        IContactStore store,
        IContactAttributeDefinitionStore definitions,
        IContactAuditSink audit,
        IAccessEvaluator evaluator,
        IAccessQueryPlanner planner,
        IAccessFieldPolicyStore policies,
        IPermissionCatalog catalog,
        IEnumerable<IContactChangeObserver> observers,
        IOptions<ContactsOptions> options,
        TimeProvider time)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        this.audit = audit ?? throw new ArgumentNullException(nameof(audit));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.policies = policies ?? throw new ArgumentNullException(nameof(policies));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(observers);
        this.observers = [.. observers];
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<TicketResult<ContactView, ContactRefused>> CreateAsync(PrincipalRef actor, ContactDraft draft, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(draft);
        var definitionMap = await DefinitionsAsync(scope, cancellationToken).ConfigureAwait(false);
        var emptyAttributes = new Dictionary<string, ContactAttributeValue>(StringComparer.Ordinal);
        if (ValidateAttributes(draft.Attributes ?? emptyAttributes, definitionMap) is { } invalid)
            return invalid;
        var now = Now;
        Contact contact;
        try
        {
            contact = new Contact(draft.Id ?? options.IdFactory(), draft.Profile ?? ContactProfile.Empty, draft.Identities ?? [],
                draft.Attributes ?? emptyAttributes, draft.Labels ?? [], draft.Blocked, now, now);
        }
        catch (ArgumentException)
        {
            return ContactRefused.Invalid("The contact draft is invalid or exceeds contact limits.");
        }
        if (!(await AuthorizeAsync(actor, ContactPermissions.Create, ContactAccess.Resource(scope, contact), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        var batch = new ContactWriteBatch(puts: [new ContactPut(contact, null)], changes: [Change(ContactChangeKind.Created, contact.Id, now)]);
        return await WriteAsync(actor, scope, batch, applied => ViewAsync(actor, applied.Contacts[0], definitionMap, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TicketResult<ContactView, ContactRefused>> GetAsync(PrincipalRef actor, ContactId id, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ContactRefused.NotFound;
        var decision = await AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
        if (!decision.IsPermitted)
            return ContactRefused.Forbidden;
        if (stored.Contact.MergedInto is { } mergedInto)
            return new ContactRefused(ContactRefusal.Merged, [], stored.Revision, mergedInto);
        var masking = await MaskingAsync(scope, ContactPermissions.Read, cancellationToken).ConfigureAwait(false);
        return new TicketValue<ContactView>(masking.Project(stored, decision));
    }

    /// <summary>One decision context and one masking load for the whole batch (list rendering).</summary>
    public async Task<IReadOnlyList<ContactView>> GetManyAsync(PrincipalRef actor, IEnumerable<ContactId> ids, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = new List<ContactId>();
        var seen = new HashSet<ContactId>();
        foreach (var id in ids)
        {
            if (!seen.Add(id))
                continue;
            if (distinct.Count == IContactService.MaximumBatch)
                throw new ArgumentException($"At most {IContactService.MaximumBatch} contacts per batch.", nameof(ids));
            RequireId(id);
            distinct.Add(id);
        }
        if (distinct.Count == 0)
            return [];
        var decisionsTask = evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Read], cancellationToken);
        var loadedTask = store.GetManyAsync(scope, distinct, cancellationToken);
        await Task.WhenAll(decisionsTask, loadedTask).ConfigureAwait(false);
        var decisions = decisionsTask.Result;
        var loaded = loadedTask.Result;
        var views = new List<ContactView>(loaded.Count);
        ContactMasking? masking = null;
        foreach (var id in distinct)
        {
            if (!loaded.TryGetValue(id, out var stored) || stored.Contact.MergedInto is not null)
                continue;
            var decision = await decisions.DecideAsync(ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
            if (!decision.IsPermitted)
                continue;
            masking ??= await MaskingAsync(scope, ContactPermissions.Read, cancellationToken).ConfigureAwait(false);
            views.Add(masking.Project(stored, decision));
        }
        return views;
    }

    public async Task<TicketResult<ContactView, ContactRefused>> UpdateAsync(PrincipalRef actor, ContactId id, long expectedRevision, IEnumerable<ContactEdit> edits, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        ArgumentNullException.ThrowIfNull(edits);
        IReadOnlyList<ContactEdit> editList = [.. edits];
        if (editList.Count is 0 or > 256 || editList.Any(edit => edit is null))
            return ContactRefused.Invalid("An update applies 1-256 edits.");
        var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ContactRefused.NotFound;
        if (stored.Revision != expectedRevision)
            return new ContactRefused(ContactRefusal.Conflict, [], stored.Revision);

        HashSet<FieldPath>? identityFields = null;
        foreach (var edit in editList)
        {
            if (IdentityField(edit) is { } field)
                (identityFields ??= []).Add(field);
        }
        var decisions = await evaluator.LoadDecisionContextAsync(actor, scope,
            identityFields is not null ? [ContactPermissions.Update, ContactPermissions.Read] : [ContactPermissions.Update], cancellationToken).ConfigureAwait(false);
        var storedResource = ContactAccess.Resource(stored);
        if (!(await decisions.DecideAsync(ContactPermissions.Update, storedResource, cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        if (stored.Contact.MergedInto is { } mergedInto)
            return new ContactRefused(ContactRefusal.Merged, [], stored.Revision, mergedInto);

        // Identity edits on a field the actor cannot see unmasked are refused up front with one uniform answer, so neither the
        // presence of an address on this contact nor its verified ownership elsewhere can be probed through updates.
        if (identityFields is not null)
        {
            var read = await decisions.DecideAsync(ContactPermissions.Read, storedResource, cancellationToken).ConfigureAwait(false);
            if (!Unmasked(read, identityFields))
                return ContactRefused.Forbidden;
        }

        var definitionMap = await DefinitionsAsync(scope, cancellationToken).ConfigureAwait(false);
        var now = Now;
        var (updated, refusal) = ApplyEdits(stored.Contact, editList, definitionMap, now);
        if (updated is null)
            return refusal!;
        if (!(await decisions.DecideAsync(ContactPermissions.Update, ContactAccess.Resource(scope, updated), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        var batch = new ContactWriteBatch(puts: [new ContactPut(updated, expectedRevision)], changes: [Change(ContactChangeKind.Updated, id, now)]);
        return await WriteAsync(actor, scope, batch, result => ViewAsync(actor, result.Contacts[0], definitionMap, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TicketResult<ContactId, ContactRefused>> DeleteAsync(PrincipalRef actor, ContactId id, long expectedRevision, string? justification = null, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        if (justification is not null && (justification.Length > ContactAuditEvent.MaximumJustificationLength || string.IsNullOrWhiteSpace(justification)))
            return ContactRefused.Invalid("The justification is invalid.");
        var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ContactRefused.NotFound;
        if (stored.Revision != expectedRevision)
            return new ContactRefused(ContactRefusal.Conflict, [], stored.Revision);
        var decisions = await evaluator.LoadDecisionContextAsync(actor, scope, [ContactPermissions.Delete], cancellationToken).ConfigureAwait(false);
        if (!(await decisions.DecideAsync(ContactPermissions.Delete, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        // Every tombstone the delete cascades to is authorized too; the store applies the delete only for exactly this set.
        var cascade = new List<ContactId>();
        var seen = new HashSet<ContactId> { id };
        var pending = new Queue<ContactId>([id]);
        while (pending.TryDequeue(out var parent))
        {
            var children = await store.ListMergedIntoAsync(scope, parent, ContactDelete.MaximumCascade, cancellationToken).ConfigureAwait(false);
            if (children.Count == 0)
                continue;
            var batchIds = new List<ContactId>(children.Count);
            foreach (var child in children)
            {
                if (!seen.Add(child))
                    continue;
                if (cascade.Count + batchIds.Count == ContactDelete.MaximumCascade)
                    return new ContactRefused(ContactRefusal.Rejected, ["The contact's merge history is too large to delete in one operation."]);
                batchIds.Add(child);
            }
            if (batchIds.Count == 0)
                continue;
            var tombstones = await store.GetManyAsync(scope, batchIds, cancellationToken).ConfigureAwait(false);
            foreach (var child in batchIds)
            {
                if (!tombstones.TryGetValue(child, out var tombstone))
                    continue;
                if (!(await decisions.DecideAsync(ContactPermissions.Delete, ContactAccess.Resource(tombstone), cancellationToken).ConfigureAwait(false)).IsPermitted)
                    return ContactRefused.Forbidden;
                cascade.Add(child);
                pending.Enqueue(child);
            }
        }

        var now = Now;
        ContactAuditEvent auditEvent;
        try
        {
            auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Deleted, AccessActor.From(actor), id, count: cascade.Count + 1,
                justification: justification);
        }
        catch (ArgumentException)
        {
            return ContactRefused.Invalid("The justification is invalid.");
        }
        var batch = new ContactWriteBatch(
            deletes: [new ContactDelete(id, expectedRevision, cascade)],
            changes: [Change(ContactChangeKind.Deleted, id, now), .. cascade.Select(child => Change(ContactChangeKind.Deleted, child, now, related: id))],
            audit: auditEvent);
        return await WriteAsync(actor, scope, batch, _ => Task.FromResult(id), cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContactRevealResult> RevealAsync(PrincipalRef actor, ContactId id, FieldPath field, string? justification = null, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        RequireId(id);
        if (!field.IsInitialized)
            throw new ArgumentException("A field path is required.", nameof(field));
        var stored = await store.GetAsync(scope, id, cancellationToken).ConfigureAwait(false);
        if (stored is null)
            return ContactRefused.NotFound;
        if (stored.Contact.MergedInto is { } mergedInto)
        {
            var read = await AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
            return read.IsPermitted ? new ContactRefused(ContactRefusal.Merged, [], stored.Revision, mergedInto) : ContactRefused.Forbidden;
        }

        string? attributeKey = null;
        if (field.Value.StartsWith(ContactFieldPaths.AttributePrefix, StringComparison.Ordinal))
        {
            attributeKey = field.Value[ContactFieldPaths.AttributePrefix.Length..];
            if (await definitions.GetAsync(scope, attributeKey, cancellationToken).ConfigureAwait(false) is null)
                return ContactRefused.Invalid("The field is not a defined contact attribute.");
        }
        else if (field != ContactFieldPaths.Email && field != ContactFieldPaths.Phone && field != ContactFieldPaths.Social && field != ContactFieldPaths.ExternalId)
            return ContactRefused.Invalid("The field is not a maskable contact field.");

        RevealRequest request;
        try
        {
            request = new RevealRequest(actor, ContactAccess.Resource(stored), ContactPermissions.Read, field, justification);
        }
        catch (ArgumentException)
        {
            return ContactRefused.Invalid("The justification is invalid.");
        }
        return await evaluator.RevealAsync(request, cancellationToken).ConfigureAwait(false) switch
        {
            RevealGranted granted => new ContactFieldRevealed(field,
                [.. stored.Contact.Identities.Where(identity => attributeKey is null && identity.Address.Field == field).Select(identity => identity.Address)],
                attributeKey is not null && stored.Contact.Attributes.TryGetValue(attributeKey, out var value) ? value : (ContactAttributeValue?)null,
                granted.AuditEventId),
            RevealRefused refused => new ContactRevealRefused(field, refused.Reason, refused.AuditEventId),
        };
    }

    private DateTimeOffset Now => time.GetUtcNow();

    private static TicketScope RequireActor(PrincipalRef actor)
    {
        if (actor is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        return actor.Scope;
    }

    private static void RequireId(ContactId id)
    {
        if (id == default)
            throw new ArgumentException("A contact identifier is required.", nameof(id));
    }

    private Task<AccessDecision> AuthorizeAsync(PrincipalRef actor, Permission permission, AccessResource resource, CancellationToken cancellationToken) =>
        evaluator.AuthorizeAsync(new AccessRequest(actor, permission, resource), cancellationToken);

    private Task<ContactMasking> MaskingAsync(TicketScope scope, Permission permission, CancellationToken cancellationToken) =>
        ContactMasking.LoadAsync(scope, permission, definitions, policies, cancellationToken);

    private async Task<IReadOnlyDictionary<string, ContactAttributeDefinition>> DefinitionsAsync(TicketScope scope, CancellationToken cancellationToken)
    {
        var stored = await definitions.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, ContactAttributeDefinition>(stored.Count, StringComparer.Ordinal);
        foreach (var item in stored)
            map[item.Definition.Key] = item.Definition;
        return map;
    }

    /// <summary>A view masked per the actor's <c>contact:read</c> decision (everything masked when read is denied).</summary>
    private async Task<ContactView> ViewAsync(PrincipalRef actor, StoredContact stored, CancellationToken cancellationToken)
    {
        var decisionTask = AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken);
        var maskingTask = MaskingAsync(stored.Scope, ContactPermissions.Read, cancellationToken);
        await Task.WhenAll(decisionTask, maskingTask).ConfigureAwait(false);
        return maskingTask.Result.Project(stored, decisionTask.Result);
    }

    private async Task<ContactView> ViewAsync(PrincipalRef actor, StoredContact stored,
        IReadOnlyDictionary<string, ContactAttributeDefinition> definitionMap, CancellationToken cancellationToken)
    {
        var decisionTask = AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken);
        var maskingTask = ContactMasking.LoadAsync(stored.Scope, ContactPermissions.Read, definitionMap, policies, cancellationToken);
        await Task.WhenAll(decisionTask, maskingTask).ConfigureAwait(false);
        return maskingTask.Result.Project(stored, decisionTask.Result);
    }

    private ContactDecisionCache Decisions(PrincipalRef actor, Permission permission) =>
        new(evaluator, actor, permission, catalog.Find(permission)?.IsSensitive != false);

    private static ContactChangeDraft Change(ContactChangeKind kind, ContactId contact, DateTimeOffset at, ContactId? related = null, Guid? merge = null) =>
        new(Guid.CreateVersion7(), kind, contact, at, related, merge);

    private static ContactRefused? ValidateAttributes(IReadOnlyDictionary<string, ContactAttributeValue> attributes, IReadOnlyDictionary<string, ContactAttributeDefinition> definitionMap)
    {
        foreach (var (key, value) in attributes)
            if (ValidateAttribute(key, value, definitionMap) is { } invalid)
                return invalid;
        return null;
    }

    private static ContactRefused? ValidateAttribute(string key, ContactAttributeValue value, IReadOnlyDictionary<string, ContactAttributeDefinition> definitionMap)
    {
        if (!definitionMap.TryGetValue(key, out var definition))
            return ContactRefused.Invalid($"Attribute '{SafeKey(key)}' is not defined.");
        if (value.Value is null || !definition.Accepts(value))
            return ContactRefused.Invalid($"Attribute '{definition.Key}' requires a {definition.Type} value.");
        return null;
    }

    private static string SafeKey(string key) => ContactAttributeDefinition.IsValidKey(key) ? key : "<invalid>";

    private static (Contact? Contact, ContactRefused? Refused) ApplyEdits(Contact contact, IReadOnlyList<ContactEdit> edits, IReadOnlyDictionary<string, ContactAttributeDefinition> definitionMap, DateTimeOffset now)
    {
        var profile = contact.Profile;
        Dictionary<ContactIdentityAddress, ContactIdentity>? identities = null;
        Dictionary<string, ContactAttributeValue>? attributes = null;
        List<LabelId>? labels = null;
        var blocked = contact.Blocked;
        foreach (var edit in edits)
        {
            switch (edit)
            {
                case SetContactProfile set:
                    if (set.Profile is null)
                        return Refuse(ContactRefused.Invalid("A profile is required."));
                    profile = set.Profile;
                    break;
                case SetContactAttribute set:
                    if (ValidateAttribute(set.Key ?? string.Empty, set.Value, definitionMap) is { } invalid)
                        return Refuse(invalid);
                    attributes ??= new Dictionary<string, ContactAttributeValue>(contact.Attributes, StringComparer.Ordinal);
                    attributes[set.Key!] = set.Value;
                    break;
                case RemoveContactAttribute remove:
                    attributes ??= new Dictionary<string, ContactAttributeValue>(contact.Attributes, StringComparer.Ordinal);
                    if (remove.Key is null || !attributes.Remove(remove.Key))
                        return Refuse(ContactRefused.Invalid($"Attribute '{SafeKey(remove.Key ?? string.Empty)}' is not set."));
                    break;
                case AddContactLabel add:
                    if (add.Label == default)
                        return Refuse(ContactRefused.Invalid("Labels must be initialized."));
                    labels ??= [.. contact.Labels];
                    if (!labels.Contains(add.Label))
                        labels.Add(add.Label);
                    break;
                case RemoveContactLabel remove:
                    labels ??= [.. contact.Labels];
                    labels.Remove(remove.Label);
                    break;
                case SetContactBlocked set:
                    blocked = set.Blocked;
                    break;
                case AddContactIdentity add:
                    if (add.Identity is null)
                        return Refuse(ContactRefused.Invalid("An identity is required."));
                    identities ??= contact.Identities.ToDictionary(identity => identity.Address);
                    identities[add.Identity.Address] = add.Identity;
                    break;
                case RemoveContactIdentity remove:
                    identities ??= contact.Identities.ToDictionary(identity => identity.Address);
                    if (remove.Address is null || !identities.Remove(remove.Address))
                        return Refuse(ContactRefused.Invalid("The identity is not on the contact."));
                    break;
            }
        }
        try
        {
            return (contact.With(profile, identities?.Values, attributes, labels, blocked, now < contact.UpdatedAt ? contact.UpdatedAt : now), null);
        }
        catch (ArgumentException)
        {
            return Refuse(new ContactRefused(ContactRefusal.Rejected, ["The update exceeds contact limits."]));
        }
    }

    private static (Contact? Contact, ContactRefused? Refused) Refuse(ContactRefused refused) => (null, refused);

    /// <summary>Writes a batch, notifies observers after commit and maps store outcomes to refusals.</summary>
    private async Task<TicketResult<T, ContactRefused>> WriteAsync<T>(PrincipalRef actor, TicketScope scope, ContactWriteBatch batch, Func<ContactWriteApplied, Task<T>> onApplied, CancellationToken cancellationToken)
    {
        var result = await store.WriteAsync(scope, batch, cancellationToken).ConfigureAwait(false);
        if (result.Value is ContactWriteApplied applied)
        {
            await NotifyAsync(applied.Changes, cancellationToken).ConfigureAwait(false);
            return new TicketValue<T>(await onApplied(applied).ConfigureAwait(false));
        }
        return result.Value is ContactIdentityTaken taken ? await TakenAsync(actor, taken, cancellationToken).ConfigureAwait(false) : Refusal(result);
    }

    /// <summary>
    /// Reports <see cref="ContactRefusal.IdentityTaken"/> only when the actor may read the holder with that identity field unmasked;
    /// otherwise the uniform <see cref="ContactRefused.Forbidden"/>, so ownership of masked or unreadable data is not disclosed.
    /// </summary>
    private async Task<ContactRefused> TakenAsync(PrincipalRef actor, ContactIdentityTaken taken, CancellationToken cancellationToken)
    {
        var owner = await store.GetAsync(actor.Scope, taken.Owner, cancellationToken).ConfigureAwait(false);
        if (owner is not null)
        {
            var read = await AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(owner), cancellationToken).ConfigureAwait(false);
            if (Unmasked(read, [taken.Address.Field]))
                return Refusal(taken);
        }
        return ContactRefused.Forbidden;
    }

    /// <summary>True when the decision permits reading and masks none of <paramref name="fields"/>.</summary>
    private static bool Unmasked(AccessDecision decision, IReadOnlyCollection<FieldPath> fields) => decision switch
    {
        AccessAllowed => true,
        AccessMasked masked => masked.Fields.All(field => !fields.Contains(field.Field)),
        AccessDenied => false,
    };

    private static FieldPath? IdentityField(ContactEdit edit) => edit switch
    {
        AddContactIdentity add => add.Identity?.Address.Field,
        RemoveContactIdentity remove => remove.Address?.Field,
        SetContactProfile => null,
        SetContactAttribute => null,
        RemoveContactAttribute => null,
        AddContactLabel => null,
        RemoveContactLabel => null,
        SetContactBlocked => null,
    };

    private static ContactRefused Refusal(ContactWriteResult result) => result switch
    {
        ContactWriteApplied => throw new InvalidOperationException("An applied write is not a refusal."),
        ContactWriteNotFound => ContactRefused.NotFound,
        ContactWriteConflict conflict => new ContactRefused(ContactRefusal.Conflict, [], conflict.CurrentRevision),
        ContactIdentityTaken taken => new ContactRefused(ContactRefusal.IdentityTaken, [$"A verified {taken.Address.Kind} identity is held by another contact."]),
        ContactWriteRejected rejected => new ContactRefused(ContactRefusal.Rejected, [$"The store rejected the write: {rejected.Reason}."]),
    };

    private async Task NotifyAsync(IReadOnlyList<ContactChange> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
            return;
        foreach (var observer in observers)
            await observer.OnChangedAsync(changes, cancellationToken).ConfigureAwait(false);
    }
}
