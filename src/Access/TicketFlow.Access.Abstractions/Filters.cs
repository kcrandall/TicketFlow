using System.Collections.Frozen;
using System.Collections.ObjectModel;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Store-neutral list/search filter produced by <see cref="IAccessQueryPlanner"/>. Semantics are three-valued (Kleene) logic like
/// SQL: a row is included only when the filter is <em>true</em>. Team, inbox, assignee and scope predicates are two-valued
/// (a missing team is "no team"); attribute and label predicates are unknown when the data is missing. Translators that cannot
/// translate a node must reject the whole filter (deny-all), never drop or weaken the node.
/// </summary>
public closed record AccessFilter
{
    /// <summary>Maximum nodes in one filter tree.</summary>
    public const int MaximumNodes = 4096;

    private protected AccessFilter(int nodeCount)
    {
        if (nodeCount > MaximumNodes)
            throw new AccessFilterLimitExceededException();
        NodeCount = nodeCount;
    }

    public int NodeCount { get; }

    private protected static (IReadOnlyList<AccessFilter> List, int Nodes) Items(IEnumerable<AccessFilter> items, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(items, parameterName);
        var capacity = items.TryGetNonEnumeratedCount(out var count) ? count : 0;
        var snapshot = capacity > 0 ? new List<AccessFilter>(capacity) : new List<AccessFilter>();
        var nodes = 1;
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, parameterName);
            snapshot.Add(item);
            nodes += item.NodeCount;
        }
        if (snapshot.Count == 0)
            throw new ArgumentException("At least one operand is required.", parameterName);
        return (snapshot.AsReadOnly(), nodes);
    }
}

/// <summary>A filter tree would exceed <see cref="AccessFilter.MaximumNodes"/>. Planners convert this into a deny-all plan.</summary>
public sealed class AccessFilterLimitExceededException()
    : ArgumentException($"Access filters can contain at most {AccessFilter.MaximumNodes} nodes.");

/// <summary>Constant true.</summary>
public sealed record AllowAllFilter : AccessFilter
{
    private AllowAllFilter()
        : base(1)
    {
    }

    public static AllowAllFilter Instance { get; } = new();
}

/// <summary>Constant false.</summary>
public sealed record DenyAllFilter : AccessFilter
{
    private DenyAllFilter()
        : base(1)
    {
    }

    public static DenyAllFilter Instance { get; } = new();
}

/// <summary>Constant unknown (for example a self reference for a non-user principal). Never true; its negation is also unknown.</summary>
public sealed record UnknownFilter : AccessFilter
{
    private UnknownFilter()
        : base(1)
    {
    }

    public static UnknownFilter Instance { get; } = new();
}

/// <summary>The row's full scope equals <see cref="Scope"/>.</summary>
public sealed record ScopeFilter : AccessFilter
{
    public ScopeFilter(TicketScope scope)
        : base(1)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
    }

    public TicketScope Scope { get; }
}

public sealed record AndFilter : AccessFilter
{
    public AndFilter(params IEnumerable<AccessFilter> items)
        : this(Items(items, nameof(items)))
    {
    }

    private AndFilter((IReadOnlyList<AccessFilter> List, int Nodes) items)
        : base(items.Nodes) => Operands = items.List;

    public IReadOnlyList<AccessFilter> Operands { get; }

    public bool Equals(AndFilter? other) => other is not null && Operands.SequenceEqual(other.Operands);

    public override int GetHashCode() => HashCode.Combine(nameof(AndFilter), Operands.Count);
}

public sealed record OrFilter : AccessFilter
{
    public OrFilter(params IEnumerable<AccessFilter> items)
        : this(Items(items, nameof(items)))
    {
    }

    private OrFilter((IReadOnlyList<AccessFilter> List, int Nodes) items)
        : base(items.Nodes) => Operands = items.List;

    public IReadOnlyList<AccessFilter> Operands { get; }

    public bool Equals(OrFilter? other) => other is not null && Operands.SequenceEqual(other.Operands);

    public override int GetHashCode() => HashCode.Combine(nameof(OrFilter), Operands.Count);
}

