using TicketFlow.Core;

namespace TicketFlow.Surveys;

/// <summary>Keyset cursor over responses ordered by (responded at descending, invitation identifier descending).</summary>
public sealed record SurveyResponseCursor(DateTimeOffset RespondedAt, SurveyInvitationId Invitation);

/// <summary>
/// A bounded response listing: responses with <see cref="From"/> ≤ responded at &lt; <see cref="To"/> (at most
/// <see cref="MaximumRange"/>), optionally of one survey, newest first, at most <see cref="Limit"/> (1-200) per page.
/// </summary>
public sealed record SurveyResponseQuery
{
    public static readonly TimeSpan MaximumRange = TimeSpan.FromDays(366);

    public SurveyResponseQuery(DateTimeOffset from, DateTimeOffset to, SurveyId? survey = null, int limit = 50, SurveyResponseCursor? after = null)
    {
        if (to <= from || to - from > MaximumRange)
            throw new ArgumentException("The range must be non-empty and at most 366 days.", nameof(to));
        if (survey is { IsInitialized: false } || after is { Invitation.IsInitialized: false })
            throw new ArgumentException("Survey and cursor identifiers must be initialized.");
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, SurveyLimits.MaximumPageSize);
        From = SurveyLimits.Truncate(from);
        To = SurveyLimits.Truncate(to);
        Survey = survey;
        Limit = limit;
        After = after;
    }

    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public SurveyId? Survey { get; }
    public int Limit { get; }
    public SurveyResponseCursor? After { get; }
}

public sealed record SurveyResponsePage(IReadOnlyList<StoredResponse> Items, SurveyResponseCursor? Next);

public enum SurveyTimeBucket
{
    /// <summary>One bucket for the whole range.</summary>
    None = 0,

    /// <summary>UTC calendar days.</summary>
    Day = 1,

    /// <summary>UTC ISO weeks starting on Monday.</summary>
    Week = 2,

    /// <summary>UTC calendar months.</summary>
    Month = 3,
}

public enum SurveyDimension
{
    None = 0,
    Inbox = 1,
    Team = 2,
    Agent = 3,
}

/// <summary>
/// A bounded aggregate over responses of one <see cref="Kind"/> with <see cref="From"/> ≤ responded at &lt; <see cref="To"/> (at most
/// 366 days), grouped by time bucket and optionally by inbox, team or agent, optionally limited to one survey. At most
/// <see cref="MaximumRows"/> groups are returned.
/// </summary>
public sealed record SurveyAggregateQuery
{
    public const int MaximumRows = 1000;

    public SurveyAggregateQuery(SurveyKind kind, DateTimeOffset from, DateTimeOffset to, SurveyTimeBucket bucket = SurveyTimeBucket.None, SurveyDimension groupBy = SurveyDimension.None, SurveyId? survey = null)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(bucket) || !Enum.IsDefined(groupBy))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (to <= from || to - from > SurveyResponseQuery.MaximumRange)
            throw new ArgumentException("The range must be non-empty and at most 366 days.", nameof(to));
        if (survey is { IsInitialized: false })
            throw new ArgumentException("The survey identifier must be initialized.", nameof(survey));
        Kind = kind;
        From = SurveyLimits.Truncate(from);
        To = SurveyLimits.Truncate(to);
        Bucket = bucket;
        GroupBy = groupBy;
        Survey = survey;
    }

    public SurveyKind Kind { get; }
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public SurveyTimeBucket Bucket { get; }
    public SurveyDimension GroupBy { get; }
    public SurveyId? Survey { get; }

    /// <summary>The UTC start of the bucket containing <paramref name="at"/> (<see cref="From"/> when <see cref="Bucket"/> is None).</summary>
    public DateTimeOffset BucketStart(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return Bucket switch
        {
            SurveyTimeBucket.None => From,
            SurveyTimeBucket.Day => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero),
            SurveyTimeBucket.Week => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-(((int)utc.DayOfWeek + 6) % 7)),
            SurveyTimeBucket.Month => new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero),
            _ => throw new InvalidOperationException("Unknown bucket."),
        };
    }

    /// <summary>The grouping key of <paramref name="response"/> (null when not grouped or the dimension is absent).</summary>
    public string? GroupKey(SurveyResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return GroupBy switch
        {
            SurveyDimension.None => null,
            SurveyDimension.Inbox => response.Inbox?.Value,
            SurveyDimension.Team => response.Team?.Value,
            SurveyDimension.Agent => response.Agent?.Value,
            _ => throw new InvalidOperationException("Unknown dimension."),
        };
    }
}

