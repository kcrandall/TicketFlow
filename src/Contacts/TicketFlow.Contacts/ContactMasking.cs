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
        var definitions = (await definitionStore.ListAsync(scope, cancellationToken).ConfigureAwait(false))
            .ToDictionary(stored => stored.Definition.Key, stored => stored.Definition, StringComparer.Ordinal);
        var policies = (await policyStore.ListAsync(scope, cancellationToken).ConfigureAwait(false))
            .Where(stored => stored.Policy.AppliesTo.Contains(permission))
            .Select(stored => stored.Policy.Field)
            .ToHashSet();
        return new(definitions, policies);
    }

    public ContactView Project(StoredContact stored, AccessDecision decision)
    {
        var contact = stored.Contact;
        var masked = new Dictionary<FieldPath, bool>();
        var maskAll = false;
        switch (decision)
        {
            case AccessAllowed:
                break;
            case AccessMasked fields:
                foreach (var field in fields.Fields)
                    masked[field.Field] = field.Revealable;
                break;
            case AccessDenied:
                maskAll = true;
                break;
        }

        var identities = contact.Identities.Select(identity =>
        {
            var field = identity.Address.Field;
            if (maskAll)
                masked.TryAdd(field, false);
            var isMasked = masked.TryGetValue(field, out var revealable);
            return new ContactIdentityView(identity.Address.Kind, identity.Address.Namespace, isMasked ? null : identity.Address.Value,
                identity.Verification, isMasked, isMasked && revealable);
        }).ToList();

        var attributes = new List<ContactAttributeView>();
        foreach (var key in contact.AttributeKeys)
        {
            if (!definitions.TryGetValue(key, out var definition))
                continue;
            var field = definition.Field;
            if (maskAll || (definition.Sensitive && !policyFields.Contains(field)))
                masked.TryAdd(field, false);
            var isMasked = masked.TryGetValue(field, out var revealable);
            attributes.Add(new(key, definition.Type, definition.Sensitive, isMasked ? (ContactAttributeValue?)null : contact.Attributes[key], isMasked, isMasked && revealable));
        }

        return new ContactView(contact.Id, stored.Revision, contact.Profile, identities, attributes, contact.Labels, contact.Blocked,
            contact.CreatedAt, contact.UpdatedAt, [.. masked.OrderBy(pair => pair.Key).Select(pair => new MaskedField(pair.Key, pair.Value))]);
    }
}

/// <summary>Caches point decisions for one principal and permission by label set within a single call.</summary>
internal sealed class ContactDecisionCache(IAccessEvaluator evaluator, PrincipalRef actor, Permission permission)
{
    private readonly Dictionary<string, AccessDecision> decisions = new(StringComparer.Ordinal);

    public async Task<AccessDecision> DecideAsync(StoredContact stored, CancellationToken cancellationToken)
    {
        var key = string.Join('\u001F', stored.Contact.Labels.Select(label => label.Value));
        if (decisions.TryGetValue(key, out var cached))
            return cached;
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, permission, ContactAccess.Resource(stored)), cancellationToken).ConfigureAwait(false);
        decisions[key] = decision;
        return decision;
    }
}
