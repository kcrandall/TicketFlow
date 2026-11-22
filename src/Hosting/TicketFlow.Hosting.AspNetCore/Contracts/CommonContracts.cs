using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

// Wire DTOs are separate from domain records: no union JSON, explicit "kind"/"type" discriminators, enums as camelCase strings and
// identifiers as strings. Complex nested definitions travel as the owning feature's versioned codec JSON (JsonElement).

/// <summary>One page; <see cref="Next"/> is an opaque continuation token (<see langword="null"/> at the end).</summary>
public sealed record PageResponse<T>(IReadOnlyList<T> Items, string? Next);

/// <summary>A field removed by masking; <see cref="Revealable"/> when the caller may reveal it explicitly.</summary>
public sealed record MaskedFieldResponse(string Field, bool Revealable);

/// <summary>An acting principal: kind (<c>user</c>, <c>apiClient</c>, <c>automation</c>, <c>system</c>) and identifier; never credentials.</summary>
public sealed record ActorResponse(string Kind, string Id);

/// <summary>
/// A typed custom attribute value. <see cref="Type"/> is <c>text</c>, <c>number</c>, <c>boolean</c>, <c>date</c> or <c>list</c> and
/// selects the one populated property.
/// </summary>
public sealed record AttributeValueModel(string Type, string? Text = null, decimal? Number = null, bool? Boolean = null, DateOnly? Date = null, IReadOnlyList<string>? Items = null);

/// <summary>An attachment reference held by application or channel storage (TicketFlow never stores bytes).</summary>
public sealed record AttachmentModel(string StorageRef, string FileName, string MediaType, long SizeBytes);

/// <summary>A feature-owned versioned definition (codec JSON) with its identifier and revision.</summary>
public sealed record DefinitionResponse(string Id, JsonElement Definition, long Revision);

/// <summary>A create or update of a codec-encoded definition; <see cref="Id"/> is generated when omitted on create.</summary>
public sealed record DefinitionRequest(JsonElement Definition, string? Id = null);
