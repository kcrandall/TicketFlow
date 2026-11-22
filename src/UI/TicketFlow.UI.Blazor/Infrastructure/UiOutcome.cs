using TicketFlow.Access;
using TicketFlow.Automation;
using TicketFlow.Channels;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.HelpCenter;
using TicketFlow.Reporting;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Infrastructure;

public enum UiOutcomeKind
{
    Success = 1,
    Info = 2,
    Forbidden = 3,
    NotFound = 4,
    Conflict = 5,
    Invalid = 6,
    Rejected = 7,
}

/// <summary>
/// The user-facing outcome of a form post, mapped from a feature's union result. <see cref="Messages"/> are the non-sensitive validation
/// messages produced by TicketFlow (never input values). <see cref="Redirect"/> overrides the page the post returns to (base-relative).
/// </summary>
public sealed record UiOutcome(UiOutcomeKind Kind, string Title, IReadOnlyList<string> Messages, long? CurrentRevision = null, string? Redirect = null)
{
    public bool Succeeded => Kind is UiOutcomeKind.Success or UiOutcomeKind.Info;

    public static UiOutcome Success(string title, string? redirect = null) => new(UiOutcomeKind.Success, title, [], null, redirect);

    public static UiOutcome Invalid(params string[] messages) => new(UiOutcomeKind.Invalid, "Check the highlighted values", messages);

    public static UiOutcome Forbidden() => new(UiOutcomeKind.Forbidden, "You don't have permission to do that", ["Ask a workspace administrator for access."]);

    public static UiOutcome NotFound() => new(UiOutcomeKind.NotFound, "That record no longer exists", ["It may have been deleted by someone else."]);

    public static UiOutcome Conflict(long? current) => new(UiOutcomeKind.Conflict, "Someone else changed this record",
        [current is { } revision ? $"It is now at revision {revision}. Review the latest values and try again." : "Review the latest values and try again."], current);

    /// <summary>Maps a refusal reason by name (every feature uses the same vocabulary: Forbidden, Invalid, NotFound, Conflict, ...).</summary>
    public static UiOutcome Refused(Enum reason, IReadOnlyList<string> errors, long? currentRevision = null) => reason.ToString() switch
    {
        "Forbidden" => Forbidden() with { Messages = errors.Count > 0 ? errors : Forbidden().Messages },
        "NotFound" => NotFound(),
        "Conflict" => Conflict(currentRevision),
        "Invalid" => new(UiOutcomeKind.Invalid, "Check the values and try again", errors.Count > 0 ? errors : ["The request was not valid."]),
        "Closed" => new(UiOutcomeKind.Rejected, "This conversation is closed", errors),
        "Merged" => new(UiOutcomeKind.Rejected, "This contact was merged into another contact", errors),
        "IdentityTaken" => new(UiOutcomeKind.Conflict, "That identity already belongs to another contact", errors),
        "Disabled" => new(UiOutcomeKind.Rejected, "This feature is disabled for the workspace", errors),
        "QuotaExceeded" or "RateLimited" => new(UiOutcomeKind.Rejected, "Usage limit reached — try again later", errors),
        var other => new(UiOutcomeKind.Rejected, "The change was rejected", errors.Count > 0 ? errors : [Humanize(other)]),
    };

    private static string Humanize(string token) => string.Concat(token.Select((character, index) => index > 0 && char.IsUpper(character) ? " " + char.ToLowerInvariant(character) : character.ToString()));
}

/// <summary>Maps every feature's union result to a <see cref="UiOutcome"/> (exhaustive switches, no discard arms).</summary>
public static class UiOutcomes
{
    public static UiOutcome From<T>(WorkspaceResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        WorkspaceSucceeded<T> value => success(value.Value),
        WorkspaceRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
    };

    public static UiOutcome From(ConversationCommandResult result, string success) => result switch
    {
        ConversationApplied => UiOutcome.Success(success),
        ConversationNotFound => UiOutcome.NotFound(),
        ConversationConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
        ConversationRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };

    public static UiOutcome From<T>(ContactResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        ContactApplied<T> value => success(value.Value),
        ContactRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
    };

    public static UiOutcome From<T>(AccessChangeResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        AccessChangeApplied<T> value => success(value.Value),
        AccessChangeRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
    };

    public static UiOutcome From<T>(SlaResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        SlaValue<T> value => success(value.Value),
        SlaNotFound => UiOutcome.NotFound(),
        SlaConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
        SlaRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };

    public static UiOutcome From<T>(RoutingResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        RoutingValue<T> value => success(value.Value),
        RoutingNotFound => UiOutcome.NotFound(),
        RoutingConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
        RoutingRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };

    public static UiOutcome From<T>(AutomationResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        AutomationValue<T> value => success(value.Value),
        AutomationNotFound => UiOutcome.NotFound(),
        AutomationConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
        AutomationRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };

    public static UiOutcome From<T>(HelpCenterResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        HelpCenterSucceeded<T> value => success(value.Value),
        HelpCenterRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
    };

    public static UiOutcome From<T>(SurveyResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        SurveySucceeded<T> value => success(value.Value),
        SurveyRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
    };

    public static UiOutcome From<T>(LiveChatResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        LiveChatValue<T> value => success(value.Value),
        LiveChatRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };

    public static UiOutcome From<T>(ChannelResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        ChannelValue<T> value => success(value.Value),
        ChannelNotFound => UiOutcome.NotFound(),
        ChannelConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
        ChannelRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };

    public static UiOutcome From<T>(ReportResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        ReportSucceeded<T> value => success(value.Value),
        ReportRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };
}
