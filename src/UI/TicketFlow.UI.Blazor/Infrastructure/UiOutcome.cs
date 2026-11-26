using TicketFlow.Channels.LiveChat;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;

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

    /// <summary>Maps a standard <see cref="TicketFailure"/> to a user-facing <see cref="UiOutcome"/>.</summary>
    public static UiOutcome FromFailure(TicketFailure failure) => failure.Kind switch
    {
        TicketFailureKind.Forbidden => Forbidden() with { Messages = failure.Errors.Count > 0 ? failure.Errors : Forbidden().Messages },
        TicketFailureKind.NotFound => NotFound(),
        TicketFailureKind.Conflict => ConflictFromFailure(failure),
        TicketFailureKind.Invalid => new(UiOutcomeKind.Invalid, "Check the values and try again", failure.Errors.Count > 0 ? failure.Errors : ["The request was not valid."]),
        TicketFailureKind.Rejected => new(UiOutcomeKind.Rejected, "The change was rejected", failure.Errors.Count > 0 ? failure.Errors : ["Rejected"]),
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    private static UiOutcome ConflictFromFailure(TicketFailure failure)
    {
        if (string.IsNullOrEmpty(failure.Code) || string.Equals(failure.Code, "concurrency-conflict", StringComparison.OrdinalIgnoreCase))
            return Conflict(failure.CurrentRevision);

        var title = failure.Code switch
        {
            "already-exists" or "taken" => "That record already exists",
            "service-account-disabled" => "Service account is disabled",
            "already-revoked" => "API key is already revoked",
            "role-in-use" => "Role is still in use",
            "existing-grants-conflict" or "service-account-conflict" => "Service account identifier conflicts with existing state",
            _ => "The request conflicts with existing state",
        };
        var messages = failure.Errors.Count > 0 ? failure.Errors : ["Review the current state and try again."];
        return new(UiOutcomeKind.Conflict, title, messages, failure.CurrentRevision);
    }

    /// <summary>Maps a domain-specific refusal reason (contacts, live chat, conversations, assist) to a <see cref="UiOutcome"/>.</summary>
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
    public static UiOutcome From<T>(TicketResult<T> result, Func<T, UiOutcome> success) => result switch
    {
        TicketValue<T> value => success(value.Value),
        TicketFailure failure => UiOutcome.FromFailure(failure),
    };

    public static UiOutcome From(ConversationCommandResult result, string success) => result switch
    {
        ConversationApplied => UiOutcome.Success(success),
        ConversationNotFound => UiOutcome.NotFound(),
        ConversationConflict conflict => UiOutcome.Conflict(conflict.CurrentRevision),
        ConversationRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };

    public static UiOutcome From<T>(TicketResult<T, ContactRefused> result, Func<T, UiOutcome> success) => result switch
    {
        TicketValue<T> value => success(value.Value),
        ContactRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors, refused.CurrentRevision),
    };

    public static UiOutcome From<T>(TicketResult<T, LiveChatRefused> result, Func<T, UiOutcome> success) => result switch
    {
        TicketValue<T> value => success(value.Value),
        LiveChatRefused refused => UiOutcome.Refused(refused.Reason, refused.Errors),
    };
}
