namespace TicketFlow.Hosting.AspNetCore.Contracts;

public sealed record ContactProfileDto(string? DisplayName = null, string? AvatarRef = null, string? Locale = null, string? TimeZone = null, string? Company = null);

/// <summary>
/// A contact identity as shown to the caller. <see cref="Kind"/> is <c>email</c>, <c>phone</c>, <c>whatsApp</c>, <c>widgetVisitor</c>,
/// <c>externalUser</c> or <c>socialHandle</c>. When <see cref="Masked"/>, <see cref="Value"/> is <see langword="null"/>.
/// </summary>
public sealed record ContactIdentityDto(string Kind, string Namespace, string? Value, bool Verified, string Source, DateTimeOffset At, bool Masked, bool Revealable);

public sealed record ContactAttributeDto(string Key, string Type, bool Sensitive, AttributeValueDto? Value, bool Masked, bool Revealable);

public sealed record ContactDto(
    string Id,
    long Revision,
    ContactProfileDto Profile,
    IReadOnlyList<ContactIdentityDto> Identities,
    IReadOnlyList<ContactAttributeDto> Attributes,
    IReadOnlyList<string> Labels,
    bool Blocked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MaskedFieldDto> MaskedFields);

/// <summary>An identity to add: <see cref="Verified"/> identities were proven by <see cref="Source"/>; others are claimed evidence.</summary>
public sealed record ContactIdentityInput(string Kind, string Value, string? Namespace = null, bool Verified = false, string Source = "agent");

public sealed record ContactCreateRequest(
    ContactProfileDto? Profile = null,
    IReadOnlyList<ContactIdentityInput>? Identities = null,
    IReadOnlyDictionary<string, AttributeValueDto>? Attributes = null,
    IReadOnlyList<string>? Labels = null,
    bool Blocked = false,
    string? Id = null);

/// <summary>
/// One edit. <see cref="Kind"/>: <c>setProfile</c> (<see cref="Profile"/>), <c>setAttribute</c> (<see cref="Key"/>, <see cref="Value"/>),
/// <c>removeAttribute</c> (<see cref="Key"/>), <c>addLabel</c>/<c>removeLabel</c> (<see cref="Label"/>), <c>setBlocked</c>
/// (<see cref="Blocked"/>), <c>addIdentity</c>/<c>removeIdentity</c> (<see cref="Identity"/>).
/// </summary>
public sealed record ContactEditDto(
    string Kind,
    ContactProfileDto? Profile = null,
    string? Key = null,
    AttributeValueDto? Value = null,
    string? Label = null,
    bool? Blocked = null,
    ContactIdentityInput? Identity = null);

public sealed record ContactUpdateRequest(IReadOnlyList<ContactEditDto> Edits);

public sealed record FieldRevealRequest(string Field, string? Justification = null);

public sealed record ContactIdentityValueDto(string Kind, string Namespace, string Value);

/// <summary>The revealed values of one field (audited by Access; never cached by clients).</summary>
public sealed record ContactRevealedDto(string Field, IReadOnlyList<ContactIdentityValueDto> Identities, AttributeValueDto? Attribute, Guid AuditEventId);

public sealed record ContactMergeRequestDto(string Target, long TargetRevision, string Source, long SourceRevision, string? AttributeConflict = null, string? Justification = null);

public sealed record ContactMergeResultDto(ContactDto Target, Guid MergeId, Guid AuditEventId);

public sealed record ContactUnmergeResultDto(ContactDto Target, ContactDto Source, Guid AuditEventId);

public sealed record ContactExportRequestDto(int MaximumContacts, string Justification);

public sealed record AttributeDefinitionDto(string Key, string Type, bool Sensitive, string? DisplayName, long Revision);

public sealed record AttributeDefinitionRequest(string Key, string Type, bool Sensitive = false, string? DisplayName = null);
