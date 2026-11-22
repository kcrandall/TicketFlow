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
        ArgumentNullException.ThrowIfNull(observers);
        this.observers = [.. observers];
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<ContactResult<ContactView>> CreateAsync(PrincipalRef actor, ContactDraft draft, CancellationToken cancellationToken = default)
    {
        var scope = RequireActor(actor);
        ArgumentNullException.ThrowIfNull(draft);
        var definitionMap = await DefinitionsAsync(scope, cancellationToken).ConfigureAwait(false);
        if (ValidateAttributes(draft.Attributes ?? new Dictionary<string, ContactAttributeValue>(), definitionMap) is { } invalid)
            return invalid;
        var now = Now;
        Contact contact;
        try
        {
            contact = new Contact(draft.Id ?? options.IdFactory(), draft.Profile ?? ContactProfile.Empty, draft.Identities ?? [],
                draft.Attributes ?? new Dictionary<string, ContactAttributeValue>(), draft.Labels ?? [], draft.Blocked, now, now);
        }
        catch (ArgumentException)
        {
            return ContactRefused.Invalid("The contact draft is invalid or exceeds contact limits.");
        }
        if (!(await AuthorizeAsync(actor, ContactPermissions.Create, ContactAccess.Resource(scope, contact), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        var batch = new ContactWriteBatch(puts: [new ContactPut(contact, null)], changes: [Change(ContactChangeKind.Created, contact.Id, now)]);
        return await WriteAsync(scope, batch, async applied => await ViewAsync(actor, applied.Contacts[0], cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContactResult<ContactView>> GetAsync(PrincipalRef actor, ContactId id, CancellationToken cancellationToken = default)
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
        return new ContactApplied<ContactView>(masking.Project(stored, decision));
    }

    public async Task<ContactResult<ContactView>> UpdateAsync(PrincipalRef actor, ContactId id, long expectedRevision, IEnumerable<ContactEdit> edits, CancellationToken cancellationToken = default)
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
        if (!(await AuthorizeAsync(actor, ContactPermissions.Update, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;
        if (stored.Contact.MergedInto is { } mergedInto)
            return new ContactRefused(ContactRefusal.Merged, [], stored.Revision, mergedInto);

        var definitionMap = await DefinitionsAsync(scope, cancellationToken).ConfigureAwait(false);
        var now = Now;
        var (updated, refusal) = ApplyEdits(stored.Contact, editList, definitionMap, now);
        if (updated is null)
            return refusal!;
        if (!(await AuthorizeAsync(actor, ContactPermissions.Update, ContactAccess.Resource(scope, updated), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        var batch = new ContactWriteBatch(puts: [new ContactPut(updated, expectedRevision)], changes: [Change(ContactChangeKind.Updated, id, now)]);
        return await WriteAsync(scope, batch, async result => await ViewAsync(actor, result.Contacts[0], cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContactResult<ContactId>> DeleteAsync(PrincipalRef actor, ContactId id, long expectedRevision, string? justification = null, CancellationToken cancellationToken = default)
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
        if (!(await AuthorizeAsync(actor, ContactPermissions.Delete, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false)).IsPermitted)
            return ContactRefused.Forbidden;

        var now = Now;
        ContactAuditEvent auditEvent;
        try
        {
            auditEvent = new ContactAuditEvent(Guid.CreateVersion7(), now, scope, ContactAuditKind.Deleted, AccessActor.From(actor), id, justification: justification);
        }
        catch (ArgumentException)
        {
            return ContactRefused.Invalid("The justification is invalid.");
        }
        var batch = new ContactWriteBatch(deletes: [new ContactDelete(id, expectedRevision)], changes: [Change(ContactChangeKind.Deleted, id, now)], audit: auditEvent);
        return await WriteAsync(scope, batch, _ => Task.FromResult(id), cancellationToken).ConfigureAwait(false);
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
        if (actor.Value is null)
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

    private async Task<IReadOnlyDictionary<string, ContactAttributeDefinition>> DefinitionsAsync(TicketScope scope, CancellationToken cancellationToken) =>
        (await definitions.ListAsync(scope, cancellationToken).ConfigureAwait(false))
            .ToDictionary(stored => stored.Definition.Key, stored => stored.Definition, StringComparer.Ordinal);

    /// <summary>A view masked per the actor's <c>contact:read</c> decision (everything masked when read is denied).</summary>
    private async Task<ContactView> ViewAsync(PrincipalRef actor, StoredContact stored, CancellationToken cancellationToken)
    {
        var decision = await AuthorizeAsync(actor, ContactPermissions.Read, ContactAccess.Resource(stored), cancellationToken).ConfigureAwait(false);
        var masking = await MaskingAsync(stored.Scope, ContactPermissions.Read, cancellationToken).ConfigureAwait(false);
        return masking.Project(stored, decision);
    }

    private static ContactChangeDraft Change(ContactChangeKind kind, ContactId contact, DateTimeOffset at, ContactId? related = null, Guid? merge = null) =>
        new(Guid.CreateVersion7(), kind, contact, at, related, merge);

    private static ContactRefused? ValidateAttributes(IReadOnlyDictionary<string, ContactAttributeValue> attributes, IReadOnlyDictionary<string, ContactAttributeDefinition> definitionMap)
    {
        foreach (var (key, value) in attributes)
        {
            if (!definitionMap.TryGetValue(key, out var definition))
                return ContactRefused.Invalid($"Attribute '{SafeKey(key)}' is not defined.");
            if (value.Value is null || !definition.Accepts(value))
                return ContactRefused.Invalid($"Attribute '{definition.Key}' requires a {definition.Type} value.");
        }
        return null;
    }

    private static string SafeKey(string key) => ContactAttributeDefinition.IsValidKey(key) ? key : "<invalid>";

    private static (Contact? Contact, ContactRefused? Refused) ApplyEdits(Contact contact, IReadOnlyList<ContactEdit> edits, IReadOnlyDictionary<string, ContactAttributeDefinition> definitionMap, DateTimeOffset now)
    {
        var profile = contact.Profile;
        var identities = contact.Identities.ToDictionary(identity => identity.Address);
        var attributes = new Dictionary<string, ContactAttributeValue>(contact.Attributes, StringComparer.Ordinal);
        var labels = contact.Labels.ToList();
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
                    if (ValidateAttributes(new Dictionary<string, ContactAttributeValue> { [set.Key ?? string.Empty] = set.Value }, definitionMap) is { } invalid)
                        return Refuse(invalid);
                    attributes[set.Key!] = set.Value;
                    break;
                case RemoveContactAttribute remove:
                    if (remove.Key is null || !attributes.Remove(remove.Key))
                        return Refuse(ContactRefused.Invalid($"Attribute '{SafeKey(remove.Key ?? string.Empty)}' is not set."));
                    break;
                case AddContactLabel add:
                    if (add.Label == default)
                        return Refuse(ContactRefused.Invalid("Labels must be initialized."));
                    if (!labels.Contains(add.Label))
                        labels.Add(add.Label);
                    break;
                case RemoveContactLabel remove:
                    labels.Remove(remove.Label);
                    break;
                case SetContactBlocked set:
                    blocked = set.Blocked;
                    break;
                case AddContactIdentity add:
                    if (add.Identity is null)
                        return Refuse(ContactRefused.Invalid("An identity is required."));
                    identities[add.Identity.Address] = add.Identity;
                    break;
                case RemoveContactIdentity remove:
                    if (remove.Address is null || !identities.Remove(remove.Address))
                        return Refuse(ContactRefused.Invalid("The identity is not on the contact."));
                    break;
            }
        }
        try
        {
            return (contact.With(profile, identities.Values, attributes, labels, blocked, now < contact.UpdatedAt ? contact.UpdatedAt : now), null);
        }
        catch (ArgumentException)
        {
            return Refuse(new ContactRefused(ContactRefusal.Rejected, ["The update exceeds contact limits."]));
        }
    }

    private static (Contact? Contact, ContactRefused? Refused) Refuse(ContactRefused refused) => (null, refused);

    /// <summary>Writes a batch, notifies observers after commit and maps store outcomes to refusals.</summary>
    private async Task<ContactResult<T>> WriteAsync<T>(TicketScope scope, ContactWriteBatch batch, Func<ContactWriteApplied, Task<T>> onApplied, CancellationToken cancellationToken)
    {
        var result = await store.WriteAsync(scope, batch, cancellationToken).ConfigureAwait(false);
        if (result.Value is ContactWriteApplied applied)
        {
            await NotifyAsync(applied.Changes, cancellationToken).ConfigureAwait(false);
            return new ContactApplied<T>(await onApplied(applied).ConfigureAwait(false));
        }
        return Refusal(result);
    }

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
