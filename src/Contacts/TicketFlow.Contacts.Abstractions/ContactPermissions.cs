using TicketFlow.Access;

namespace TicketFlow.Contacts;

/// <summary>
/// Permissions used by the contacts feature. <c>contact:read|update|export|merge|reveal</c> are Access built-ins and are reused;
/// <c>contact:create</c>, <c>contact:delete</c> and <c>contact-attribute:manage</c> are registered by <c>AddContacts()</c>.
/// </summary>
public static class ContactPermissions
{
    public static Permission Read => AccessPermissions.ContactRead;
    public static Permission Update => AccessPermissions.ContactUpdate;
    public static Permission Export => AccessPermissions.ContactExport;
    public static Permission Merge => AccessPermissions.ContactMerge;
    public static Permission Reveal => AccessPermissions.ContactReveal;
    public static Permission Create { get; } = new("contact:create");
    public static Permission Delete { get; } = new("contact:delete");
    public static Permission ManageAttributes { get; } = new("contact-attribute:manage");

    /// <summary>The feature-owned definitions registered in the permission catalog (built-ins are already cataloged).</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Create, false, "Create contacts."),
        new(Delete, true, "Delete contacts and their merge history."),
        new(ManageAttributes, true, "Define contact attributes and their sensitivity."),
    ];
}

/// <summary>
/// Field paths masked through Access field policies. Identity values map by kind: email to <see cref="Email"/>, phone and WhatsApp
/// to <see cref="Phone"/>, social handles to <see cref="Social"/>, widget visitor and external user identifiers to
/// <see cref="ExternalId"/>. Attribute values use <c>contact.attribute.&lt;key&gt;</c>.
/// </summary>
public static class ContactFieldPaths
{
    public static FieldPath Email { get; } = new("contact.email");
    public static FieldPath Phone { get; } = new("contact.phone");
    public static FieldPath Social { get; } = new("contact.social");
    public static FieldPath ExternalId { get; } = new("contact.external-id");

    public const string AttributePrefix = "contact.attribute.";

    public static FieldPath ForIdentity(ContactIdentityKind kind) => kind switch
    {
        ContactIdentityKind.Email => Email,
        ContactIdentityKind.Phone or ContactIdentityKind.WhatsApp => Phone,
        ContactIdentityKind.SocialHandle => Social,
        ContactIdentityKind.WidgetVisitor or ContactIdentityKind.ExternalUser => ExternalId,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The masking path of attribute <paramref name="key"/>.</summary>
    public static FieldPath Attribute(string key) => new(AttributePrefix + ContactAttributeDefinition.RequireKey(key, nameof(key)));

    /// <summary>
    /// A field policy for a sensitive attribute: masked for <c>contact:read</c> and <c>contact:export</c> unless the grant uses one
    /// of <paramref name="unmaskedRoles"/>, revealable with <c>contact:reveal</c>.
    /// </summary>
    public static AccessFieldPolicy SensitivePolicy(FieldPath field, IEnumerable<RoleId>? unmaskedRoles = null) =>
        new(field, [ContactPermissions.Read, ContactPermissions.Export], unmaskedRoles, ContactPermissions.Reveal);
}

/// <summary>
/// The Access resource for a contact: its scope, identifier and labels. Contacts have no team, inbox, assignee or condition attributes,
/// so team/inbox grants never apply and attribute conditions are unknown (fail closed). Stores evaluate search filters over exactly
/// this shape.
/// </summary>
public static class ContactAccess
{
    public static AccessResource Resource(TicketFlow.Core.TicketScope scope, Contact contact)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(contact);
        return new(scope, contact.Id.Value, labels: contact.Labels);
    }

    public static AccessResource Resource(StoredContact stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return Resource(stored.Scope, stored.Contact);
    }

    /// <summary>A workspace-level resource (no contact), used for create, export and search-wide checks. Labels are unknown.</summary>
    public static AccessResource Workspace(TicketFlow.Core.TicketScope scope) => new(scope);
}
