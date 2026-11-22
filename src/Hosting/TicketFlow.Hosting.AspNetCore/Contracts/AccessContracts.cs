using System.Text.Json;

namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>
/// The caller as TicketFlow sees it: principal kind and identifier, the team snapshot and the non-sensitive catalog permissions it holds
/// at workspace level (narrower team/inbox grants are not probed; sensitive permissions only with <c>includeSensitive=true</c>, which
/// records an audit event per sensitive decision). The UI may hide actions with it; the server still enforces every request.
/// </summary>
public sealed record MeResponse(string Kind, string Id, IReadOnlyList<string> Teams, IReadOnlyList<string> WorkspacePermissions);

public sealed record RoleResponse(string Id, string Name, IReadOnlyList<string> Permissions, bool IncludesAllPermissions, bool BuiltIn, long? Revision);

public sealed record RoleRequest(string Name, IReadOnlyList<string> Permissions, string? Id = null);

/// <summary>A grant subject: <see cref="Kind"/> is <c>user</c>, <c>team</c> or <c>apiClient</c>.</summary>
public sealed record GrantSubjectModel(string Kind, string Id);

/// <summary>A grant scope: <see cref="Kind"/> is <c>tenant</c>, <c>workspace</c>, <c>team</c> or <c>inbox</c>; <see cref="Id"/> names the team or inbox.</summary>
public sealed record GrantScopeModel(string Kind, string? Id = null);

/// <summary>An access grant; <see cref="Condition"/> is the versioned access condition codec JSON (<c>{"v":1,"c":{...}}</c>).</summary>
public sealed record GrantResponse(
    string Id,
    GrantSubjectModel Subject,
    string Role,
    GrantScopeModel Scope,
    string Effect,
    JsonElement? Condition,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    string? Justification,
    string Kind,
    long Revision);

public sealed record GrantRequest(
    GrantSubjectModel Subject,
    string Role,
    GrantScopeModel Scope,
    string? Effect = null,
    JsonElement? Condition = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null,
    string? Justification = null,
    string? Id = null);

public sealed record FieldPolicyResponse(string Field, IReadOnlyList<string> AppliesTo, IReadOnlyList<string> UnmaskedRoles, string? RevealPermission, long Revision);

public sealed record FieldPolicyRequest(string Field, IReadOnlyList<string> AppliesTo, IReadOnlyList<string>? UnmaskedRoles = null, string? RevealPermission = null);

/// <summary>Requests a time-bound elevated grant for the caller (audited; never confers administration).</summary>
public sealed record BreakGlassActivationRequest(string Role, GrantScopeModel Scope, int DurationMinutes, string Justification, string? Id = null);
