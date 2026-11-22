using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>A conversation status: <see cref="Kind"/> is <c>open</c>, <c>pending</c>, <c>snoozed</c>, <c>resolved</c> or <c>closed</c>.</summary>
public sealed record ConversationStatusDto(string Kind, DateTimeOffset? SnoozedUntil = null);

public sealed record ChannelThreadDto(string Channel, string? ExternalThreadId, bool Masked, bool Revealable);

public sealed record ConversationAttributeDto(string Key, string Type, bool Sensitive, AttributeValueDto? Value, bool Masked, bool Revealable);

public sealed record ConversationDto(
    string Id,
    long Revision,
    string InboxId,
    string ContactId,
    ConversationStatusDto Status,
    string Priority,
    string? AssigneeId,
    string? TeamId,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ConversationAttributeDto> Attributes,
    IReadOnlyList<string> Participants,
    ChannelThreadDto? ChannelThread,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? WaitingSince,
    DateTimeOffset? FirstReplyAt,
    DateTimeOffset? LastIncomingAt,
    DateTimeOffset? LastOutgoingAt,
    long LastSequence,
    IReadOnlyList<MaskedFieldDto> MaskedFields);

/// <summary>A message party: <see cref="Kind"/> is <c>contact</c> (<see cref="ContactId"/>) or <c>principal</c> (<see cref="Actor"/>).</summary>
public sealed record PartyDto(string Kind, string? ContactId = null, ActorDto? Actor = null);

/// <summary>Delivery state: <c>pending</c>, <c>sent</c>, <c>delivered</c>, <c>read</c>, <c>failed</c> or <c>received</c>.</summary>
public sealed record DeliveryDto(string State, DateTimeOffset UpdatedAt, string? FailureCode);

public sealed record TimelineMessageDto(string Id, string Direction, PartyDto Sender, string Body, string ContentType, IReadOnlyList<AttachmentDto> Attachments, string? ExternalId, DeliveryDto Delivery);

public sealed record TimelineNoteDto(string Id, string Body, IReadOnlyList<AttachmentDto> Attachments);

/// <summary>A change between two optional identifier or token values (assignment, team, priority).</summary>
public sealed record TimelineValueChangeDto(string? Previous, string? Current);

public sealed record TimelineStatusChangeDto(ConversationStatusDto Previous, ConversationStatusDto Current);

public sealed record TimelineLabelsChangeDto(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public sealed record TimelineAttributeChangeDto(string Key, AttributeValueDto? Previous, AttributeValueDto? Current);

public sealed record TimelineParticipantChangeDto(string UserId, string Change);

public sealed record TimelineContactChangeDto(string Previous, string Current, Guid? Merge);

public sealed record TimelineSystemDto(string Code, string? Detail);

/// <summary>A feature-owned extension event; <see cref="Payload"/> is the owning feature's JSON object.</summary>
public sealed record TimelineExtensionDto(string Kind, int PayloadVersion, JsonElement Payload);

/// <summary>
/// One timeline event. <see cref="Kind"/> (<c>message</c>, <c>note</c>, <c>assignment</c>, <c>team</c>, <c>status</c>, <c>priority</c>,
/// <c>labels</c>, <c>attribute</c>, <c>participant</c>, <c>contact</c>, <c>system</c>, <c>extension</c>) selects the one populated payload.
/// </summary>
public sealed record TimelineEventDto(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    ActorDto Actor,
    string Kind,
    IReadOnlyList<MaskedFieldDto> MaskedFields,
    TimelineMessageDto? Message = null,
    TimelineNoteDto? Note = null,
    TimelineValueChangeDto? Assignment = null,
    TimelineValueChangeDto? Team = null,
    TimelineStatusChangeDto? Status = null,
    TimelineValueChangeDto? Priority = null,
    TimelineLabelsChangeDto? Labels = null,
    TimelineAttributeChangeDto? Attribute = null,
    TimelineParticipantChangeDto? Participant = null,
    TimelineContactChangeDto? Contact = null,
    TimelineSystemDto? System = null,
    TimelineExtensionDto? Extension = null);

public sealed record TimelinePageDto(string ConversationId, IReadOnlyList<TimelineEventDto> Events, long? NextAfterSequence, bool NotesIncluded);

/// <summary>A command result: the conversation revision afterwards and the sequences appended (refetch the timeline to read them).</summary>
public sealed record ConversationCommandDto(string ConversationId, long Revision, IReadOnlyList<long> Sequences, bool Replayed);

public sealed record MessageInput(string Body, string? ContentType = null, IReadOnlyList<AttachmentDto>? Attachments = null, string? Id = null);

public sealed record StartConversationRequestDto(
    string InboxId,
    string ContactId,
    string? Priority = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyDictionary<string, AttributeValueDto>? Attributes = null,
    string? TeamId = null,
    string? AssigneeId = null,
    MessageInput? InitialMessage = null,
    string? Id = null);

public sealed record NoteInput(string Body, IReadOnlyList<AttachmentDto>? Attachments = null, string? Id = null);

public sealed record AssigneeRequest(string? UserId);

public sealed record TeamAssignmentRequest(string? TeamId);

public sealed record StatusRequest(string Status, DateTimeOffset? SnoozedUntil = null);

public sealed record SnoozeRequest(DateTimeOffset Until);

public sealed record PriorityRequest(string Priority);

public sealed record LabelsRequest(IReadOnlyList<string>? Add = null, IReadOnlyList<string>? Remove = null);

/// <summary>Attribute changes; a <see langword="null"/> value removes the attribute.</summary>
public sealed record AttributesRequest(IReadOnlyDictionary<string, AttributeValueDto?> Changes);

public sealed record ParticipantsRequest(IReadOnlyList<string>? Add = null, IReadOnlyList<string>? Remove = null);