/// <summary>
/// One aggregate group: counts of positive (CSAT 4-5 / promoters 9-10), neutral (CSAT 3 / passives 7-8) and negative (CSAT 1-2 /
/// detractors 0-6) responses. <see cref="CsatPercent"/> is satisfied / responses × 100; <see cref="NpsScore"/> is
/// (promoters − detractors) / responses × 100.
/// </summary>
public sealed record SurveyAggregateRow(SurveyKind Kind, DateTimeOffset BucketStart, string? Group, long Responses, long Positive, long Neutral, long Negative, long ScoreSum)
{
    public double? CsatPercent => Kind == SurveyKind.Csat && Responses > 0 ? Positive * 100.0 / Responses : null;

    public double? NpsScore => Kind == SurveyKind.Nps && Responses > 0 ? (Positive - Negative) * 100.0 / Responses : null;

    public double? AverageScore => Responses > 0 ? (double)ScoreSum / Responses : null;
}

/// <summary>Aggregate rows ordered by (bucket start, group ordinal, nulls first). <see cref="Truncated"/> means more than <see cref="SurveyAggregateQuery.MaximumRows"/> groups existed.</summary>
public sealed record SurveyAggregateResult(IReadOnlyList<SurveyAggregateRow> Rows, bool Truncated);

/// <summary>
/// The Access snapshot of a survey response or invitation: its scope, invitation identifier, inbox, team and agent (as assignee).
/// Labels are unknown and attributes absent, so label/attribute conditions fail closed. Stores filter over exactly this shape.
/// </summary>
public static class SurveyResources
{
    public static Access.AccessResource Response(TicketScope scope, SurveyResponse response)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        return new(scope, response.Invitation.ToString(), response.Team, response.Inbox, response.Agent);
    }

    public static Access.AccessResource Invitation(TicketScope scope, SurveyInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(invitation);
        return new(scope, invitation.Id.ToString(), invitation.Team, invitation.Inbox, invitation.Agent);
    }

    /// <summary>A survey definition: its inbox when inbox-bound, otherwise the workspace.</summary>
    public static Access.AccessResource Survey(TicketScope scope, SurveyDefinition survey)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(survey);
        return survey.Inbox is { } inbox ? new(scope, survey.Id.Value, inbox: inbox) : new(scope, survey.Id.Value);
    }

    public static Access.AccessResource Workspace(TicketScope scope) => new(scope);
}

/// <summary>
/// Permissions used by surveys. <c>survey:read</c> and <c>survey:manage</c> are registered by <c>AddSurveys()</c>; aggregates reuse the
/// built-in <c>report:view</c>.
/// </summary>
public static class SurveyPermissions
{
    public static Access.Permission Read { get; } = new("survey:read");
    public static Access.Permission Manage { get; } = new("survey:manage");
    public static Access.Permission ReportView => Access.AccessPermissions.ReportView;

    /// <summary>The feature-owned catalog definitions, ordered by permission.</summary>
    public static IReadOnlyList<Access.PermissionDefinition> Definitions { get; } =
    [
        new(Manage, false, "Manage CSAT and NPS survey definitions."),
        new(Read, false, "Read survey definitions, invitations and responses."),
    ];
}

/// <summary>Maskable survey fields. A field policy on <see cref="Comment"/> for <c>survey:read</c> redacts response comments.</summary>
public static class SurveyFieldPaths
{
    public static Access.FieldPath Comment { get; } = new("survey.comment");
}
