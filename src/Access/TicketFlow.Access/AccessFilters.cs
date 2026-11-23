namespace TicketFlow.Access;

/// <summary>
/// Kleene-preserving constructors that fold constants, flatten nested connectives and coalesce two-valued membership siblings:
/// <c>team∈A ∨ team∈B = team∈(A∪B)</c> and <c>¬team∈A ∧ ¬team∈B = ¬team∈(A∪B)</c> (likewise for inboxes).
/// </summary>
public static class AccessFilters
{
    public static AccessFilter And(params IEnumerable<AccessFilter> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var operands = new List<AccessFilter>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
            if (item is DenyAllFilter)
                return DenyAllFilter.Instance;
            if (item is AllowAllFilter)
                continue;
            if (item is AndFilter nested)
                operands.AddRange(nested.Operands);
            else
                operands.Add(item);
        }
        operands = CoalesceNegated(operands);
        return operands.Count switch
        {
            0 => AllowAllFilter.Instance,
            1 => operands[0],
            _ => new AndFilter(operands),
        };
    }

    public static AccessFilter Or(params IEnumerable<AccessFilter> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var operands = new List<AccessFilter>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
            if (item is AllowAllFilter)
                return AllowAllFilter.Instance;
            if (item is DenyAllFilter)
                continue;
            if (item is OrFilter nested)
                operands.AddRange(nested.Operands);
            else
                operands.Add(item);
        }
        operands = Coalesce(operands, negated: false);
        return operands.Count switch
        {
            0 => DenyAllFilter.Instance,
            1 => operands[0],
            _ => new OrFilter(operands),
        };
    }

    public static AccessFilter Not(AccessFilter item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is AllowAllFilter)
            return DenyAllFilter.Instance;
        if (item is DenyAllFilter)
            return AllowAllFilter.Instance;
        if (item is UnknownFilter)
            return UnknownFilter.Instance;
        return item is NotFilter not ? not.Operand : new NotFilter(item);
    }

    private static List<AccessFilter> CoalesceNegated(List<AccessFilter> operands) =>
        operands.Count <= 1 ? operands : Coalesce(operands, negated: true);

    /// <summary>Merges every operand whose (unwrapped) form is a team or inbox membership into one membership per kind, in first-seen position.</summary>
    private static List<AccessFilter> Coalesce(List<AccessFilter> operands, bool negated)
    {
        if (operands.Count <= 1)
            return operands;
        TeamInFilter? firstTeam = null;
        InboxInFilter? firstInbox = null;
        HashSet<TicketFlow.Core.TeamId>? teams = null;
        HashSet<TicketFlow.Core.InboxId>? inboxes = null;
        int teamIndex = -1, inboxIndex = -1;
        var result = new List<AccessFilter>(operands.Count);
        for (var i = 0; i < operands.Count; i++)
        {
            var operand = operands[i];
            var inner = negated ? (operand is NotFilter not ? not.Operand : null) : operand;
            switch (inner)
            {
                case TeamInFilter team:
                    if (firstTeam is null)
                    {
                        firstTeam = team;
                        teamIndex = result.Count;
                        result.Add(operand);
                    }
                    else
                    {
                        if (teams is null)
                        {
                            teams = [.. firstTeam.Teams];
                        }
                        teams.UnionWith(team.Teams);
                    }
                    break;
                case InboxInFilter inbox:
                    if (firstInbox is null)
                    {
                        firstInbox = inbox;
                        inboxIndex = result.Count;
                        result.Add(operand);
                    }
                    else
                    {
                        if (inboxes is null)
                        {
                            inboxes = [.. firstInbox.Inboxes];
                        }
                        inboxes.UnionWith(inbox.Inboxes);
                    }
                    break;
                default:
                    result.Add(operand);
                    break;
            }
        }
        if (teams is not null)
        {
            AccessFilter merged = new TeamInFilter(teams);
            result[teamIndex] = negated ? new NotFilter(merged) : merged;
        }
        if (inboxes is not null)
        {
            AccessFilter merged = new InboxInFilter(inboxes);
            result[inboxIndex] = negated ? new NotFilter(merged) : merged;
        }
        return result;
    }
}
