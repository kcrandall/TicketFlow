using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Attribute condition AST attached to grants. Evaluation uses three-valued (Kleene) logic: a predicate over a resource attribute
/// or label set that the snapshot does not supply, or a principal reference the principal cannot satisfy, is <em>unknown</em>.
/// An allow grant applies only when its condition is true; a deny grant applies unless its condition is false, so missing
/// attributes fail closed in both directions. Trees are bounded by construction: depth at most <see cref="MaximumDepth"/> and at
/// most <see cref="MaximumNodes"/> nodes.
/// </summary>
public closed record AccessCondition
{
    public const int MaximumDepth = 8;
    public const int MaximumNodes = 64;
    public const int MaximumValues = 64;
    public const int MaximumValueLength = 256;

    private protected AccessCondition(int depth, int nodeCount)
    {
        if (depth > MaximumDepth)
            throw new ArgumentException($"Conditions can be at most {MaximumDepth} levels deep.");
        if (nodeCount > MaximumNodes)
            throw new ArgumentException($"Conditions can contain at most {MaximumNodes} nodes.");
        Depth = depth;
        NodeCount = nodeCount;
    }

    /// <summary>Levels in this tree; a leaf has depth 1.</summary>
    public int Depth { get; }

    public int NodeCount { get; }

    private protected static (int Depth, int Nodes) Measure(IReadOnlyList<AccessCondition> items)
    {
        var depth = 0;
        var nodes = 1;
        foreach (var item in items)
        {
            depth = Math.Max(depth, item.Depth);
            nodes += item.NodeCount;
        }
        return (depth + 1, nodes);
    }

    private protected static (IReadOnlyList<AccessCondition> List, int Depth, int Nodes) Items(IEnumerable<AccessCondition> items, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(items, parameterName);
        var capacity = items.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumNodes) : 0;
        var snapshot = capacity > 0 ? new List<AccessCondition>(capacity) : new List<AccessCondition>();
        var depth = 0;
        var nodes = 1;
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, parameterName);
            snapshot.Add(item);
            if (snapshot.Count > MaximumNodes)
                throw new ArgumentException($"Conditions can contain at most {MaximumNodes} nodes.", parameterName);
            depth = Math.Max(depth, item.Depth);
            nodes += item.NodeCount;
        }
        if (snapshot.Count == 0)
            throw new ArgumentException("At least one operand is required.", parameterName);
        return (snapshot.AsReadOnly(), depth + 1, nodes);
    }

    internal static string RequireKey(string key, string parameterName) =>
        AccessText.RequireToken(key, AccessResource.MaximumAttributeKeyLength, parameterName, "Attribute keys");

    internal static string RequireValue(string value, string parameterName) =>
        AccessText.RequireText(value, MaximumValueLength, parameterName, allowEmpty: true);
}

/// <summary>True when every operand is true.</summary>
public sealed record AndCondition : AccessCondition
{
    public AndCondition(params IEnumerable<AccessCondition> items)
        : this(Items(items, nameof(items)))
    {
    }

    private AndCondition((IReadOnlyList<AccessCondition> List, int Depth, int Nodes) items)
        : base(items.Depth, items.Nodes) => Operands = items.List;

    public IReadOnlyList<AccessCondition> Operands { get; }

    public bool Equals(AndCondition? other) => other is not null && Operands.SequenceEqual(other.Operands);

    public override int GetHashCode() => HashCode.Combine(nameof(AndCondition), Operands.Count, Operands[0]);
}

/// <summary>True when any operand is true.</summary>
public sealed record OrCondition : AccessCondition
{
    public OrCondition(params IEnumerable<AccessCondition> items)
        : this(Items(items, nameof(items)))
    {
    }

    private OrCondition((IReadOnlyList<AccessCondition> List, int Depth, int Nodes) items)
        : base(items.Depth, items.Nodes) => Operands = items.List;

    public IReadOnlyList<AccessCondition> Operands { get; }

    public bool Equals(OrCondition? other) => other is not null && Operands.SequenceEqual(other.Operands);

    public override int GetHashCode() => HashCode.Combine(nameof(OrCondition), Operands.Count, Operands[0]);
}

