using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>A conversation status: <see cref="Kind"/> is <c>open</c>, <c>pending</c>, <c>snoozed</c>, <c>resolved</c> or <c>closed</c>.</summary>
public sealed record ConversationStatusResponse(string Kind, DateTimeOffset? SnoozedUntil = null);

public sealed record ChannelThreadResponse(string Channel, string? ExternalThreadId, bool Masked, bool Revealable);

public sealed record ConversationAttributeResponse(string Key, string Type, bool Sensitive, AttributeValueModel? Value, bool Masked, bool Revealable);

public sealed record ConversationResponse(
    string Id,
    long Revision,
    string InboxId,
    string ContactId,
    ConversationStatusResponse Status,
    string Priority,
    string? AssigneeId,
    string? TeamId,
    IReadOnlyList<string> Labels,
    IReadOnlyList<ConversationAttributeResponse> Attributes,
    IReadOnlyList<string> Participants,
    ChannelThreadResponse? ChannelThread,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? WaitingSince,
    DateTimeOffset? FirstReplyAt,
    DateTimeOffset? LastIncomingAt,
    DateTimeOffset? LastOutgoingAt,
    long LastSequence,
    IReadOnlyList<MaskedFieldResponse> MaskedFields);

/// <summary>A message party: <see cref="Kind"/> is <c>contact</c> (<see cref="ContactId"/>) or <c>principal</c> (<see cref="Actor"/>).</summary>
public sealed record PartyResponse(string Kind, string? ContactId = null, ActorResponse? Actor = null);

/// <summary>Delivery state: <c>pending</c>, <c>sent</c>, <c>delivered</c>, <c>read</c>, <c>failed</c> or <c>received</c>.</summary>
public sealed record DeliveryResponse(string State, DateTimeOffset UpdatedAt, string? FailureCode);

public sealed record TimelineMessageResponse(string Id, string Direction, PartyResponse Sender, string Body, string ContentType, IReadOnlyList<AttachmentModel> Attachments, string? ExternalId, DeliveryResponse Delivery);

public sealed record TimelineNoteResponse(string Id, string Body, IReadOnlyList<AttachmentModel> Attachments);

/// <summary>A change between two optional identifier or token values (assignment, team, priority).</summary>
public sealed record TimelineValueChangeResponse(string? Previous, string? Current);

public sealed record TimelineStatusChangeResponse(ConversationStatusResponse Previous, ConversationStatusResponse Current);

public sealed record TimelineLabelsChangeResponse(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public sealed record TimelineAttributeChangeResponse(string Key, AttributeValueModel? Previous, AttributeValueModel? Current);

public sealed record TimelineParticipantChangeResponse(string UserId, string Change);

public sealed record TimelineContactChangeResponse(string Previous, string Current, Guid? Merge);

public sealed record TimelineSystemResponse(string Code, string? Detail);

/// <summary>A feature-owned extension event; <see cref="Payload"/> is the owning feature's JSON object.</summary>
public sealed record TimelineExtensionResponse(string Kind, int PayloadVersion, JsonElement Payload);

/// <summary>
/// One timeline event. <see cref="Kind"/> (<c>message</c>, <c>note</c>, <c>assignment</c>, <c>team</c>, <c>status</c>, <c>priority</c>,
/// <c>labels</c>, <c>attribute</c>, <c>participant</c>, <c>contact</c>, <c>system</c>, <c>extension</c>) selects the one populated payload.
/// </summary>
public sealed record TimelineEventResponse(
    Guid Id,
    long Sequence,
    DateTimeOffset OccurredAt,
    ActorResponse Actor,
    string Kind,
    IReadOnlyList<MaskedFieldResponse> MaskedFields,
    TimelineMessageResponse? Message = null,
    TimelineNoteResponse? Note = null,
    TimelineValueChangeResponse? Assignment = null,
    TimelineValueChangeResponse? Team = null,
    TimelineStatusChangeResponse? Status = null,
    TimelineValueChangeResponse? Priority = null,
    TimelineLabelsChangeResponse? Labels = null,
    TimelineAttributeChangeResponse? Attribute = null,
    TimelineParticipantChangeResponse? Participant = null,
    TimelineContactChangeResponse? Contact = null,
    TimelineSystemResponse? System = null,
    TimelineExtensionResponse? Extension = null);

public sealed record TimelinePageResponse(string ConversationId, IReadOnlyList<TimelineEventResponse> Events, long? NextAfterSequence, bool NotesIncluded);

/// <summary>A command result: the conversation revision afterwards and the sequences appended (refetch the timeline to read them).</summary>
public sealed record ConversationCommandResponse(string ConversationId, long Revision, IReadOnlyList<long> Sequences, bool Replayed);

public sealed record MessageInput(string Body, string? ContentType = null, IReadOnlyList<AttachmentModel>? Attachments = null, string? Id = null);

public sealed record ConversationStartRequest(
    string InboxId,
    string ContactId,
    string? Priority = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyDictionary<string, AttributeValueModel>? Attributes = null,
    string? TeamId = null,
    string? AssigneeId = null,
    MessageInput? InitialMessage = null,
    string? Id = null);

public sealed record NoteInput(string Body, IReadOnlyList<AttachmentModel>? Attachments = null, string? Id = null);

public sealed record AssigneeRequest(string? UserId);

public sealed record TeamAssignmentRequest(string? TeamId);

public sealed record StatusRequest(string Status, DateTimeOffset? SnoozedUntil = null);

public sealed record SnoozeRequest(DateTimeOffset Until);

public sealed record PriorityRequest(string Priority);

public sealed record LabelsRequest(IReadOnlyList<string>? Add = null, IReadOnlyList<string>? Remove = null);

/// <summary>Attribute changes; a <see langword="null"/> value removes the attribute.</summary>
public sealed record AttributesRequest(IReadOnlyDictionary<string, AttributeValueModel?> Changes);

public sealed record ParticipantsRequest(IReadOnlyList<string>? Add = null, IReadOnlyList<string>? Remove = null);
