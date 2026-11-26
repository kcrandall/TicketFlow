using TicketFlow.Core;

namespace TicketFlow.Events;

/// <summary>
/// One allow-list rule for named, operator-configured sinks (PostHog projects, WalFlow sinks): the tenant is required, the other levels
/// match any value when <see langword="null"/>. Named sinks refuse scopes no rule matches, so one tenant cannot route events into another
/// tenant's analytics project or broker.
/// </summary>
public sealed record EventScopeRule
{
    public EventScopeRule(string tenantId, string? projectId = null, string? environmentId = null, string? appId = null, WorkspaceId? workspaceId = null)
    {
        TenantId = TicketScope.RequireIdentifier(tenantId, nameof(tenantId));
        ProjectId = projectId is null ? null : TicketScope.RequireIdentifier(projectId, nameof(projectId));
        EnvironmentId = environmentId is null ? null : TicketScope.RequireIdentifier(environmentId, nameof(environmentId));
        AppId = appId is null ? null : TicketScope.RequireIdentifier(appId, nameof(appId));
        if (workspaceId == default(WorkspaceId))
            throw new ArgumentException("The workspace identifier must be initialized when supplied.", nameof(workspaceId));
        WorkspaceId = workspaceId;
    }

    public string TenantId { get; }
    public string? ProjectId { get; }
    public string? EnvironmentId { get; }
    public string? AppId { get; }
    public WorkspaceId? WorkspaceId { get; }

    public bool Matches(TicketScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return string.Equals(scope.TenantId, TenantId, StringComparison.Ordinal) && (ProjectId is null || string.Equals(scope.ProjectId, ProjectId, StringComparison.Ordinal)) && (EnvironmentId is null || string.Equals(scope.EnvironmentId, EnvironmentId, StringComparison.Ordinal))
            && (AppId is null || string.Equals(scope.AppId, AppId, StringComparison.Ordinal)) && (WorkspaceId is null || scope.WorkspaceId == WorkspaceId);
    }

    /// <summary>True when any rule matches.</summary>
    public static bool Allows(IEnumerable<EventScopeRule> rules, TicketScope scope)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return rules.Any(rule => rule.Matches(scope));
    }
}

