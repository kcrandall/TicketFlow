using System.Globalization;
using System.Text.RegularExpressions;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Automation;

/// <summary>
/// Rule condition AST. Evaluation is three-valued (Kleene) over an <see cref="AutomationContext"/>: a predicate over data the context
/// does not supply (no message, no contact, a masked or missing attribute, no business calendar, a regex timeout) is
/// <em>unknown</em>, and a rule fires only when its condition is <em>true</em>, so missing or masked data never fires a rule. Trees
/// are bounded at construction: depth at most <see cref="MaximumDepth"/> and at most <see cref="MaximumNodes"/> nodes. Switch over
/// every case without a discard arm.
/// </summary>
public closed record AutomationCondition
{
    public const int MaximumDepth = 8;
    public const int MaximumNodes = 64;
    public const int MaximumRegexConditions = 8;
    public const int MaximumValues = 64;
    public const int MaximumValueLength = 256;

    private protected AutomationCondition(int depth, int nodeCount, int regexCount = 0)
    {
        if (depth > MaximumDepth)
            throw new ArgumentException($"Conditions can be at most {MaximumDepth} levels deep.", nameof(depth));
        if (nodeCount > MaximumNodes)
            throw new ArgumentException($"Conditions can contain at most {MaximumNodes} nodes.", nameof(nodeCount));
        if (regexCount > MaximumRegexConditions)
            throw new ArgumentException($"Conditions can contain at most {MaximumRegexConditions} regular expression matches.", nameof(regexCount));
        Depth = depth;
        NodeCount = nodeCount;
        RegexCount = regexCount;
    }

    /// <summary>Levels in this tree; a leaf has depth 1.</summary>
    public int Depth { get; }

    public int NodeCount { get; }

    public int RegexCount { get; }

    private protected static IReadOnlyList<AutomationCondition> Operands(IEnumerable<AutomationCondition> items, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(items, parameterName);
        var snapshot = new List<AutomationCondition>();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, parameterName);
            snapshot.Add(item);
            if (snapshot.Count > MaximumNodes)
                throw new ArgumentException($"Conditions can contain at most {MaximumNodes} nodes.", parameterName);
        }
        if (snapshot.Count == 0)
            throw new ArgumentException("At least one operand is required.", parameterName);
        return snapshot.AsReadOnly();
    }

    private protected static int DepthOf(IReadOnlyList<AutomationCondition> items) => items.Max(item => item.Depth) + 1;

    private protected static int NodesOf(IReadOnlyList<AutomationCondition> items) => items.Sum(item => item.NodeCount) + 1;

    private protected static int RegexCountOf(IReadOnlyList<AutomationCondition> items) => items.Sum(item => item.RegexCount);

    private protected static IReadOnlyList<string> RequireValues(IEnumerable<string> values, int minimum, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = new List<string>();
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value, parameterName);
            if (value.Length > MaximumValueLength || !System.Text.Unicode.Utf16.IsValid(value) || value.Any(character => character is <= '\u001F' or (>= '\u007F' and <= '\u009F')))
                throw new ArgumentException($"Condition values are at most {MaximumValueLength} characters without control characters.", parameterName);
            snapshot.Add(value);
        }
        if (snapshot.Count < minimum || snapshot.Count > maximum)
            throw new ArgumentException($"This operator takes {minimum}-{maximum} values.", parameterName);
        return snapshot.AsReadOnly();
    }
}

/// <summary>True when every operand is true.</summary>
public sealed record AllOfCondition : AutomationCondition
{
    public AllOfCondition(params IEnumerable<AutomationCondition> operands)
        : this(Operands(operands, nameof(operands)))
    {
    }

    private AllOfCondition(IReadOnlyList<AutomationCondition> operands)
        : base(DepthOf(operands), NodesOf(operands), RegexCountOf(operands)) => Items = operands;

    public IReadOnlyList<AutomationCondition> Items { get; }

    public bool Equals(AllOfCondition? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(nameof(AllOfCondition), Items.Count);
}

/// <summary>True when any operand is true.</summary>
public sealed record AnyOfCondition : AutomationCondition
{
    public AnyOfCondition(params IEnumerable<AutomationCondition> operands)
        : this(Operands(operands, nameof(operands)))
    {
    }

    private AnyOfCondition(IReadOnlyList<AutomationCondition> operands)
        : base(DepthOf(operands), NodesOf(operands), RegexCountOf(operands)) => Items = operands;

    public IReadOnlyList<AutomationCondition> Items { get; }

