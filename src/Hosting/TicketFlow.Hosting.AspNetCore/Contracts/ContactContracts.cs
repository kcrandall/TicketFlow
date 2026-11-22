namespace TicketFlow.Hosting.AspNetCore.Contracts;

public sealed record ContactProfileModel(string? DisplayName = null, string? AvatarRef = null, string? Locale = null, string? TimeZone = null, string? Company = null);

/// <summary>
/// A contact identity as shown to the caller. <see cref="Kind"/> is <c>email</c>, <c>phone</c>, <c>whatsApp</c>, <c>widgetVisitor</c>,
/// <c>externalUser</c> or <c>socialHandle</c>. When <see cref="Masked"/>, <see cref="Value"/> is <see langword="null"/>.
/// </summary>
public sealed record ContactIdentityResponse(string Kind, string Namespace, string? Value, bool Verified, string Source, DateTimeOffset At, bool Masked, bool Revealable);

public sealed record ContactAttributeResponse(string Key, string Type, bool Sensitive, AttributeValueModel? Value, bool Masked, bool Revealable);

public sealed record ContactResponse(
    string Id,
    long Revision,
    ContactProfileModel Profile,
    IReadOnlyList<ContactIdentityResponse> Identities,
    IReadOnlyList<ContactAttributeResponse> Attributes,
    IReadOnlyList<string> Labels,
    bool Blocked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MaskedFieldResponse> MaskedFields);

/// <summary>An identity to add: <see cref="Verified"/> identities were proven by <see cref="Source"/>; others are claimed evidence.</summary>
public sealed record ContactIdentityInput(string Kind, string Value, string? Namespace = null, bool Verified = false, string Source = "agent");

public sealed record ContactCreateRequest(
    ContactProfileModel? Profile = null,
    IReadOnlyList<ContactIdentityInput>? Identities = null,
    IReadOnlyDictionary<string, AttributeValueModel>? Attributes = null,
    IReadOnlyList<string>? Labels = null,
    bool Blocked = false,
    string? Id = null);

/// <summary>
/// One edit. <see cref="Kind"/>: <c>setProfile</c> (<see cref="Profile"/>), <c>setAttribute</c> (<see cref="Key"/>, <see cref="Value"/>),
/// <c>removeAttribute</c> (<see cref="Key"/>), <c>addLabel</c>/<c>removeLabel</c> (<see cref="Label"/>), <c>setBlocked</c>
/// (<see cref="Blocked"/>), <c>addIdentity</c>/<c>removeIdentity</c> (<see cref="Identity"/>).
/// </summary>
public sealed record ContactEditInput(
    string Kind,
    ContactProfileModel? Profile = null,
    string? Key = null,
    AttributeValueModel? Value = null,
    string? Label = null,
    bool? Blocked = null,
    ContactIdentityInput? Identity = null);

public sealed record ContactUpdateRequest(IReadOnlyList<ContactEditInput> Edits);

public sealed record FieldRevealRequest(string Field, string? Justification = null);

public sealed record ContactIdentityValueResponse(string Kind, string Namespace, string Value);

/// <summary>The revealed values of one field (audited by Access; never cached by clients).</summary>
public sealed record ContactRevealResponse(string Field, IReadOnlyList<ContactIdentityValueResponse> Identities, AttributeValueModel? Attribute, Guid AuditEventId);

public sealed record ContactsMergeRequest(string Target, long TargetRevision, string Source, long SourceRevision, string? AttributeConflict = null, string? Justification = null);

public sealed record ContactMergeResponse(ContactResponse Target, Guid MergeId, Guid AuditEventId);

public sealed record ContactUnmergeResponse(ContactResponse Target, ContactResponse Source, Guid AuditEventId);

public sealed record ContactExportStartRequest(int MaximumContacts, string Justification);

public sealed record AttributeDefinitionResponse(string Key, string Type, bool Sensitive, string? DisplayName, long Revision);

public sealed record AttributeDefinitionRequest(string Key, string Type, bool Sensitive = false, string? DisplayName = null);
