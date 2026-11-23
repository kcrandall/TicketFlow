using System.Collections.Concurrent;
using TicketFlow.Core;

namespace TicketFlow.Automation;

internal sealed class CompiledRuleSet
{
    private const int EventKindCount = 10; // AutomationEventKind is 1..9
    private readonly AutomationRule[][] byEventKind;

    public CompiledRuleSet(IReadOnlyList<AutomationRule> rules)
    {
        var sorted = rules is AutomationRule[] arr ? arr : [.. rules];
        Array.Sort(sorted, static (a, b) =>
        {
            var cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Id.Value, b.Id.Value);
        });
        AllRules = sorted;

        var buckets = new List<AutomationRule>?[EventKindCount];
        var hasEventDriven = false;
        for (var i = 0; i < sorted.Length; i++)
        {
            var rule = sorted[i];
            if (!rule.Enabled)
                continue;
            switch (rule.Trigger)
            {
                case ConversationCreatedTrigger:
                    Add(buckets, AutomationEventKind.ConversationCreated, rule);
                    hasEventDriven = true;
                    break;
                case MessageReceivedTrigger:
                    Add(buckets, AutomationEventKind.MessageReceived, rule);
                    hasEventDriven = true;
                    break;
                case MessageSentTrigger:
                    Add(buckets, AutomationEventKind.MessageSent, rule);
                    hasEventDriven = true;
                    break;
                case ConversationUpdatedTrigger:
                    // ConversationUpdatedTrigger fires for both ConversationUpdated and StatusChanged events.
                    Add(buckets, AutomationEventKind.ConversationUpdated, rule);
                    Add(buckets, AutomationEventKind.StatusChanged, rule);
                    hasEventDriven = true;
                    break;
                case StatusChangedTrigger:
                    Add(buckets, AutomationEventKind.StatusChanged, rule);
                    hasEventDriven = true;
                    break;
                case AssignmentChangedTrigger:
                    Add(buckets, AutomationEventKind.AssignmentChanged, rule);
                    hasEventDriven = true;
                    break;
                case SlaEventTrigger:
                    Add(buckets, AutomationEventKind.SlaEvent, rule);
                    hasEventDriven = true;
                    break;
                case ScheduledTrigger:
                    Add(buckets, AutomationEventKind.Scheduled, rule);
                    break;
                case ContactUpdatedTrigger:
                    Add(buckets, AutomationEventKind.ContactUpdated, rule);
                    break;
            }
        }
        HasEventDrivenRules = hasEventDriven;
        byEventKind = new AutomationRule[EventKindCount][];
        for (var i = 0; i < EventKindCount; i++)
            byEventKind[i] = buckets[i] is { } list ? [.. list] : [];
    }

    public IReadOnlyList<AutomationRule> AllRules { get; }
    public bool HasEventDrivenRules { get; }

    public AutomationRule[] ForEvent(AutomationEventKind kind) =>
        (uint)kind < EventKindCount ? byEventKind[(int)kind] : [];

    private static void Add(List<AutomationRule>?[] buckets, AutomationEventKind kind, AutomationRule rule)
    {
        var index = (int)kind;
        (buckets[index] ??= []).Add(rule);
    }
}

/// <summary>
/// Process-wide cache of decoded and event-partitioned rule sets per scope (bounded to <see cref="MaximumScopes"/> scopes with
/// LRU eviction and single-flight reload coalescing), revalidated on every use with
/// <see cref="IAutomationRuleStore.GetRuleSetVersionAsync"/>.
/// </summary>
public sealed class AutomationRuleCache
{
    public const int MaximumScopes = 4096;

    private sealed class CacheEntry(long version, CompiledRuleSet compiled, long stamp)
    {
        public long Version { get; } = version;
        public CompiledRuleSet Compiled { get; } = compiled;
        public long LastAccess = stamp;
    }

    private readonly ConcurrentDictionary<TicketScope, CacheEntry> entries = new();
    private readonly ConcurrentDictionary<(TicketScope Scope, long Version), Task<CompiledRuleSet>> inflight = new();
    private long accessCounter;

    public async Task<IReadOnlyList<AutomationRule>> GetAsync(IAutomationRuleStore store, TicketScope scope, CancellationToken cancellationToken = default) =>
        (await GetCompiledAsync(store, scope, cancellationToken).ConfigureAwait(false)).AllRules;

    internal async Task<CompiledRuleSet> GetCompiledAsync(IAutomationRuleStore store, TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        var version = await store.GetRuleSetVersionAsync(scope, cancellationToken).ConfigureAwait(false);
        if (entries.TryGetValue(scope, out var cached) && cached.Version == version)
        {
            Volatile.Write(ref cached.LastAccess, Interlocked.Increment(ref accessCounter));
            return cached.Compiled;
        }
        // Coalesce concurrent reloads for the same (scope, version) to avoid thundering-herd DB queries.
        var key = (scope, version);
        var task = inflight.GetOrAdd(key, _ => LoadAsync(store, scope, version));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inflight.TryRemove(new KeyValuePair<(TicketScope, long), Task<CompiledRuleSet>>(key, task));
        }
    }

    private async Task<CompiledRuleSet> LoadAsync(IAutomationRuleStore store, TicketScope scope, long version)
    {
        var stored = await store.ListAsync(scope).ConfigureAwait(false);
        var rules = new AutomationRule[stored.Count];
        for (var i = 0; i < stored.Count; i++)
            rules[i] = stored[i].Rule;
        var compiled = new CompiledRuleSet(rules);
        if (entries.Count >= MaximumScopes && !entries.ContainsKey(scope))
            EvictOldestQuarter();
        entries[scope] = new CacheEntry(version, compiled, Interlocked.Increment(ref accessCounter));
        return compiled;
    }

    private void EvictOldestQuarter()
    {
        var snapshot = entries.ToArray();
        if (snapshot.Length < MaximumScopes)
            return;
        Array.Sort(snapshot, static (a, b) => Volatile.Read(ref a.Value.LastAccess).CompareTo(Volatile.Read(ref b.Value.LastAccess)));
        var target = Math.Max(1, snapshot.Length / 4);
        for (var i = 0; i < target; i++)
            entries.TryRemove(snapshot[i].Key, out _);
    }
}