    public bool Equals(AnyOfCondition? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => HashCode.Combine(nameof(AnyOfCondition), Items.Count);
}

/// <summary>Negation; unknown stays unknown.</summary>
public sealed record NegatedCondition : AutomationCondition
{
    public NegatedCondition(AutomationCondition operand)
        : base((operand ?? throw new ArgumentNullException(nameof(operand))).Depth + 1, operand.NodeCount + 1, operand.RegexCount) => Operand = operand;

    public AutomationCondition Operand { get; }
}

/// <summary>The conversation status is one of <see cref="Statuses"/>.</summary>
public sealed record StatusIsCondition : AutomationCondition
{
    private readonly byte statusMask;

    public StatusIsCondition(params IEnumerable<ConversationStatusKind> statuses)
        : base(1, 1)
    {
        Statuses = [.. AutomationText.Distinct(statuses, 5, Enum.IsDefined, nameof(statuses), allowEmpty: false).Order()];
        byte mask = 0;
        for (var i = 0; i < Statuses.Count; i++)
            mask |= (byte)(1 << (int)Statuses[i]);
        statusMask = mask;
    }

    public IReadOnlyList<ConversationStatusKind> Statuses { get; }

    public bool Matches(ConversationStatusKind status) => (statusMask & (1 << (int)status)) != 0;

    public bool Equals(StatusIsCondition? other) => other is not null && Statuses.SequenceEqual(other.Statuses);

    public override int GetHashCode() => Statuses.Count;
}

/// <summary>Priority comparison operators (priorities order None &lt; Low &lt; Medium &lt; High &lt; Urgent). Never renumber.</summary>
public enum PriorityComparison
{
    Equal = 1,
    NotEqual = 2,
    AtLeast = 3,
    AtMost = 4,
}

public sealed record PriorityCondition : AutomationCondition
{
    public PriorityCondition(PriorityComparison comparison, ConversationPriority priority)
        : base(1, 1)
    {
        Comparison = AutomationText.Defined(comparison, nameof(comparison));
        Priority = AutomationText.Defined(priority, nameof(priority));
    }

    public PriorityComparison Comparison { get; }
    public ConversationPriority Priority { get; }
}

/// <summary>The conversation's inbox is one of <see cref="Inboxes"/>.</summary>
public sealed record InboxIsCondition : AutomationCondition
{
    public InboxIsCondition(params IEnumerable<InboxId> inboxes)
        : base(1, 1) => Inboxes = [.. AutomationText.Distinct(inboxes, MaximumValues, inbox => inbox != default, nameof(inboxes), allowEmpty: false)
            .OrderBy(inbox => inbox.Value, StringComparer.Ordinal)];

    public IReadOnlyList<InboxId> Inboxes { get; }

    public bool Equals(InboxIsCondition? other) => other is not null && Inboxes.SequenceEqual(other.Inboxes);

    public override int GetHashCode() => Inboxes.Count;
}

/// <summary>The conversation's team is one of <see cref="Teams"/>, or it has no team and <see cref="OrNone"/>.</summary>
public sealed record TeamIsCondition : AutomationCondition
{
    public TeamIsCondition(IEnumerable<TeamId> teams, bool orNone = false)
        : base(1, 1)
    {
        Teams = [.. AutomationText.Distinct(teams, MaximumValues, team => team != default, nameof(teams)).OrderBy(team => team.Value, StringComparer.Ordinal)];
        if (Teams.Count == 0 && !orNone)
            throw new ArgumentException("Name at least one team or match conversations without a team.", nameof(teams));
        OrNone = orNone;
    }

    public IReadOnlyList<TeamId> Teams { get; }
    public bool OrNone { get; }

    public bool Equals(TeamIsCondition? other) => other is not null && OrNone == other.OrNone && Teams.SequenceEqual(other.Teams);

    public override int GetHashCode() => HashCode.Combine(Teams.Count, OrNone);
}

/// <summary>The assignee is one of <see cref="Users"/>, or the conversation is unassigned and <see cref="OrUnassigned"/>.</summary>
public sealed record AssigneeIsCondition : AutomationCondition
{
    public AssigneeIsCondition(IEnumerable<UserId> users, bool orUnassigned = false)
        : base(1, 1)
    {
        Users = [.. AutomationText.Distinct(users, MaximumValues, user => user != default, nameof(users)).OrderBy(user => user.Value, StringComparer.Ordinal)];
        if (Users.Count == 0 && !orUnassigned)
            throw new ArgumentException("Name at least one user or match unassigned conversations.", nameof(users));
        OrUnassigned = orUnassigned;
    }

    public IReadOnlyList<UserId> Users { get; }
    public bool OrUnassigned { get; }

