using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Reporting;

/// <summary>
/// Reporting permissions. <c>report:view</c> is the Access built-in (Supervisor, Viewer, Admin, Owner). <c>report:export</c>
/// (sensitive: every decision and plan is audited) and <c>report:view-own</c> are registered by <c>AddReporting()</c>; only Owner
/// holds them until custom roles grant them.
/// </summary>
public static class ReportPermissions
{
    /// <summary>View reports over the rows the planned filter allows (team/inbox scopes and conditions apply).</summary>
    public static Permission View => AccessPermissions.ReportView;

    /// <summary>Export report facts as CSV (sensitive).</summary>
    public static Permission Export { get; } = new("report:export");

    /// <summary>View one's own agent statistics: rows attributed to the principal (user principals only), within the planned filter.</summary>
    public static Permission ViewOwn { get; } = new("report:view-own");

    /// <summary>The feature-owned catalog definitions, ordered by permission.</summary>
    public static IReadOnlyList<PermissionDefinition> Definitions { get; } =
    [
        new(Export, true, "Export support report facts."),
        new(ViewOwn, false, "View one's own agent report statistics."),
    ];
}

/// <summary>
/// The Access snapshots reporting stores filter over. Rollup rows carry inbox, team and the attributed agent (as assignee) with
/// unknown labels and attributes, so label and attribute conditions fail closed on aggregates; fact rows and conversation states
/// also carry their labels. Attributes are always unknown.
/// </summary>
public static class ReportResources
{
    public static AccessResource Rollup(TicketScope scope, InboxId inbox, TeamId? team, UserId? agent)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return new(scope, null, team, inbox, agent);
    }

    public static AccessResource Fact(TicketScope scope, ReportFact fact)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(fact);
        var dimensions = fact.Dimensions;
        return new(scope, fact.Conversation.Value, dimensions.Team, dimensions.Inbox, dimensions.Agent, dimensions.Labels);
    }

    public static AccessResource Conversation(TicketScope scope, ReportConversationState state)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        return new(scope, state.Conversation.Value, state.Team, state.Inbox, state.Assignee, state.Labels);
    }

    public static AccessResource Workspace(TicketScope scope) => new(scope ?? throw new ArgumentNullException(nameof(scope)));
}
