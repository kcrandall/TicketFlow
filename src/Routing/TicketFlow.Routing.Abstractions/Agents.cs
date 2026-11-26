using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Routing;

/// <summary>Persisted agent availability. Never renumber. Only <see cref="Online"/> agents receive automatic assignments.</summary>
public enum AgentStatus
{
    Online = 1,
    Busy = 2,
    Away = 3,
    Offline = 4,
}

/// <summary>A bounded skill tag (1-64 characters of a-z, 0-9, '-', '_' or '.') with a proficiency level from 1 to 10.</summary>
public sealed record AgentSkill
{
    public const int MaximumNameLength = 64;
    public const int MinimumLevel = 1;
    public const int MaximumLevel = 10;

    public AgentSkill(string name, int level = MinimumLevel)
    {
        Name = RequireName(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfLessThan(level, MinimumLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaximumLevel);
        Level = level;
    }

    public string Name { get; }
    public int Level { get; }

    public static string RequireName(string name, string parameterName) => RoutingText.RequireToken(name, MaximumNameLength, parameterName, "Skill names");
}

/// <summary>
/// An agent's routing configuration: the default number of concurrent open conversations per channel kind, per-kind overrides and
/// skills. A capacity of 0 means the agent receives no automatic assignments for that kind. Skills are kept in name order.
/// </summary>
public sealed record AgentRoutingSettings
{
    public const int MaximumCapacity = 1000;
    public const int MaximumCapacities = 32;
    public const int MaximumSkills = 32;

    public AgentRoutingSettings(UserId agent, int defaultCapacity, IEnumerable<KeyValuePair<ChannelKind, int>>? capacities = null, IEnumerable<AgentSkill>? skills = null)
    {
        if (agent == default)
            throw new ArgumentException("An agent identifier is required.", nameof(agent));
        Agent = agent;
        DefaultCapacity = RequireCapacity(defaultCapacity, nameof(defaultCapacity));
        var snapshot = new SortedDictionary<ChannelKind, int>();
        foreach (var (kind, capacity) in capacities ?? [])
        {
            if (!kind.IsInitialized)
                throw new ArgumentException("Channel kinds must be initialized.", nameof(capacities));
            if (!snapshot.TryAdd(kind, RequireCapacity(capacity, nameof(capacities))))
                throw new ArgumentException("Channel kinds must be unique.", nameof(capacities));
            if (snapshot.Count > MaximumCapacities)
                throw new ArgumentException($"At most {MaximumCapacities} channel capacities are allowed.", nameof(capacities));
        }
        Capacities = snapshot.AsReadOnly();
        var skillSnapshot = new SortedDictionary<string, AgentSkill>(StringComparer.Ordinal);
        foreach (var skill in skills ?? [])
        {
            ArgumentNullException.ThrowIfNull(skill, nameof(skills));
            if (!skillSnapshot.TryAdd(skill.Name, skill))
                throw new ArgumentException("Skill names must be unique.", nameof(skills));
            if (skillSnapshot.Count > MaximumSkills)
                throw new ArgumentException($"At most {MaximumSkills} skills are allowed.", nameof(skills));
        }
        Skills = [.. skillSnapshot.Values];
    }

    public UserId Agent { get; }
    public int DefaultCapacity { get; }

    /// <summary>Per-channel-kind capacities in kind order.</summary>
    public IReadOnlyDictionary<ChannelKind, int> Capacities { get; }

    /// <summary>Skills in name order (ordinal).</summary>
    public IReadOnlyList<AgentSkill> Skills { get; }

    public int CapacityFor(ChannelKind kind) => Capacities.TryGetValue(kind, out var capacity) ? capacity : DefaultCapacity;