public sealed record NotFilter : AccessFilter
{
    public NotFilter(AccessFilter operand)
        : base((operand ?? throw new ArgumentNullException(nameof(operand))).NodeCount + 1) => Operand = operand;

    public AccessFilter Operand { get; }
}

/// <summary>The row's team is in <see cref="Teams"/>; false when the row has no team.</summary>
public sealed record TeamInFilter : AccessFilter
{
    public TeamInFilter(IEnumerable<TeamId> teams)
        : base(1)
    {
        ArgumentNullException.ThrowIfNull(teams);
        var capacity = teams.TryGetNonEnumeratedCount(out var count) ? count : 0;
        if (count == 0 && teams is ICollection<TeamId> or IReadOnlyCollection<TeamId>)
        {
            Teams = FrozenSet<TeamId>.Empty;
            return;
        }
        var snapshot = capacity > 0 ? new HashSet<TeamId>(capacity) : new HashSet<TeamId>();
        foreach (var team in teams)
        {
            if (team == default)
                throw new ArgumentException("Team identifiers must be initialized.", nameof(teams));
            snapshot.Add(team);
        }
        Teams = snapshot.Count == 0 ? FrozenSet<TeamId>.Empty : new ReadOnlySet<TeamId>(snapshot);
    }

    public IReadOnlySet<TeamId> Teams { get; }

    public bool Equals(TeamInFilter? other) => other is not null && Teams.SetEquals(other.Teams);

    public override int GetHashCode() => Teams.Count;
}

/// <summary>The row's inbox is in <see cref="Inboxes"/>; false when the row has no inbox.</summary>
public sealed record InboxInFilter : AccessFilter
{
    public InboxInFilter(IEnumerable<InboxId> inboxes)
        : base(1)
    {
        ArgumentNullException.ThrowIfNull(inboxes);
        var capacity = inboxes.TryGetNonEnumeratedCount(out var count) ? count : 0;
        if (count == 0 && inboxes is ICollection<InboxId> or IReadOnlyCollection<InboxId>)
        {
            Inboxes = FrozenSet<InboxId>.Empty;
            return;
        }
        var snapshot = capacity > 0 ? new HashSet<InboxId>(capacity) : new HashSet<InboxId>();
        foreach (var inbox in inboxes)
        {
            if (inbox == default)
                throw new ArgumentException("Inbox identifiers must be initialized.", nameof(inboxes));
            snapshot.Add(inbox);
        }
        Inboxes = snapshot.Count == 0 ? FrozenSet<InboxId>.Empty : new ReadOnlySet<InboxId>(snapshot);
    }

    public IReadOnlySet<InboxId> Inboxes { get; }

    public bool Equals(InboxInFilter? other) => other is not null && Inboxes.SetEquals(other.Inboxes);

    public override int GetHashCode() => Inboxes.Count;
}

/// <summary>The row's assignee is <see cref="Assignee"/>; false when unassigned.</summary>
public sealed record AssigneeIsFilter : AccessFilter
{
    public AssigneeIsFilter(UserId assignee)
        : base(1)
    {
        if (assignee == default)
            throw new ArgumentException("An assignee identifier is required.", nameof(assignee));
        Assignee = assignee;
    }

    public UserId Assignee { get; }
}

/// <summary>The row carries <see cref="Label"/>; unknown when the row's labels are unknown.</summary>
public sealed record HasLabelFilter : AccessFilter
{
    public HasLabelFilter(LabelId label)
        : base(1)
    {
        if (label == default)
            throw new ArgumentException("A label identifier is required.", nameof(label));
        Label = label;
    }

    public LabelId Label { get; }
}

/// <summary>Attribute equality; unknown when the attribute is missing.</summary>
public sealed record AttributeEqualsFilter : AccessFilter
{
    public AttributeEqualsFilter(string key, string value)
        : base(1)
    {
        Key = AccessCondition.RequireKey(key, nameof(key));
        Value = AccessCondition.RequireValue(value, nameof(value));
    }

    public string Key { get; }
    public string Value { get; }
}