/// <summary>Validated opaque identifier of an event subscription. <c>default</c> is uninitialized.</summary>
public readonly record struct EventSubscriptionId
{
    private readonly string? value;

    public EventSubscriptionId(string value) => this.value = TicketScope.RequireIdentifier(value, nameof(value));

    public string Value => value ?? throw new InvalidOperationException("The EventSubscriptionId is uninitialized.");

    public bool IsInitialized => value is not null;

    public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// Where a subscription delivers: a registered <see cref="IEventSink"/> <see cref="Kind"/> (<c>webhook</c>, <c>in-process</c>,
/// <c>posthog</c>, <c>walflow</c>, …) and a sink-specific <see cref="Target"/> (an https URL, a subscriber or sink name) of at most 2048
/// printable characters. Secrets never live in targets.
/// </summary>
public sealed record EventSinkRef
{
    public const int MaximumKindLength = 64;
    public const int MaximumTargetLength = 2048;

    public EventSinkRef(string kind, string target)
    {
        Kind = EventText.RequireToken(kind, MaximumKindLength, nameof(kind));
        ArgumentNullException.ThrowIfNull(target);
        if (target.Length is 0 or > MaximumTargetLength || target.Any(character => character is < '!' or > '~'))
            throw new ArgumentException($"Sink targets are 1-{MaximumTargetLength} printable ASCII characters without spaces.", nameof(target));
        Target = target;
    }

    public string Kind { get; }
    public string Target { get; }
}

/// <summary>
/// Which events a subscription receives: types matching any pattern and, when <see cref="Inboxes"/> or <see cref="Teams"/> is non-empty,
/// only events whose resource has a listed inbox (respectively team) — events without one (for example contact events) are excluded.
/// </summary>
public sealed record EventSubscriptionFilter
{
    public const int MaximumPatterns = 32;
    public const int MaximumIdentifiers = 64;

    public EventSubscriptionFilter(IEnumerable<EventTypePattern> types, IEnumerable<InboxId>? inboxes = null, IEnumerable<TeamId>? teams = null)
    {
        ArgumentNullException.ThrowIfNull(types);
        Types = [.. types.Distinct().OrderBy(pattern => pattern.Value, StringComparer.Ordinal)];
        Inboxes = [.. (inboxes ?? []).Distinct().OrderBy(inbox => inbox.Value, StringComparer.Ordinal)];
        Teams = [.. (teams ?? []).Distinct().OrderBy(team => team.Value, StringComparer.Ordinal)];
        if (Types.Count is 0 or > MaximumPatterns || Types.Any(pattern => !pattern.IsInitialized))
            throw new ArgumentException($"A filter has 1-{MaximumPatterns} initialized type patterns.", nameof(types));
        if (Inboxes.Count > MaximumIdentifiers || Teams.Count > MaximumIdentifiers || Inboxes.Any(inbox => inbox == default) || Teams.Any(team => team == default))
            throw new ArgumentException($"A filter lists at most {MaximumIdentifiers} initialized inboxes and teams.", Inboxes.Count > MaximumIdentifiers || Inboxes.Any(inbox => inbox == default) ? nameof(inboxes) : nameof(teams));
    }

    /// <summary>Every event type, no inbox or team restriction.</summary>
    public static EventSubscriptionFilter Everything { get; } = new([EventTypePattern.All]);

    public IReadOnlyList<EventTypePattern> Types { get; }
    public IReadOnlyList<InboxId> Inboxes { get; }
    public IReadOnlyList<TeamId> Teams { get; }

    public bool Matches(StoredIntegrationEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var type = stored.Event.Type;
        var matchedType = false;
        for (var i = 0; i < Types.Count; i++)
        {
            if (Types[i].Matches(type))
            {
                matchedType = true;
                break;
            }
        }
        if (!matchedType)
            return false;
        if (Inboxes.Count > 0)
        {
            if (stored.Resource.Inbox is not { } inbox)
                return false;
            var foundInbox = false;
            for (var i = 0; i < Inboxes.Count; i++)
                if (Inboxes[i] == inbox)
                {
                    foundInbox = true;
                    break;
                }
            if (!foundInbox)
                return false;
        }
        if (Teams.Count > 0)
        {
            if (stored.Resource.Team is not { } team)
                return false;
            for (var i = 0; i < Teams.Count; i++)
                if (Teams[i] == team)
                    return true;
            return false;
        }
        return true;
    }

    public bool Equals(EventSubscriptionFilter? other) =>
        other is not null && Types.SequenceEqual(other.Types) && Inboxes.SequenceEqual(other.Inboxes) && Teams.SequenceEqual(other.Teams);

    public override int GetHashCode() => HashCode.Combine(Types.Count, Inboxes.Count, Teams.Count);
}

/// <summary>
/// A workspace subscription. Events are authorized for and (at <see cref="EventPrivacy.Content"/>) hydrated as
/// <see cref="Principal"/>, an application API client that must hold <c>events:read</c> (and <c>events:content</c> for content).
/// <see cref="BatchSize"/> (1-100) bounds events per sink call; the sink may lower it.
/// </summary>
public sealed record EventSubscription
{
    public const int MaximumNameLength = 128;
    public const int MaximumBatchSize = 100;

    public EventSubscription(EventSubscriptionId id, string name, EventSinkRef sink, EventSubscriptionFilter filter, ApiClientId principal,
        EventPrivacy privacy = EventPrivacy.IdsOnly, bool enabled = true, int batchSize = 50)
    {
        if (!id.IsInitialized || principal == default)
            throw new ArgumentException("Subscription and principal identifiers are required.", !id.IsInitialized ? nameof(id) : nameof(principal));
        if (!Enum.IsDefined(privacy))
            throw new ArgumentOutOfRangeException(nameof(privacy));
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaximumBatchSize);
        Id = id;
        Name = EventText.RequireText(name, MaximumNameLength, nameof(name));
        Sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        Principal = principal;
        Privacy = privacy;
        Enabled = enabled;
        BatchSize = batchSize;
    }

    public EventSubscriptionId Id { get; }
    public string Name { get; }
    public EventSinkRef Sink { get; }
    public EventSubscriptionFilter Filter { get; }
    public ApiClientId Principal { get; }
    public EventPrivacy Privacy { get; }
    public bool Enabled { get; }
    public int BatchSize { get; }
}

