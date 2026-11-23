using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Workspaces;

/// <summary>
/// Development-only workspace settings store. State is process-local, lost on restart and never shared between processes; it is an
/// example of the <see cref="IWorkspaceSettingsStore"/> contract, not durable storage. Writes are serialized under one lock.
/// </summary>
public sealed class InMemoryWorkspaceSettingsStore(int maximumEntries = 10_000) : IWorkspaceSettingsStore, IWorkspaceDirectory
{
    private const string Key = "settings";
    private readonly InMemoryWorkspaceTable<WorkspaceSettings> table = new(maximumEntries, 1);

    public Task<StoredWorkspaceSettings?> GetAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult(table.Get(scope, Key) is { } entry ? Project(scope, entry) : null);
    }

    public Task<StoreWriteResult<StoredWorkspaceSettings, WorkspaceWriteRejection>> CreateAsync(TicketScope scope, WorkspaceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        return Task.FromResult(table.Create(scope, Key, settings, entry => Project(scope, entry)));
    }

    public Task<StoreWriteResult<StoredWorkspaceSettings, WorkspaceWriteRejection>> UpdateAsync(TicketScope scope, WorkspaceSettings settings, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Update(scope, Key, expectedRevision, settings, entry => Project(scope, entry)));
    }

    /// <summary>Scopes ordered by their unit-separated identifiers (ordinal); the token is the last key of the page.</summary>
    public Task<WorkspaceScopePage> ListWorkspacesAsync(string? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IWorkspaceDirectory.MaximumPage);
        var ordered = table.Scopes().Select(scope => (Key: string.Join('\u001f', scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value), Scope: scope))
            .Where(item => after is null || string.CompareOrdinal(item.Key, after) > 0).OrderBy(item => item.Key, StringComparer.Ordinal).Take(limit + 1).ToList();
        var page = ordered.Take(limit).ToList();
        return Task.FromResult(new WorkspaceScopePage([.. page.Select(item => item.Scope)], ordered.Count > limit ? page[^1].Key : null));
    }

    private static StoredWorkspaceSettings Project(TicketScope scope, InMemoryWorkspaceTable<WorkspaceSettings>.Entry entry) => new(scope, entry.Value, entry.Revision);
}

/// <summary>Development-only inbox store with the same non-durable, single-lock semantics as <see cref="InMemoryWorkspaceSettingsStore"/>.</summary>
public sealed class InMemoryInboxStore(int maximumEntries = 10_000) : IInboxStore
{
    private readonly InMemoryWorkspaceTable<Inbox> table = new(maximumEntries, WorkspaceLimits.MaximumInboxesPerWorkspace);

    public Task<StoredInbox?> GetAsync(TicketScope scope, InboxId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult(table.Get(scope, Key(id)) is { } entry ? Project(scope, entry) : null);
    }

    public Task<WorkspacePage<StoredInbox>> ListAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        var (items, more) = table.Page(scope, page, entry => AccessFilterEvaluator.Matches(filter, WorkspaceResources.Inbox(scope, entry.Value.Id)));
        return Task.FromResult(new WorkspacePage<StoredInbox>([.. items.Select(entry => Project(scope, entry))], more));
    }

    public Task<StoreWriteResult<StoredInbox, WorkspaceWriteRejection>> CreateAsync(TicketScope scope, Inbox inbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inbox);
        return Task.FromResult(table.Create(scope, Key(inbox.Id), inbox, entry => Project(scope, entry), others => CheckBinding(inbox, others)));
    }

    public Task<StoreWriteResult<StoredInbox, WorkspaceWriteRejection>> UpdateAsync(TicketScope scope, Inbox inbox, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inbox);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Update(scope, Key(inbox.Id), expectedRevision, inbox, entry => Project(scope, entry), others => CheckBinding(inbox, others)));
    }

    public Task<StoreWriteResult<StoredInbox, WorkspaceWriteRejection>> DeleteAsync(TicketScope scope, InboxId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Delete(scope, Key(id), expectedRevision, entry => Project(scope, entry)));
    }

    private static WorkspaceWriteRejection? CheckBinding(Inbox inbox, IEnumerable<InMemoryWorkspaceTable<Inbox>.Entry> others) =>
        others.Any(other => other.Value.Channel == inbox.Channel) ? WorkspaceWriteRejection.ChannelBindingInUse : null;

    private static string Key(InboxId id) => id == default ? throw new ArgumentException("An inbox identifier is required.", nameof(id)) : id.Value;

    private static StoredInbox Project(TicketScope scope, InMemoryWorkspaceTable<Inbox>.Entry entry) => new(scope, entry.Value, entry.Revision);
}

