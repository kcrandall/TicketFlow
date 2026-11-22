using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>A live-chat widget configuration; <see cref="Definition"/> is the versioned live-chat widget codec JSON.</summary>
public sealed record WidgetResponse(string ConfigurationId, string InboxId, JsonElement Definition, long Revision);

public sealed record WidgetRequest(string ConfigurationId, string InboxId, JsonElement Definition);

/// <summary>A channel endpoint binding an inbox's channel configuration (and optional lookup address such as a widget key).</summary>
public sealed record ChannelEndpointResponse(string Kind, string ConfigurationId, string InboxId, string? Address, long Revision);

public sealed record ChannelEndpointRequest(string Kind, string ConfigurationId, string InboxId, string? Address = null);

public sealed record PrechatAnswerResponse(string Key, string Label, string? Value, bool Masked);

/// <summary>The live-chat visitor behind a conversation as the agent may see it (masked identity and pre-chat values removed).</summary>
public sealed record VisitorResponse(string VisitorId, bool Verified, string? IdentityNamespace, string? IdentityValue, string? DisplayName, IReadOnlyList<PrechatAnswerResponse> Prechat,
    DateTimeOffset SessionCreatedAt, IReadOnlyList<MaskedFieldResponse> MaskedFields);