/// <summary>A subscription with its scope, revision (first is 1) and creation time.</summary>
public sealed record StoredEventSubscription(TicketScope Scope, EventSubscription Subscription, long Revision, DateTimeOffset CreatedAt)
{
    public ApiClientPrincipal PrincipalRef => new(Scope, Subscription.Principal);
}

/// <summary>
/// The delivery position of a subscription: every event with a sequence ≤ <see cref="Cursor"/> was delivered, skipped by its filter or
/// Access, or dead-lettered. <see cref="Attempts"/> counts consecutive failures of the batch after the cursor. <see cref="LastError"/> also
/// records retention gaps (<c>events-pruned-&lt;from&gt;-&lt;to&gt;</c>). <see cref="Generation"/> increases with every replay (it is part of
/// delivery ids). A <see cref="Suspended"/> subscription is not delivered until it is updated or replayed (set after repeated configuration
/// failures such as a principal that lost <c>events:read</c>).
/// </summary>
public sealed record EventDeliveryState(long Cursor, int Attempts, DateTimeOffset AvailableAt, string? LastError, DateTimeOffset? LastDeliveredAt, long Generation = 0,
    bool Suspended = false);

/// <summary>A leased claim of one subscription's delivery. Completion, failure and dead-lettering succeed only with the current fence while the lease is held.</summary>
public sealed record EventDeliveryClaim(StoredEventSubscription Subscription, EventDeliveryState State, long Fence, DateTimeOffset LeaseUntil)
{
    /// <summary>Optional workspace event log head sequence observed at claim time to avoid an extra head query.</summary>
    public long? HeadSequence { get; init; }

    public bool Equals(EventDeliveryClaim? other) =>
        other is not null && Subscription == other.Subscription && State == other.State && Fence == other.Fence && LeaseUntil == other.LeaseUntil;

    public override int GetHashCode() => HashCode.Combine(Subscription, State, Fence, LeaseUntil);
}

/// <summary>An event a subscription gave up on after its retries (or a permanent sink failure). Failure codes never contain payloads.</summary>
public sealed record EventDeadLetter(EventSubscriptionId Subscription, long Sequence, Guid EventId, string Type, int Attempts, string Error, DateTimeOffset DeadLetteredAt);

/// <summary>Where a new subscription starts. Switch over every case without a discard arm.</summary>
public union EventSubscriptionStart(EventStartAtLatest, EventStartAtEarliest, EventStartAfterSequence);

/// <summary>Only events appended after creation (the default).</summary>
public sealed record EventStartAtLatest
{
    private EventStartAtLatest()
    {
    }

    public static EventStartAtLatest Instance { get; } = new();
}

/// <summary>Every retained event.</summary>
public sealed record EventStartAtEarliest
{
    private EventStartAtEarliest()
    {
    }

    public static EventStartAtEarliest Instance { get; } = new();
}

/// <summary>Events with a sequence greater than <see cref="Sequence"/>.</summary>
public sealed record EventStartAfterSequence(long Sequence)
{
    public long Sequence { get; } = Sequence >= 0 ? Sequence : throw new ArgumentOutOfRangeException(nameof(Sequence));
}