    public int? SkillLevel(string name)
    {
        int lo = 0, hi = Skills.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var cmp = StringComparer.Ordinal.Compare(Skills[mid].Name, name);
            if (cmp == 0)
                return Skills[mid].Level;
            if (cmp < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return null;
    }

    /// <summary>True when the agent holds every required skill at or above its level.</summary>
    public bool HasSkills(IReadOnlyDictionary<string, int> required)
    {
        ArgumentNullException.ThrowIfNull(required);
        if (required.Count == 0)
            return true;
        foreach (var (name, minLevel) in required)
            if (SkillLevel(name) is not { } level || level < minLevel)
                return false;
        return true;
    }

    public bool Equals(AgentRoutingSettings? other) =>
        other is not null && Agent == other.Agent && DefaultCapacity == other.DefaultCapacity && Capacities.SequenceEqual(other.Capacities)
        && Skills.SequenceEqual(other.Skills);

    public override int GetHashCode() => HashCode.Combine(Agent, DefaultCapacity, Capacities.Count, Skills.Count);

    public override string ToString() => $"AgentRoutingSettings {{ Agent = {Agent} }}";

    private static int RequireCapacity(int capacity, string parameterName)
    {
        if (capacity is < 0 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(parameterName, $"Capacities are 0-{MaximumCapacity}.");
        return capacity;
    }
}

/// <summary>
/// Availability state. <see cref="ExpiresAt"/> is when presence lapses without a heartbeat (<see langword="null"/> when offline); an
/// agent whose presence lapsed is effectively offline even before the maintenance worker records it.
/// </summary>
public sealed record AgentPresence
{
    public AgentPresence(AgentStatus status, DateTimeOffset changedAt, DateTimeOffset? lastHeartbeatAt, DateTimeOffset? expiresAt)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status));
        if ((status == AgentStatus.Offline) != (expiresAt is null))
            throw new ArgumentException("Offline agents have no presence expiry; other statuses have one.", nameof(expiresAt));
        Status = status;
        ChangedAt = RoutingText.Instant(changedAt);
        LastHeartbeatAt = RoutingText.Instant(lastHeartbeatAt);
        ExpiresAt = RoutingText.Instant(expiresAt);
    }

    public AgentStatus Status { get; }
    public DateTimeOffset ChangedAt { get; }
    public DateTimeOffset? LastHeartbeatAt { get; }
    public DateTimeOffset? ExpiresAt { get; }

    public static AgentPresence Offline(DateTimeOffset at) => new(AgentStatus.Offline, at, null, null);

    /// <summary>The status at <paramref name="now"/>: <see cref="AgentStatus.Offline"/> once presence has lapsed.</summary>
    public AgentStatus EffectiveStatus(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now ? AgentStatus.Offline : Status;
}

/// <summary>
/// An agent's stored routing profile. <see cref="ReassignmentPending"/> is set when the agent goes offline and cleared once the
/// maintenance worker has applied the offline reassignment policies. <see cref="Revision"/> fences settings updates; status changes
/// increment it too, heartbeats do not.
/// </summary>
public sealed record StoredAgentProfile(TicketScope Scope, AgentRoutingSettings Settings, AgentPresence Presence, bool ReassignmentPending, long Revision)
{
    public UserId Agent => Settings.Agent;
}

/// <summary>A leased claim of an agent's pending offline reassignment.</summary>
public sealed record AgentReassignmentClaim(TicketScope Scope, UserId Agent, long Fence, DateTimeOffset LeaseUntil);

/// <summary>An agent whose presence lapsed and was recorded offline.</summary>
public sealed record AgentPresenceExpired(TicketScope Scope, UserId Agent, DateTimeOffset At);

/// <summary>
/// Filters for <see cref="IAgentAvailabilityStore.ListAvailableAsync"/>: effectively Online agents with a positive capacity for
/// <see cref="Channel"/>, optionally restricted to <see cref="Agents"/> (for example a team's members, ≤ 1000) and holding every
/// <see cref="RequiredSkills"/> entry at or above its level; one keyset page after <see cref="After"/> of at most <see cref="Limit"/>.
/// </summary>
public sealed record AvailableAgentQuery
{
    public const int MaximumAgents = 1000;