/// <summary>
/// Development-only team store with the same non-durable, single-lock semantics as <see cref="InMemoryWorkspaceSettingsStore"/>.
/// The per-user team limit is checked under the same lock as the write.
/// </summary>
public sealed class InMemoryTeamStore(int maximumEntries = 10_000) : ITeamStore
{
    private readonly InMemoryWorkspaceTable<Team> table = new(maximumEntries, WorkspaceLimits.MaximumTeamsPerWorkspace);

    public Task<StoredTeam?> GetAsync(TicketScope scope, TeamId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult(table.Get(scope, Key(id)) is { } entry ? Project(scope, entry) : null);
    }

    public Task<WorkspacePage<TeamSummary>> ListAsync(TicketScope scope, AccessFilter filter, WorkspacePageRequest page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        var (items, more) = table.Page(scope, page, entry => AccessFilterEvaluator.Matches(filter, WorkspaceResources.Team(scope, entry.Value.Id)));
        return Task.FromResult(new WorkspacePage<TeamSummary>(
            [.. items.Select(entry => new TeamSummary(scope, entry.Value.Id, entry.Value.Name, entry.Value.Description, entry.Value.Members.Count, entry.Revision))], more));
    }

    public Task<IReadOnlyList<TeamId>> ListTeamsForUserAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (user == default)
            throw new ArgumentException("A user identifier is required.", nameof(user));
        return Task.FromResult<IReadOnlyList<TeamId>>([.. table.All(scope).Where(entry => entry.Value.Members.Contains(user)).Select(entry => entry.Value.Id)]);
    }

    public Task<IReadOnlyDictionary<UserId, IReadOnlyList<TeamId>>> ListTeamsForUsersAsync(TicketScope scope, IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(users);
        var buckets = new Dictionary<UserId, List<TeamId>>(users.Count);
        foreach (var user in users)
        {
            if (user == default)
                throw new ArgumentException("User identifiers must be initialized.", nameof(users));
            buckets.TryAdd(user, []);
        }
        if (buckets.Count > 0)
        {
            foreach (var entry in table.All(scope))
            {
                var team = entry.Value;
                if (team.Members.Count <= buckets.Count)
                {
                    foreach (var member in team.Members)
                        if (buckets.TryGetValue(member, out var list))
                            list.Add(team.Id);
                }
                else
                {
                    foreach (var (user, list) in buckets)
                        if (team.Members.Contains(user))
                            list.Add(team.Id);
                }
            }
        }
        var result = new Dictionary<UserId, IReadOnlyList<TeamId>>(buckets.Count);
        foreach (var (user, list) in buckets)
            result[user] = list;
        return Task.FromResult<IReadOnlyDictionary<UserId, IReadOnlyList<TeamId>>>(result);
    }

    public Task<StoreWriteResult<StoredTeam, WorkspaceWriteRejection>> CreateAsync(TicketScope scope, Team team, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(team);
        return Task.FromResult(table.Create(scope, Key(team.Id), team, entry => Project(scope, entry), others => CheckMemberships(team, others)));
    }

    public Task<StoreWriteResult<StoredTeam, WorkspaceWriteRejection>> UpdateAsync(TicketScope scope, Team team, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(team);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Update(scope, Key(team.Id), expectedRevision, team, entry => Project(scope, entry), others => CheckMemberships(team, others)));
    }

    public Task<StoreWriteResult<StoredTeam, WorkspaceWriteRejection>> DeleteAsync(TicketScope scope, TeamId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Delete(scope, Key(id), expectedRevision, entry => Project(scope, entry)));
    }

    /// <summary>Every member of <paramref name="team"/> must belong to fewer than the limit of other teams in the scope.</summary>
    private static WorkspaceWriteRejection? CheckMemberships(Team team, IEnumerable<InMemoryWorkspaceTable<Team>.Entry> others)
    {
        if (team.Members.Count == 0)
            return null;
        var counts = new Dictionary<UserId, int>(Math.Min(team.Members.Count, 64));
        foreach (var other in others)
        {
            var otherMembers = other.Value.Members;
            if (otherMembers.Count == 0)
                continue;
            if (team.Members.Count <= otherMembers.Count)
            {
                foreach (var member in team.Members)
                    if (otherMembers.Contains(member) && (counts[member] = counts.GetValueOrDefault(member) + 1) >= WorkspaceLimits.MaximumTeamsPerUser)
                        return WorkspaceWriteRejection.TeamMembershipLimitExceeded;
            }
            else
            {
                foreach (var member in otherMembers)
                    if (team.Members.Contains(member) && (counts[member] = counts.GetValueOrDefault(member) + 1) >= WorkspaceLimits.MaximumTeamsPerUser)
                        return WorkspaceWriteRejection.TeamMembershipLimitExceeded;
            }
        }
        return null;
    }

    private static string Key(TeamId id) => id == default ? throw new ArgumentException("A team identifier is required.", nameof(id)) : id.Value;

    private static StoredTeam Project(TicketScope scope, InMemoryWorkspaceTable<Team>.Entry entry) => new(scope, entry.Value, entry.Revision);
}