/// <summary>Negation; unknown stays unknown.</summary>
public sealed record NotCondition : AccessCondition
{
    public NotCondition(AccessCondition operand)
        : base(Required(operand).Depth + 1, operand.NodeCount + 1) => Operand = operand;

    public AccessCondition Operand { get; }

    private static AccessCondition Required(AccessCondition operand)
    {
        ArgumentNullException.ThrowIfNull(operand);
        return operand;
    }
}

/// <summary>The resource's assignee is the acting user. Unknown for non-user principals; false for unassigned resources.</summary>
public sealed record AssigneeIsSelfCondition() : AccessCondition(1, 1);

/// <summary>The resource's team is in the acting user's team snapshot. Unknown for non-user principals; false when the resource has no team.</summary>
public sealed record TeamInPrincipalTeamsCondition() : AccessCondition(1, 1);

/// <summary>A resource attribute equals a literal (ordinal). Unknown when the attribute is not in the snapshot.</summary>
public sealed record AttributeEqualsCondition : AccessCondition
{
    public AttributeEqualsCondition(string key, string value)
        : base(1, 1)
    {
        Key = RequireKey(key, nameof(key));
        Value = RequireValue(value, nameof(value));
    }

    public string Key { get; }
    public string Value { get; }
}

/// <summary>A resource attribute is one of 1-64 literals (ordinal). Unknown when the attribute is not in the snapshot.</summary>
public sealed record AttributeInCondition : AccessCondition
{
    private readonly string[] values;

    public AttributeInCondition(string key, params IEnumerable<string> values)
        : base(1, 1)
    {
        Key = RequireKey(key, nameof(key));
        ArgumentNullException.ThrowIfNull(values);
        var snapshot = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            snapshot.Add(RequireValue(value, nameof(values)));
            if (snapshot.Count > MaximumValues)
                throw new ArgumentException($"At most {MaximumValues} values are allowed.", nameof(values));
        }
        if (snapshot.Count == 0)
            throw new ArgumentException("At least one value is required.", nameof(values));
        this.values = [.. snapshot];
    }

    /// <summary>Distinct values in ordinal order.</summary>
    public IReadOnlyList<string> Values => values;

    public bool ContainsValue(string value) => Array.BinarySearch(values, value, StringComparer.Ordinal) >= 0;

    public string Key { get; }

    public bool Equals(AttributeInCondition? other) => other is not null && Key == other.Key && Values.SequenceEqual(other.Values);

    public override int GetHashCode() => HashCode.Combine(Key, Values.Count, Values[0]);
}

/// <summary>The resource carries a label. Unknown when the snapshot does not include labels.</summary>
public sealed record HasLabelCondition : AccessCondition
{
    public HasLabelCondition(LabelId label)
        : base(1, 1)
    {
        if (label == default)
            throw new ArgumentException("A label identifier is required.", nameof(label));
        Label = label;
    }

    public LabelId Label { get; }
}

/// <summary>
/// The evaluation instant's UTC time of day is within [<see cref="StartUtc"/>, <see cref="EndUtc"/>). A start after the end wraps
/// midnight. Query filters evaluate the window once at plan time.
/// </summary>
public sealed record TimeWindowCondition : AccessCondition
{
    public TimeWindowCondition(TimeOnly startUtc, TimeOnly endUtc)
        : base(1, 1)
    {
        if (startUtc == endUtc)
            throw new ArgumentException("A time window needs distinct start and end times.", nameof(endUtc));
        StartUtc = Truncate(startUtc);
        EndUtc = Truncate(endUtc);
        if (StartUtc == EndUtc)
            throw new ArgumentException("A time window needs distinct start and end times.", nameof(endUtc));
    }

    public TimeOnly StartUtc { get; }
    public TimeOnly EndUtc { get; }

    public bool Contains(DateTimeOffset instant)
    {
        var time = TimeOnly.FromTimeSpan(instant.UtcDateTime.TimeOfDay);
        return StartUtc < EndUtc ? time >= StartUtc && time < EndUtc : time >= StartUtc || time < EndUtc;
    }

    private static TimeOnly Truncate(TimeOnly value) => new(value.Hour, value.Minute, value.Second);
}
