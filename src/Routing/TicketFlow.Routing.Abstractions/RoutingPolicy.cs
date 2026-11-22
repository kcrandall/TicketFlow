using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>Persisted strategy discriminators. Never renumber.</summary>
public enum RoutingStrategyKind
{
    RoundRobin = 1,
    LeastBusy = 2,
    SkillsBased = 3,
    StickyOwner = 4,
    Manual = 5,
}

/// <summary>Rotate through eligible agents in identifier order, starting after the last agent this inbox routed to.</summary>
public sealed record RoundRobinStrategy
{
    private RoundRobinStrategy()
    {
    }

    public static RoundRobinStrategy Instance { get; } = new();
}

/// <summary>Pick the eligible agent with the lowest load relative to capacity (ties rotate like round robin).</summary>
public sealed record LeastBusyStrategy
{
    private LeastBusyStrategy()
    {
    }

    public static LeastBusyStrategy Instance { get; } = new();
}

/// <summary>A conversation label that requires a skill.</summary>
public sealed record LabelSkillMatch(LabelId Label)
{
    public LabelId Label { get; } = Label == default ? throw new ArgumentException("A label is required.", nameof(Label)) : Label;
}

/// <summary>A conversation attribute whose condition value (text verbatim, numbers invariant, booleans, dates) equals <see cref="Value"/>.</summary>
public sealed record AttributeSkillMatch
{
    public AttributeSkillMatch(string key, string value)
    {
        Key = RoutingText.RequireToken(key, 64, nameof(key), "Attribute keys");
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 256)
            throw new ArgumentException("Attribute match values contain at most 256 characters.", nameof(value));
        Value = value;
    }

    public string Key { get; }
    public string Value { get; }
}

/// <summary>What a skill rule matches on. Switch over every case without a discard arm.</summary>
public union SkillMatch(LabelSkillMatch, AttributeSkillMatch);

/// <summary>When <see cref="Match"/> holds for a conversation, agents need <see cref="Skill"/> at <see cref="MinimumLevel"/> or above.</summary>
public sealed record SkillRule
{
    public SkillRule(SkillMatch match, string skill, int minimumLevel = 1)
    {
        if (match.Value is null)
            throw new ArgumentException("A match is required.", nameof(match));
        Match = match;
        Skill = AgentSkill.RequireName(skill, nameof(skill));
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumLevel, AgentSkill.MinimumLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumLevel, AgentSkill.MaximumLevel);
        MinimumLevel = minimumLevel;
    }

    public SkillMatch Match { get; }
    public string Skill { get; }
    public int MinimumLevel { get; }
}

/// <summary>
/// Derive required skills from the conversation's labels and attributes (every matching rule applies; the highest minimum level per
/// skill wins), keep the agents holding all of them, then pick the least busy.
/// </summary>
public sealed record SkillsBasedStrategy
{
    public const int MaximumRules = 32;

    public SkillsBasedStrategy(IEnumerable<SkillRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = [.. rules];
        if (Rules.Count is < 1 or > MaximumRules || Rules.Any(rule => rule is null))
            throw new ArgumentException($"Skills-based routing needs 1-{MaximumRules} rules.", nameof(rules));
    }

    public IReadOnlyList<SkillRule> Rules { get; }

    /// <summary>Required skill levels for <paramref name="conversation"/>, keyed by skill name (ordinal).</summary>
    public IReadOnlyDictionary<string, int> RequiredSkills(Conversations.Conversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var required = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            var applies = rule.Match switch
            {
                LabelSkillMatch label => conversation.Labels.Contains(label.Label),
                AttributeSkillMatch attribute => conversation.Attributes.TryGetValue(attribute.Key, out var value) && value.ConditionValue == attribute.Value,
            };
            if (applies)
                required[rule.Skill] = Math.Max(required.GetValueOrDefault(rule.Skill), rule.MinimumLevel);
        }
        return required;
    }

    public bool Equals(SkillsBasedStrategy? other) => other is not null && Rules.SequenceEqual(other.Rules);

    public override int GetHashCode() => Rules.Count;
}

/// <summary>
/// Prefer the contact's previous assignee when they were assigned within <see cref="Window"/> and are still eligible and under capacity;
/// otherwise use <see cref="Fallback"/> (any strategy except sticky owner).
/// </summary>
public sealed record StickyOwnerStrategy
{
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(365);

    public StickyOwnerStrategy(TimeSpan window, RoutingStrategy fallback)
    {
        Window = RoutingText.RequireDuration(window, MaximumWindow, nameof(window));
        if (fallback.Value is null || fallback.Value is StickyOwnerStrategy)
            throw new ArgumentException("Sticky owner routing needs a non-sticky fallback strategy.", nameof(fallback));
        Fallback = fallback;
    }

    public TimeSpan Window { get; }
    public RoutingStrategy Fallback { get; }
}

/// <summary>No automatic assignment: conversations wait in the queue for an agent or supervisor.</summary>
public sealed record ManualStrategy
{
    private ManualStrategy()
    {
    }

    public static ManualStrategy Instance { get; } = new();
}

/// <summary>How an inbox assigns conversations. Switch over every case without a discard arm; persisted by <see cref="RoutingPolicyCodec"/>.</summary>
public union RoutingStrategy(RoundRobinStrategy, LeastBusyStrategy, SkillsBasedStrategy, StickyOwnerStrategy, ManualStrategy)
{
    public static RoutingStrategy RoundRobin => RoundRobinStrategy.Instance;
    public static RoutingStrategy LeastBusy => LeastBusyStrategy.Instance;
    public static RoutingStrategy Manual => ManualStrategy.Instance;