    public bool Equals(AssigneeIsCondition? other) => other is not null && OrUnassigned == other.OrUnassigned && Users.SequenceEqual(other.Users);

    public override int GetHashCode() => HashCode.Combine(Users.Count, OrUnassigned);
}

/// <summary>Whose labels or attributes a condition reads. Never renumber.</summary>
public enum ConditionTarget
{
    Conversation = 1,

    /// <summary>The conversation's contact (unknown when the contact cannot be read).</summary>
    Contact = 2,
}

/// <summary>How <see cref="HasLabelsCondition.Labels"/> must match. Never renumber.</summary>
public enum LabelMatch
{
    All = 1,
    Any = 2,
    None = 3,
}

public sealed record HasLabelsCondition : AutomationCondition
{
    public HasLabelsCondition(ConditionTarget target, LabelMatch match, params IEnumerable<LabelId> labels)
        : base(1, 1)
    {
        Target = AutomationText.Defined(target, nameof(target));
        Match = AutomationText.Defined(match, nameof(match));
        Labels = [.. AutomationText.Distinct(labels, MaximumValues, label => label != default, nameof(labels), allowEmpty: false).OrderBy(label => label.Value, StringComparer.Ordinal)];
    }

    public ConditionTarget Target { get; }
    public LabelMatch Match { get; }
    public IReadOnlyList<LabelId> Labels { get; }

    public bool Equals(HasLabelsCondition? other) => other is not null && Target == other.Target && Match == other.Match && Labels.SequenceEqual(other.Labels);

    public override int GetHashCode() => HashCode.Combine(Target, Match, Labels.Count);
}

/// <summary>The conversation's inbox channel kind is one of <see cref="Kinds"/> (unknown when the inbox cannot be read).</summary>
public sealed record ChannelKindIsCondition : AutomationCondition
{
    public ChannelKindIsCondition(params IEnumerable<ChannelKind> kinds)
        : base(1, 1) => Kinds = [.. AutomationText.Distinct(kinds, MaximumValues, kind => kind.IsInitialized, nameof(kinds), allowEmpty: false).Order()];

    public IReadOnlyList<ChannelKind> Kinds { get; }

    public bool Equals(ChannelKindIsCondition? other) => other is not null && Kinds.SequenceEqual(other.Kinds);

    public override int GetHashCode() => Kinds.Count;
}

/// <summary>Attribute comparison operators over the Access condition form of a value. Never renumber.</summary>
public enum AttributeOperator
{
    /// <summary>The attribute is present (false when absent, unknown when masked). No values.</summary>
    Exists = 1,

    /// <summary>Ordinal equality with the single value.</summary>
    Equal = 2,

    /// <summary>Ordinal inequality with the single value (unknown when absent or masked).</summary>
    NotEqual = 3,

    /// <summary>Equal to one of 1-64 values.</summary>
    In = 4,

    /// <summary>Text contains the single value (ordinal, case-insensitive); list attributes contain the value as an item.</summary>
    Contains = 5,

    /// <summary>Numbers compare numerically, dates and text ordinally.</summary>
    GreaterThan = 6,

    LessThan = 7,
}

/// <summary>
/// Compares a conversation or contact attribute. Values use the Access condition form (text verbatim, numbers invariant, booleans
/// <c>true</c>/<c>false</c>, dates <c>yyyy-MM-dd</c>). Masked attributes are unknown.
/// </summary>
public sealed record AttributeCondition : AutomationCondition
{
    public const int MaximumKeyLength = 64;

    private readonly string[] valuesArray;