    public AvailableAgentQuery(ChannelKind channel, IEnumerable<UserId>? agents = null, IReadOnlyDictionary<string, int>? requiredSkills = null, UserId? after = null,
        int limit = IAgentAvailabilityStore.MaximumAvailableList)
    {
        if (!channel.IsInitialized || after == default(UserId))
            throw new ArgumentException("A channel kind is required; the cursor must be initialized when supplied.", !channel.IsInitialized ? nameof(channel) : nameof(after));
        if (agents is not null)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var agent in agents)
            {
                if (agent == default)
                    throw new ArgumentException("Agents must be initialized.", nameof(agents));
                if (set.Add(agent.Value) && set.Count > MaximumAgents)
                    throw new ArgumentException($"At most {MaximumAgents} agents can be listed.", nameof(agents));
            }
            Agents = [.. set.Select(value => new UserId(value))];
        }
        var skills = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, level) in requiredSkills ?? new Dictionary<string, int>(StringComparer.Ordinal))
        {
            AgentSkill.RequireName(name, nameof(requiredSkills));
            if (level is < AgentSkill.MinimumLevel or > AgentSkill.MaximumLevel || skills.Count >= AgentRoutingSettings.MaximumSkills)
                throw new ArgumentException("Required skills are bounded and levels are 1-10.", nameof(requiredSkills));
            skills[name] = level;
        }
        RequiredSkills = skills.AsReadOnly();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAgentAvailabilityStore.MaximumAvailableList);
        Channel = channel;
        After = after;
        Limit = limit;
    }

    private AvailableAgentQuery(ChannelKind channel, IReadOnlyList<UserId>? agents, IReadOnlyDictionary<string, int> requiredSkills, UserId? after, int limit)
    {
        if (after == default(UserId))
            throw new ArgumentException("The cursor must be initialized when supplied.", nameof(after));
        Channel = channel;
        Agents = agents;
        RequiredSkills = requiredSkills;
        After = after;
        Limit = limit;
    }

    public ChannelKind Channel { get; }

    /// <summary>Allowed agents in identifier order, or <see langword="null"/> for any agent.</summary>
    public IReadOnlyList<UserId>? Agents { get; }

    public IReadOnlyDictionary<string, int> RequiredSkills { get; }
    public UserId? After { get; }
    public int Limit { get; }

    public AvailableAgentQuery WithAfter(UserId after) => new(Channel, Agents, RequiredSkills, after, Limit);

    /// <summary>True when <paramref name="profile"/> matches every filter except availability and the cursor.</summary>
    public bool Matches(StoredAgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = profile.Settings;
        return settings.CapacityFor(Channel) > 0 && (Agents is null || ContainsAgent(Agents, settings.Agent)) && settings.HasSkills(RequiredSkills);
    }

    private static bool ContainsAgent(IReadOnlyList<UserId> agents, UserId target)
    {
        int lo = 0, hi = agents.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var cmp = StringComparer.Ordinal.Compare(agents[mid].Value, target.Value);
            if (cmp == 0)
                return true;
            if (cmp < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return false;
    }
}

/// <summary>
/// Resolves team snapshots for many agents at once, for building the <see cref="UserPrincipal"/>s whose Access plans decide routing
/// eligibility. A trusted, unauthorized path like <c>ITeamMembershipResolver</c>; replace both together.
/// </summary>
public interface IRoutingTeamMembershipResolver
{
    /// <summary>Teams per agent (every requested agent is a key; ordered team identifiers, at most 256 each).</summary>
    Task<IReadOnlyDictionary<UserId, IReadOnlyList<TeamId>>> GetTeamsAsync(TicketScope scope, IReadOnlyCollection<UserId> agents, CancellationToken cancellationToken = default);
}
