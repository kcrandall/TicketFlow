using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

public sealed record WorkspaceSettingsResponse(string Name, string DefaultLocale, string TimeZoneId, IReadOnlyDictionary<string, bool> FeatureToggles, long Revision);

public sealed record WorkspaceSettingsRequest(string Name, string DefaultLocale, string TimeZoneId, IReadOnlyDictionary<string, bool>? FeatureToggles = null);

public sealed record ChannelBindingModel(string Kind, string ConfigurationId);

public sealed record InboxResponse(
    string Id,
    string Name,
    ChannelBindingModel Channel,
    string? CalendarId,
    string? SlaPolicyId,
    string? RoutingPolicyId,
    bool Enabled,
    string? GreetingMessage,
    string? AwayMessage,
    long Revision);

/// <summary>Creates (<see cref="Id"/> generated when omitted) or replaces an inbox.</summary>
public sealed record InboxRequest(
    string Name,
    ChannelBindingModel Channel,
    string? CalendarId = null,
    string? SlaPolicyId = null,
    string? RoutingPolicyId = null,
    bool Enabled = true,
    string? GreetingMessage = null,
    string? AwayMessage = null,
    string? Id = null);

/// <summary>Grants <see cref="UserId"/> the <see cref="Role"/> (default <c>agent</c>) on the inbox.</summary>
public sealed record InboxAgentRequest(string UserId, string? Role = null);

public sealed record TeamResponse(string Id, string Name, string? Description, IReadOnlyList<string> Members, long Revision);

public sealed record TeamSummaryResponse(string Id, string Name, string? Description, int MemberCount, long Revision);

public sealed record TeamRequest(string Name, string? Description = null, IReadOnlyList<string>? Members = null, string? Id = null);

public sealed record TeamMemberRequest(string UserId);

/// <summary>A business calendar; <see cref="Schedule"/> is the versioned calendar codec JSON (<c>{"v":1,"week":[...],"overrides":[...]}</c>).</summary>
public sealed record CalendarResponse(string Id, string Name, string TimeZoneId, JsonElement Schedule, long Revision);

public sealed record CalendarRequest(string Name, string TimeZoneId, JsonElement Schedule, string? Id = null);
