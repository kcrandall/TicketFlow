using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Surveys;

namespace TicketFlow.Reporting;

/// <summary>
/// <see cref="IReportSatisfactionSource"/> over survey aggregates (<see cref="ISurveyStore.AggregateAsync"/>): the caller's planned
/// filter is applied by the survey store over the response resource (inbox, team, agent as assignee; labels unknown).
/// </summary>
public sealed class SurveyReportSatisfactionSource(ISurveyStore surveys) : IReportSatisfactionSource
{
    private readonly ISurveyStore surveys = surveys ?? throw new ArgumentNullException(nameof(surveys));

    public async Task<IReadOnlyList<ReportSatisfactionRow>> GetAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, ReportDimension groupBy, AccessFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);
        var dimension = groupBy switch
        {
            ReportDimension.None => SurveyDimension.None,
            ReportDimension.Inbox => SurveyDimension.Inbox,
            ReportDimension.Team => SurveyDimension.Team,
            ReportDimension.Agent => SurveyDimension.Agent,
            _ => throw new ArgumentOutOfRangeException(nameof(groupBy), "Satisfaction groups by inbox, team or agent only."),
        };
        if (filter is DenyAllFilter)
            return [];
        var groups = new Dictionary<string, ReportSatisfaction>(StringComparer.Ordinal);
        ReportSatisfaction? ungrouped = null;
        foreach (var kind in new[] { SurveyKind.Csat, SurveyKind.Nps })
        {
            var result = await surveys.AggregateAsync(scope, new SurveyAggregateQuery(kind, from, to, SurveyTimeBucket.None, dimension), filter, cancellationToken).ConfigureAwait(false);
            foreach (var row in result.Rows)
            {
                var counts = kind == SurveyKind.Csat
                    ? new ReportSatisfaction(row.Responses, row.Positive, 0, 0, 0)
                    : new ReportSatisfaction(0, 0, row.Responses, row.Positive, row.Negative);
                if (row.Group is null)
                    ungrouped = (ungrouped ?? ReportSatisfaction.Empty).Add(counts);
                else
                    groups[row.Group] = groups.GetValueOrDefault(row.Group, ReportSatisfaction.Empty).Add(counts);
            }
        }
        var rows = new List<ReportSatisfactionRow>();
        if (ungrouped is not null)
            rows.Add(new(null, ungrouped));
        rows.AddRange(groups.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ReportSatisfactionRow(pair.Key, pair.Value)));
        return rows;
    }
}

/// <summary>The satisfaction source used when no survey store is registered: always empty.</summary>
public sealed class NoReportSatisfactionSource : IReportSatisfactionSource
{
    private NoReportSatisfactionSource()
    {
    }

    public static NoReportSatisfactionSource Instance { get; } = new();

    public Task<IReadOnlyList<ReportSatisfactionRow>> GetAsync(TicketScope scope, DateTimeOffset from, DateTimeOffset to, ReportDimension groupBy, AccessFilter filter,
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ReportSatisfactionRow>>([]);
}
