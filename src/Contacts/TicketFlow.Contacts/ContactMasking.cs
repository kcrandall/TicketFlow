using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// Builds masked <see cref="ContactView"/> projections for one permission in one scope. Fields listed by an
/// <see cref="AccessMasked"/> decision are removed; a sensitive attribute with no field policy applying to the permission is masked
/// and not revealable (fail closed). A denied decision masks every identity and attribute.
/// </summary>
internal sealed class ContactMasking
{
    private readonly IReadOnlyDictionary<string, ContactAttributeDefinition> definitions;
    private readonly HashSet<FieldPath> policyFields;

    private ContactMasking(IReadOnlyDictionary<string, ContactAttributeDefinition> definitions, HashSet<FieldPath> policyFields)
    {
        this.definitions = definitions;
        this.policyFields = policyFields;
    }

    public IReadOnlyDictionary<string, ContactAttributeDefinition> Definitions => definitions;

    public static async Task<ContactMasking> LoadAsync(TicketScope scope, Permission permission, IContactAttributeDefinitionStore definitionStore,
        IAccessFieldPolicyStore policyStore, CancellationToken cancellationToken)
    {
        var definitionsTask = definitionStore.ListAsync(scope, cancellationToken);
        var policiesTask = policyStore.ListAsync(scope, cancellationToken);
        await Task.WhenAll(definitionsTask, policiesTask).ConfigureAwait(false);
        var storedDefs = definitionsTask.Result;
        var definitions = new Dictionary<string, ContactAttributeDefinition>(storedDefs.Count, StringComparer.Ordinal);
        foreach (var stored in storedDefs)
            definitions[stored.Definition.Key] = stored.Definition;
        return new(definitions, BuildPolicyFields(policiesTask.Result, permission));
    }

    public static async Task<ContactMasking> LoadAsync(TicketScope scope, Permission permission,
        IReadOnlyDictionary<string, ContactAttributeDefinition> definitions, IAccessFieldPolicyStore policyStore, CancellationToken cancellationToken)
    {
        var storedPolicies = await policyStore.ListAsync(scope, cancellationToken).ConfigureAwait(false);
        return new(definitions, BuildPolicyFields(storedPolicies, permission));
    }

    private static HashSet<FieldPath> BuildPolicyFields(IReadOnlyList<StoredAccessFieldPolicy> storedPolicies, Permission permission)
    {
        var fields = new HashSet<FieldPath>();
        foreach (var stored in storedPolicies)
            if (stored.Policy.AppliesTo.Contains(permission))
                fields.Add(stored.Policy.Field);
        return fields;
    }

    public ContactView Project(StoredContact stored, AccessDecision decision)
    {
        var contact = stored.Contact;
        Dictionary<FieldPath, bool>? masked = null;
        var maskAll = false;
        switch (decision)
        {
            case AccessAllowed:
                break;
            case AccessMasked fields:
                if (fields.Fields.Count > 0)
                {
                    masked = new Dictionary<FieldPath, bool>(fields.Fields.Count);
                    foreach (var field in fields.Fields)
                        masked[field.Field] = field.Revealable;
                }
                break;
            case AccessDenied:
                maskAll = true;
                break;
        }

        IReadOnlyList<ContactIdentityView> identities;
        if (contact.Identities.Count == 0)
            identities = [];
        else
        {
            var identityViews = new ContactIdentityView[contact.Identities.Count];
            for (var i = 0; i < contact.Identities.Count; i++)
            {
                var identity = contact.Identities[i];
                var field = identity.Address.Field;
                if (maskAll)
                {
                    masked ??= [];
                    masked.TryAdd(field, false);
                }
                var revealable = false;
                var isMasked = masked is not null && masked.TryGetValue(field, out revealable);
                identityViews[i] = new ContactIdentityView(identity.Address.Kind, identity.Address.Namespace, isMasked ? null : identity.Address.Value,
                    identity.Verification, isMasked, isMasked && revealable);
            }
            identities = identityViews;
        }

        IReadOnlyList<ContactAttributeView> attributes;
        if (contact.AttributeKeys.Count == 0 || definitions.Count == 0)
            attributes = [];
        else
        {
            var attrList = new List<ContactAttributeView>(contact.AttributeKeys.Count);
            foreach (var key in contact.AttributeKeys)
            {
                if (!definitions.TryGetValue(key, out var definition))
                    continue;
                var field = definition.Field;
                if (maskAll || (definition.Sensitive && !policyFields.Contains(field)))
                {
                    masked ??= [];
                    masked.TryAdd(field, false);
                }
                var revealable = false;
                var isMasked = masked is not null && masked.TryGetValue(field, out revealable);
                attrList.Add(new(key, definition.Type, definition.Sensitive, isMasked ? (ContactAttributeValue?)null : contact.Attributes[key], isMasked, isMasked && revealable));
            }
            attributes = attrList;
        }

        IReadOnlyList<MaskedField> maskedFields;
        if (masked is null || masked.Count == 0)
            maskedFields = [];
        else
        {
            var arr = new MaskedField[masked.Count];
            var idx = 0;
            foreach (var pair in masked)
                arr[idx++] = new MaskedField(pair.Key, pair.Value);
            if (arr.Length > 1)
                Array.Sort(arr, static (a, b) => a.Field.CompareTo(b.Field));
            maskedFields = arr;
        }

        return new ContactView(contact.Id, stored.Revision, contact.Profile, identities, attributes, contact.Labels, contact.Blocked,
            contact.CreatedAt, contact.UpdatedAt, maskedFields);
    }
}

/// <summary>
/// Caches point decisions for one principal and permission by label set within a single call. Decisions for sensitive permissions
/// (which Access audits per call) are never cached, and neither are decisions that used a break-glass grant, so caching never
/// suppresses an audit event.
/// </summary>
internal sealed class ContactDecisionCache(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission, bool sensitive)
{
    private Dictionary<string, AccessDecision>? decisions;

    public async Task<AccessDecision> DecideAsync(StoredContact stored, CancellationToken cancellationToken)
    {
        if (sensitive)
            return await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, ContactAccess.Resource(stored)), cancellationToken).ConfigureAwait(false);
        var key = BuildKey(stored.Contact.Labels);
        if (decisions is not null && decisions.TryGetValue(key, out var cached))
            return cached;
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, ContactAccess.Resource(stored)), cancellationToken).ConfigureAwait(false);
        if (!UsedBreakGlass(decision))
            (decisions ??= new Dictionary<string, AccessDecision>(StringComparer.Ordinal))[key] = decision;
        return decision;
    }

    private static string BuildKey(IReadOnlyList<LabelId> labels)
    {
        if (labels.Count == 0)
            return string.Empty;
        if (labels.Count == 1)
            return labels[0].Value;
        var arr = new string[labels.Count];
        for (var i = 0; i < labels.Count; i++)
            arr[i] = labels[i].Value;
        return string.Join('\u001F', arr);
    }

    private static bool UsedBreakGlass(AccessDecision decision) => decision switch
    {
        AccessAllowed allowed => allowed.UsedBreakGlass,
        AccessDenied => false,
        AccessMasked masked => masked.UsedBreakGlass,
    };
}