    /// <exception cref="System.Runtime.CompilerServices.SwitchExpressionException">The strategy is <c>default</c>.</exception>
    public RoutingStrategyKind Kind => this switch
    {
        RoundRobinStrategy => RoutingStrategyKind.RoundRobin,
        LeastBusyStrategy => RoutingStrategyKind.LeastBusy,
        SkillsBasedStrategy => RoutingStrategyKind.SkillsBased,
        StickyOwnerStrategy => RoutingStrategyKind.StickyOwner,
        ManualStrategy => RoutingStrategyKind.Manual,
    };
}

/// <summary>Move the conversation to a team (sets the conversation team; the pool becomes that team's members).</summary>
public sealed record OverflowToTeam(TeamId Team)
{
    public TeamId Team { get; } = Team == default ? throw new ArgumentException("A team is required.", nameof(Team)) : Team;
}

/// <summary>
/// Route from another inbox's queue and policy. The conversation keeps its inbox (immutable); only the routing target changes, so the
/// other inbox's agents still need <c>conversation:reply</c> on the conversation itself.
/// </summary>
public sealed record OverflowToInbox(InboxId Inbox)
{
    public InboxId Inbox { get; } = Inbox == default ? throw new ArgumentException("An inbox is required.", nameof(Inbox)) : Inbox;
}

/// <summary>Where an overflow rule sends a waiting conversation. Switch over every case without a discard arm.</summary>
public union OverflowTarget(OverflowToTeam, OverflowToInbox);

/// <summary>After a conversation has waited <see cref="After"/> in the queue, apply <see cref="Target"/> once.</summary>
public sealed record OverflowRule
{
    public OverflowRule(TimeSpan after, OverflowTarget target)
    {
        After = RoutingText.RequireDuration(after, RoutingPolicy.MaximumWaitLimit, nameof(after));
        if (target.Value is null)
            throw new ArgumentException("A target is required.", nameof(target));
        Target = target;
    }

    public TimeSpan After { get; }
    public OverflowTarget Target { get; }
}

/// <summary>What happens to an agent's open conversations when the agent goes offline (explicitly or by presence expiry). Never renumber.</summary>
public enum OfflineReassignment
{
    /// <summary>Conversations stay assigned.</summary>
    Keep = 1,

    /// <summary>Conversations are unassigned and routed again.</summary>
    ReturnToQueue = 2,
}

/// <summary>
/// Routing settings referenced by an inbox's <see cref="RoutingPolicyId"/>: the strategy, an optional pool team (only its members are
/// candidates when the conversation has no team), ascending overflow rules, a maximum wait (after which a
/// <see cref="QueueWaitExceeded"/> event is raised once) and the offline reassignment behavior.
/// </summary>
public sealed record RoutingPolicy
{
    public const int MaximumNameLength = 128;
    public const int MaximumOverflowRules = 8;
    public static readonly TimeSpan MaximumWaitLimit = TimeSpan.FromDays(30);

    public RoutingPolicy(RoutingPolicyId id, string name, RoutingStrategy strategy, TeamId? team = null, IEnumerable<OverflowRule>? overflow = null,
        TimeSpan? maximumWait = null, OfflineReassignment onAgentOffline = OfflineReassignment.Keep)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A routing policy identifier is required.", nameof(id));
        if (strategy.Value is null)
            throw new ArgumentException("A strategy is required.", nameof(strategy));
        if (team == default(TeamId))
            throw new ArgumentException("The team must be initialized when supplied.", nameof(team));
        if (!Enum.IsDefined(onAgentOffline))
            throw new ArgumentOutOfRangeException(nameof(onAgentOffline));
        Id = id;
        Name = RoutingText.RequireText(name, MaximumNameLength, nameof(name));
        Strategy = strategy;
        Team = team;
        Overflow = [.. overflow ?? []];
        if (Overflow.Count > MaximumOverflowRules || Overflow.Any(rule => rule is null))
            throw new ArgumentException($"A policy has at most {MaximumOverflowRules} overflow rules.", nameof(overflow));
        for (var index = 1; index < Overflow.Count; index++)
            if (Overflow[index].After <= Overflow[index - 1].After)
                throw new ArgumentException("Overflow rules must be in strictly ascending wait order.", nameof(overflow));
        MaximumWait = maximumWait is { } wait ? RoutingText.RequireDuration(wait, MaximumWaitLimit, nameof(maximumWait)) : null;
        OnAgentOffline = onAgentOffline;
    }

    public RoutingPolicyId Id { get; }
    public string Name { get; }
    public RoutingStrategy Strategy { get; }
    public TeamId? Team { get; }
    public IReadOnlyList<OverflowRule> Overflow { get; }
    public TimeSpan? MaximumWait { get; }
    public OfflineReassignment OnAgentOffline { get; }

    public bool Equals(RoutingPolicy? other) =>
        other is not null && Id == other.Id && Name == other.Name && Strategy.Equals(other.Strategy) && Team == other.Team
        && Overflow.SequenceEqual(other.Overflow) && MaximumWait == other.MaximumWait && OnAgentOffline == other.OnAgentOffline;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Strategy.Kind, Team, Overflow.Count, MaximumWait, OnAgentOffline);

    public override string ToString() => $"RoutingPolicy {{ Id = {Id}, Strategy = {Strategy.Kind} }}";
}

/// <summary>A policy with its scope and revision (first revision is 1).</summary>
public sealed record StoredRoutingPolicy(TicketScope Scope, RoutingPolicy Policy, long Revision);
