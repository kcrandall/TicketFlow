using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Assist;

/// <summary>
/// Permissions registered by <c>AddAssist()</c>. <c>assist:use</c> is checked on the conversation's Access resource (so team/inbox
/// grants and conditions apply) in addition to every read the context needs; <c>assist:manage</c> (sensitive) is checked on the
/// workspace for settings, quotas and usage reports. Built-in Owner holds both; other roles receive them through custom roles.
/// </summary>
public static class AssistPermissions
{
    public static Permission Use { get; } = new("assist:use");
    public static Permission Manage { get; } = new("assist:manage");

    /// <summary>The feature-owned catalog definitions, ordered by permission.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Manage, true, "Configure assistant models, quotas and rate limits and read usage."),
        new(Use, false, "Request assistant summaries, drafts, triage, sentiment, translations and tone rewrites."),
    ];

    public static AccessResource Workspace(TicketScope scope) => new(scope);
}