/// <summary>Attribute membership in 1-64 values; unknown when the attribute is missing.</summary>
public sealed record AttributeInFilter : AccessFilter
{
    private readonly string[] values;

    public AttributeInFilter(string key, IEnumerable<string> values)
        : base(1)
    {
        Key = AccessCondition.RequireKey(key, nameof(key));
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            snapshot.Add(AccessCondition.RequireValue(value, nameof(values)));
        if (snapshot.Count is 0 or > AccessCondition.MaximumValues)
            throw new ArgumentException($"Between 1 and {AccessCondition.MaximumValues} values are required.", nameof(values));
        this.values = [.. snapshot];
    }

    public string Key { get; }

    /// <summary>Distinct values in ordinal order.</summary>
    public IReadOnlyList<string> Values => values;

    public bool ContainsValue(string value) => Array.BinarySearch(values, value, StringComparer.Ordinal) >= 0;

    public bool Equals(AttributeInFilter? other) => other is not null && Key == other.Key && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => HashCode.Combine(Key, Values.Count);
}

/// <summary>The reference in-memory translator: evaluates a filter against a resource snapshot with Kleene logic.</summary>
public static class AccessFilterEvaluator
{
    /// <summary>True only when the filter is definitely true for <paramref name="resource"/>.</summary>
    public static bool Matches(AccessFilter filter, AccessResource resource) => Evaluate(filter, resource) == true;

    /// <summary>Three-valued evaluation; <see langword="null"/> is unknown.</summary>
    public static bool? Evaluate(AccessFilter filter, AccessResource resource)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(resource);
        return EvaluateCore(filter, resource);
    }

    private static bool? EvaluateCore(AccessFilter filter, AccessResource resource) => filter switch
    {
        AllowAllFilter => true,
        DenyAllFilter => false,
        UnknownFilter => null,
        ScopeFilter scope => scope.Scope == resource.Scope,
        AndFilter and => EvaluateAnd(and.Operands, resource),
        OrFilter or => EvaluateOr(or.Operands, resource),
        NotFilter not => !EvaluateCore(not.Operand, resource),
        TeamInFilter team => resource.TeamId is { } teamId && team.Teams.Contains(teamId),
        InboxInFilter inbox => resource.InboxId is { } inboxId && inbox.Inboxes.Contains(inboxId),
        AssigneeIsFilter assignee => resource.AssigneeId == assignee.Assignee,
        HasLabelFilter label => resource.Labels?.Contains(label.Label),
        AttributeEqualsFilter equals => resource.Attributes.TryGetValue(equals.Key, out var value) ? value == equals.Value : null,
        AttributeInFilter @in => resource.Attributes.TryGetValue(@in.Key, out var value) ? @in.ContainsValue(value) : null,
    };

    private static bool? EvaluateAnd(IReadOnlyList<AccessFilter> operands, AccessResource resource)
    {
        var unknown = false;
        for (var i = 0; i < operands.Count; i++)
        {
            var value = EvaluateCore(operands[i], resource);
            if (value == false)
                return false;
            unknown |= value is null;
        }
        return unknown ? null : true;
    }

    private static bool? EvaluateOr(IReadOnlyList<AccessFilter> operands, AccessResource resource)
    {
        var unknown = false;
        for (var i = 0; i < operands.Count; i++)
        {
            var value = EvaluateCore(operands[i], resource);
            if (value == true)
                return true;
            unknown |= value is null;
        }
        return unknown ? null : false;
    }
}

/// <summary>Kleene three-valued connectives.</summary>
public static class Kleene
{
    public static bool? And<T>(IEnumerable<T> items, Func<T, bool?> evaluate)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(evaluate);
        var unknown = false;
        foreach (var item in items)
        {
            var value = evaluate(item);
            if (value == false)
                return false;
            unknown |= value is null;
        }
        return unknown ? null : true;
    }

    public static bool? Or<T>(IEnumerable<T> items, Func<T, bool?> evaluate)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(evaluate);
        var unknown = false;
        foreach (var item in items)
        {
            var value = evaluate(item);
            if (value == true)
                return true;
            unknown |= value is null;
        }
        return unknown ? null : false;
    }
}