/// <summary>Development-only business calendar store with the same non-durable, single-lock semantics as <see cref="InMemoryWorkspaceSettingsStore"/>.</summary>
public sealed class InMemoryBusinessCalendarStore(int maximumEntries = 10_000) : IBusinessCalendarStore
{
    private readonly InMemoryWorkspaceTable<BusinessCalendar> table = new(maximumEntries, WorkspaceLimits.MaximumCalendarsPerWorkspace);

    public Task<StoredBusinessCalendar?> GetAsync(TicketScope scope, BusinessCalendarId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult(table.Get(scope, Key(id)) is { } entry ? Project(scope, entry) : null);
    }

    public Task<IReadOnlyList<StoredBusinessCalendar>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Task.FromResult<IReadOnlyList<StoredBusinessCalendar>>([.. table.All(scope).Select(entry => Project(scope, entry))]);
    }

    public Task<StoreWriteResult<StoredBusinessCalendar, WorkspaceWriteRejection>> CreateAsync(TicketScope scope, BusinessCalendar calendar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(calendar);
        return Task.FromResult(table.Create(scope, Key(calendar.Id), calendar, entry => Project(scope, entry)));
    }

    public Task<StoreWriteResult<StoredBusinessCalendar, WorkspaceWriteRejection>> UpdateAsync(TicketScope scope, BusinessCalendar calendar, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Update(scope, Key(calendar.Id), expectedRevision, calendar, entry => Project(scope, entry)));
    }

    public Task<StoreWriteResult<StoredBusinessCalendar, WorkspaceWriteRejection>> DeleteAsync(TicketScope scope, BusinessCalendarId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        return Task.FromResult(table.Delete(scope, Key(id), expectedRevision, entry => Project(scope, entry)));
    }

    private static string Key(BusinessCalendarId id) => id.IsInitialized ? id.Value : throw new ArgumentException("A calendar identifier is required.", nameof(id));

    private static StoredBusinessCalendar Project(TicketScope scope, InMemoryWorkspaceTable<BusinessCalendar>.Entry entry) => new(scope, entry.Value, entry.Revision);
}