    public AttributeCondition(ConditionTarget target, string key, AttributeOperator op, params IEnumerable<string> values)
        : base(1, 1)
    {
        Target = AutomationText.Defined(target, nameof(target));
        Key = AutomationText.RequireToken(key, MaximumKeyLength, nameof(key), "Attribute keys");
        Operator = AutomationText.Defined(op, nameof(op));
        valuesArray = op switch
        {
            AttributeOperator.Exists => [.. RequireValues(values, 0, 0, nameof(values))],
            AttributeOperator.In => [.. RequireValues(values, 1, MaximumValues, nameof(values)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            _ => [.. RequireValues(values, 1, 1, nameof(values))],
        };
        Values = valuesArray;
        if (op is AttributeOperator.GreaterThan or AttributeOperator.LessThan && valuesArray.Length == 1
            && decimal.TryParse(valuesArray[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric))
            NumericValue = numeric;
    }

    public ConditionTarget Target { get; }
    public string Key { get; }
    public AttributeOperator Operator { get; }
    public IReadOnlyList<string> Values { get; }
    public decimal? NumericValue { get; }

    public bool ContainsValue(string value) =>
        valuesArray.Length <= 8
            ? Array.IndexOf(valuesArray, value) >= 0
            : Array.BinarySearch(valuesArray, value, StringComparer.Ordinal) >= 0;

    public bool Equals(AttributeCondition? other) =>
        other is not null && Target == other.Target && string.Equals(Key, other.Key, StringComparison.Ordinal) && Operator == other.Operator && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Target, Key, Operator, Values.Count);
}

/// <summary>Message text operators. Never renumber.</summary>
public enum MessageTextOperator
{
    Contains = 1,
    NotContains = 2,
    StartsWith = 3,
    EndsWith = 4,

    /// <summary>A .NET regular expression evaluated with a match timeout; a timeout makes the condition unknown.</summary>
    Matches = 5,
}

/// <summary>
/// Tests the triggering message's body (unknown when the trigger has no message). Only the first <see cref="MaximumInputLength"/>
/// characters are examined. Regular expressions are at most <see cref="MaximumPatternLength"/> characters, culture-invariant, and
/// run with a match timeout of 1-250 ms (default 100 ms); a timeout makes the condition unknown.
/// </summary>
public sealed record MessageContentCondition : AutomationCondition
{
    public const int MaximumPatternLength = 512;
    public const int MaximumInputLength = 16 * 1024;
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMilliseconds(100);
    public static TimeSpan MaximumTimeout { get; } = TimeSpan.FromMilliseconds(250);

    public MessageContentCondition(MessageTextOperator op, string value, bool ignoreCase = true, TimeSpan? timeout = null)
        : base(1, 1, op == MessageTextOperator.Matches ? 1 : 0)
    {
        Operator = AutomationText.Defined(op, nameof(op));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > MaximumPatternLength || !System.Text.Unicode.Utf16.IsValid(value))
            throw new ArgumentException($"Message patterns are 1-{MaximumPatternLength} characters of well-formed Unicode.", nameof(value));
        Value = value;
        IgnoreCase = ignoreCase;
        Timeout = timeout ?? DefaultTimeout;
        if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Regex timeouts are 1-250 ms.");
        if (op == MessageTextOperator.Matches)
        {
            try
            {
                Regex = new Regex(value, RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), Timeout);
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException("The regular expression is invalid.", nameof(value), error);
            }
        }
    }

    public MessageTextOperator Operator { get; }
    public string Value { get; }
    public bool IgnoreCase { get; }
    public TimeSpan Timeout { get; }

    /// <summary>The expression for <see cref="MessageTextOperator.Matches"/>; otherwise <see langword="null"/>.</summary>
    public Regex? Regex { get; }

    public bool Equals(MessageContentCondition? other) =>
        other is not null && Operator == other.Operator && string.Equals(Value, other.Value, StringComparison.Ordinal) && IgnoreCase == other.IgnoreCase && Timeout == other.Timeout;

    public override int GetHashCode() => HashCode.Combine(Operator, Value, IgnoreCase, Timeout);
}

/// <summary>
/// True when the evaluation instant is (<see cref="Open"/>) or is not inside the business hours of the conversation inbox's calendar
/// (E3). Unknown when the inbox has no calendar, the calendar is missing or its time zone is unknown on the host.
/// </summary>
public sealed record BusinessHoursCondition(bool Open) : AutomationCondition(1, 1);

/// <summary>Conversation instants for <see cref="ElapsedCondition"/>. Never renumber.</summary>
public enum ConversationTimestamp
{
    Created = 1,
    LastActivity = 2,

    /// <summary>The oldest unanswered incoming message (unknown when nothing is waiting).</summary>
    WaitingSince = 3,
    LastIncoming = 4,
    LastOutgoing = 5,
}

public enum ElapsedComparison
{
    AtLeast = 1,
    LessThan = 2,
}

/// <summary>Compares the time elapsed since a conversation instant with <see cref="Duration"/> (1 minute-365 days; unknown when the instant is absent).</summary>
public sealed record ElapsedCondition : AutomationCondition
{
    public ElapsedCondition(ConversationTimestamp since, ElapsedComparison comparison, TimeSpan duration)
        : base(1, 1)
    {
        Since = AutomationText.Defined(since, nameof(since));
        Comparison = AutomationText.Defined(comparison, nameof(comparison));
        if (duration < TimeSpan.FromMinutes(1) || duration > TimeSpan.FromDays(365) || duration.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(duration), "Durations are whole seconds between one minute and 365 days.");
        Duration = duration;
    }

    public ConversationTimestamp Since { get; }
    public ElapsedComparison Comparison { get; }
    public TimeSpan Duration { get; }
}
