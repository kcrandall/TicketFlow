using System.Collections.Concurrent;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>
/// Process-wide cache of decoded rule sets per scope (bounded to <see cref="MaximumScopes"/> scopes), revalidated on every use with
/// <see cref="IAutomationRuleStore.GetRuleSetVersionAsync"/>: one cheap read per outbox change instead of listing and decoding every
/// rule. A stale entry is replaced as soon as the store's version moves.
/// </summary>
public sealed class AutomationRuleCache
{
    public const int MaximumScopes = 4096;

    private readonly ConcurrentDictionary<TicketScope, (long Version, IReadOnlyList<AutomationRule> Rules)> entries = new();

    public async Task<IReadOnlyList<AutomationRule>> GetAsync(IAutomationRuleStore store, TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scope);
        var version = await store.GetRuleSetVersionAsync(scope, cancellationToken).ConfigureAwait(false);
        if (entries.TryGetValue(scope, out var cached) && cached.Version == version)
            return cached.Rules;
        // The list may already reflect writes after `version`; the next use then sees a newer version and reloads.
        IReadOnlyList<AutomationRule> rules = [.. (await store.ListAsync(scope, cancellationToken).ConfigureAwait(false)).Select(stored => stored.Rule)];
        if (entries.Count >= MaximumScopes && !entries.ContainsKey(scope))
            entries.Clear();
        entries[scope] = (version, rules);
        return rules;
    }
}