/// <summary>Bounded, single-lock, scope-partitioned table ordered by key (ordinal). Development stores only.</summary>
internal sealed class InMemoryWorkspaceTable<TValue>
    where TValue : class
{
    private readonly Lock gate = new();
    private readonly Dictionary<TicketScope, SortedDictionary<string, Entry>> scopes = [];
    private readonly int maximumEntries;
    private readonly int maximumPerScope;
    private int count;

    public InMemoryWorkspaceTable(int maximumEntries, int maximumPerScope)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        this.maximumEntries = maximumEntries;
        this.maximumPerScope = maximumPerScope;
    }

    public sealed record Entry(TValue Value, long Revision);

    public Entry? Get(TicketScope scope, string key)
    {
        lock (gate)
            return scopes.TryGetValue(scope, out var entries) ? entries.GetValueOrDefault(key) : null;
    }

    /// <summary>Every scope holding at least one entry.</summary>
    public List<TicketScope> Scopes()
    {
        lock (gate)
            return [.. scopes.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key)];
    }

    public List<Entry> All(TicketScope scope)
    {
        lock (gate)
            return scopes.TryGetValue(scope, out var entries) ? [.. entries.Values] : [];
    }

    public (List<Entry> Items, bool HasMore) Page(TicketScope scope, WorkspacePageRequest page, Func<Entry, bool> include)
    {
        var items = new List<Entry>();
        foreach (var (key, entry) in Snapshot(scope))
        {
            if (page.After is not null && string.CompareOrdinal(key, page.After) <= 0)
                continue;
            if (!include(entry))
                continue;
            if (items.Count == page.Limit)
                return (items, true);
            items.Add(entry);
        }
        return (items, false);
    }

    public StoreWriteResult<T, WorkspaceWriteRejection> Create<T>(TicketScope scope, string key, TValue value, Func<Entry, T> project, Func<IEnumerable<Entry>, WorkspaceWriteRejection?>? validate = null)
    {
        lock (gate)
        {
            var entries = Entries(scope);
            if (entries.TryGetValue(key, out var existing))
                return new StoreConflict(existing.Revision);
            if (count >= maximumEntries || entries.Count >= maximumPerScope)
                return new StoreRejected<WorkspaceWriteRejection>(WorkspaceWriteRejection.CapacityExceeded);
            if (validate?.Invoke(entries.Values) is { } rejection)
                return new StoreRejected<WorkspaceWriteRejection>(rejection);
            var created = new Entry(value, 1);
            entries.Add(key, created);
            count++;
            return new StoreApplied<T>(project(created));
        }
    }

    public StoreWriteResult<T, WorkspaceWriteRejection> Update<T>(TicketScope scope, string key, long expectedRevision, TValue value, Func<Entry, T> project, Func<IEnumerable<Entry>, WorkspaceWriteRejection?>? validate = null)
    {
        lock (gate)
        {
            if (!scopes.TryGetValue(scope, out var entries) || !entries.TryGetValue(key, out var existing))
                return StoreNotFound.Instance;
            if (existing.Revision != expectedRevision)
                return new StoreConflict(existing.Revision);
            if (validate?.Invoke(entries.Where(pair => pair.Key != key).Select(pair => pair.Value)) is { } rejection)
                return new StoreRejected<WorkspaceWriteRejection>(rejection);
            var updated = new Entry(value, checked(existing.Revision + 1));
            entries[key] = updated;
            return new StoreApplied<T>(project(updated));
        }
    }

    public StoreWriteResult<T, WorkspaceWriteRejection> Delete<T>(TicketScope scope, string key, long expectedRevision, Func<Entry, T> project)
    {
        lock (gate)
        {
            if (!scopes.TryGetValue(scope, out var entries) || !entries.TryGetValue(key, out var existing))
                return StoreNotFound.Instance;
            if (existing.Revision != expectedRevision)
                return new StoreConflict(existing.Revision);
            entries.Remove(key);
            count--;
            if (entries.Count == 0)
                scopes.Remove(scope);
            return new StoreApplied<T>(project(existing));
        }
    }

    private List<KeyValuePair<string, Entry>> Snapshot(TicketScope scope)
    {
        lock (gate)
            return scopes.TryGetValue(scope, out var entries) ? [.. entries] : [];
    }

    private SortedDictionary<string, Entry> Entries(TicketScope scope)
    {
        if (!scopes.TryGetValue(scope, out var entries))
        {
            entries = new(StringComparer.Ordinal);
            scopes.Add(scope, entries);
        }
        return entries;
    }
}
