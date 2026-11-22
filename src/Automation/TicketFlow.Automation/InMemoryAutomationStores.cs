using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>Shared revisioned, scope-confined dictionary for the in-memory automation stores (callers hold <see cref="Gate"/>).</summary>
internal sealed class InMemoryRevisioned<TValue>(int capacityPerScope)
{
    public Lock Gate { get; } = new();

    public Dictionary<(TicketScope Scope, string Id), (TValue Value, long Revision)> Entries { get; } = [];

    public IEnumerable<(string Id, TValue Value, long Revision)> InScope(TicketScope scope) =>
        Entries.Where(pair => pair.Key.Scope == scope).Select(pair => (pair.Key.Id, pair.Value.Value, pair.Value.Revision));

    public AutomationWriteResult<TStored> Create<TStored>(TicketScope scope, string id, TValue value, Func<TValue, long, TStored> wrap, Func<AutomationWriteRejection?>? check = null)
    {
        if (Entries.TryGetValue((scope, id), out var existing))
            return new AutomationWriteConflict(existing.Revision);
        if (Entries.Keys.Count(key => key.Scope == scope) >= capacityPerScope)
            return new AutomationWriteRejected(AutomationWriteRejection.CapacityExceeded);
        if (check?.Invoke() is { } rejection)
            return new AutomationWriteRejected(rejection);
        Entries[(scope, id)] = (value, 1);
        return new AutomationWriteApplied<TStored>(wrap(value, 1));
    }

    public AutomationWriteResult<TStored> Update<TStored>(TicketScope scope, string id, TValue value, long expectedRevision, Func<TValue, long, TStored> wrap,
        Func<AutomationWriteRejection?>? check = null)
    {
        if (!Entries.TryGetValue((scope, id), out var existing))
            return AutomationWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new AutomationWriteConflict(existing.Revision);
        if (check?.Invoke() is { } rejection)
            return new AutomationWriteRejected(rejection);
        Entries[(scope, id)] = (value, existing.Revision + 1);
        return new AutomationWriteApplied<TStored>(wrap(value, existing.Revision + 1));
    }

    public AutomationWriteResult<TStored> Delete<TStored>(TicketScope scope, string id, long expectedRevision, Func<TValue, long, TStored> wrap)
    {
        if (!Entries.TryGetValue((scope, id), out var existing))
            return AutomationWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new AutomationWriteConflict(existing.Revision);
        Entries.Remove((scope, id));
        return new AutomationWriteApplied<TStored>(wrap(existing.Value, existing.Revision));
    }
}

/// <summary>Development-only, process-local rule store with schedule timers. Not durable; lease times come from the supplied <see cref="TimeProvider"/>.</summary>
public sealed class InMemoryAutomationRuleStore(TimeProvider? time = null) : IAutomationRuleStore
{
    private readonly InMemoryRevisioned<AutomationRule> rules = new(IAutomationRuleStore.MaximumRulesPerWorkspace);
    private readonly Dictionary<(TicketScope Scope, string Id), (DateTimeOffset? DueAt, DateTimeOffset? LeaseUntil, long Fence)> timers = [];
    private readonly Dictionary<TicketScope, long> versions = [];
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public Task<long> GetRuleSetVersionAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (rules.Gate)
            return Task.FromResult(versions.GetValueOrDefault(scope));
    }

    public Task<StoredAutomationRule?> GetAsync(TicketScope scope, AutomationRuleId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (rules.Gate)
            return Task.FromResult(rules.Entries.TryGetValue((scope, id.Value), out var entry) ? new StoredAutomationRule(scope, entry.Value, entry.Revision) : null);
    }

    public Task<IReadOnlyList<StoredAutomationRule>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (rules.Gate)
            return Task.FromResult<IReadOnlyList<StoredAutomationRule>>([.. rules.InScope(scope).OrderBy(entry => entry.Value.Priority).ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .Select(entry => new StoredAutomationRule(scope, entry.Value, entry.Revision))]);
    }

    public Task<AutomationWriteResult<StoredAutomationRule>> CreateAsync(TicketScope scope, AutomationRule rule, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rule);
        cancellationToken.ThrowIfCancellationRequested();
        lock (rules.Gate)
        {
            var result = rules.Create(scope, rule.Id.Value, rule, (value, revision) => new StoredAutomationRule(scope, value, revision));
            if (result.Value is AutomationWriteApplied<StoredAutomationRule>)
            {
                SetTimer(scope, rule.Id.Value, nextRunAt);
                versions[scope] = versions.GetValueOrDefault(scope) + 1;
            }
            return Task.FromResult(result);
        }
    }

    public Task<AutomationWriteResult<StoredAutomationRule>> UpdateAsync(TicketScope scope, AutomationRule rule, long expectedRevision, DateTimeOffset? nextRunAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rule);
        cancellationToken.ThrowIfCancellationRequested();
        lock (rules.Gate)
        {
            var result = rules.Update(scope, rule.Id.Value, rule, expectedRevision, (value, revision) => new StoredAutomationRule(scope, value, revision));
            if (result.Value is AutomationWriteApplied<StoredAutomationRule>)
            {
                SetTimer(scope, rule.Id.Value, nextRunAt);
                versions[scope] = versions.GetValueOrDefault(scope) + 1;
            }
            return Task.FromResult(result);
        }
    }

    public Task<AutomationWriteResult<StoredAutomationRule>> DeleteAsync(TicketScope scope, AutomationRuleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (rules.Gate)
        {
            var result = rules.Delete(scope, id.Value, expectedRevision, (value, revision) => new StoredAutomationRule(scope, value, revision));
            if (result.Value is AutomationWriteApplied<StoredAutomationRule>)
            {
                timers.Remove((scope, id.Value));
                versions[scope] = versions.GetValueOrDefault(scope) + 1;
            }
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<AutomationScheduleClaim>> ClaimDueSchedulesAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAutomationRuleStore.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > IAutomationRuleStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease));
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        lock (rules.Gate)
        {
            var due = timers.Where(pair => pair.Value.DueAt <= now && (pair.Value.LeaseUntil is null || pair.Value.LeaseUntil <= now))
                .OrderBy(pair => pair.Value.DueAt).ThenBy(pair => pair.Key.Id, StringComparer.Ordinal).Take(limit).ToList();
            var claims = new List<AutomationScheduleClaim>();
            foreach (var (key, timer) in due)
            {
                var fence = timer.Fence + 1;
                timers[key] = (timer.DueAt, now + lease, fence);
                claims.Add(new AutomationScheduleClaim(key.Scope, new AutomationRuleId(key.Id), timer.DueAt!.Value, fence, now + lease));
            }
            return Task.FromResult<IReadOnlyList<AutomationScheduleClaim>>(claims);
        }
    }

    public Task<bool> CompleteScheduleAsync(AutomationScheduleClaim claim, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default) =>
        Finish(claim, nextRunAt, cancellationToken);

    public Task<bool> ReleaseScheduleAsync(AutomationScheduleClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default) =>
        Finish(claim, retryAt, cancellationToken);

    private Task<bool> Finish(AutomationScheduleClaim claim, DateTimeOffset? dueAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow();
        lock (rules.Gate)
        {
            var key = (claim.Scope, claim.Rule.Value);
            if (!timers.TryGetValue(key, out var timer) || timer.Fence != claim.Fence || timer.LeaseUntil is not { } until || until <= now)
                return Task.FromResult(false);
            timers[key] = (AutomationTextInstant.Of(dueAt), null, timer.Fence);
            return Task.FromResult(true);
        }
    }

    private void SetTimer(TicketScope scope, string id, DateTimeOffset? dueAt)
    {
        var fence = timers.TryGetValue((scope, id), out var timer) ? timer.Fence + 1 : 0;
        timers[(scope, id)] = (AutomationTextInstant.Of(dueAt), null, fence);
    }
}

internal static class AutomationTextInstant
{
    public static DateTimeOffset? Of(DateTimeOffset? value) => value is { } instant ? new DateTimeOffset(instant.UtcTicks - instant.UtcTicks % 10, TimeSpan.Zero) : null;
}

/// <summary>Development-only, process-local execution log (bounded per scope, oldest finished records evicted first). Not durable.</summary>
public sealed class InMemoryAutomationExecutionStore(int maximumExecutionsPerScope = 100_000) : IAutomationExecutionStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, Guid Id), StoredAutomationExecution> executions = [];
    private readonly Dictionary<(TicketScope Scope, string Rule, string Conversation, string Trigger), Guid> keys = [];
    private readonly Dictionary<(TicketScope Scope, string Conversation, long Sequence), AutomationCause> causes = [];
    private readonly Dictionary<(TicketScope Scope, string Name), long> checkpoints = [];

    public Task<AutomationBeginResult> BeginAsync(TicketScope scope, AutomationExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var key = Key(scope, execution.Rule, execution.Conversation, execution.TriggerKey);
            if (keys.TryGetValue(key, out var existing))
                return Task.FromResult<AutomationBeginResult>(new AutomationExecutionExists(executions[(scope, existing)]));
            if (executions.ContainsKey((scope, execution.Id)))
                throw new InvalidOperationException("The execution identifier is already used.");
            Evict(scope);
            var stored = new StoredAutomationExecution(scope, execution, 1);
            executions[(scope, execution.Id)] = stored;
            keys[key] = execution.Id;
            Index(scope, execution);
            return Task.FromResult<AutomationBeginResult>(new AutomationExecutionStarted(stored));
        }
    }

    public Task<AutomationWriteResult<StoredAutomationExecution>> UpdateAsync(TicketScope scope, AutomationExecution execution, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!executions.TryGetValue((scope, execution.Id), out var existing))
                return Task.FromResult<AutomationWriteResult<StoredAutomationExecution>>(AutomationWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Task.FromResult<AutomationWriteResult<StoredAutomationExecution>>(new AutomationWriteConflict(existing.Revision));
            if (existing.Execution.Rule != execution.Rule || existing.Execution.Conversation != execution.Conversation || existing.Execution.TriggerKey != execution.TriggerKey)
                throw new ArgumentException("An execution's rule, conversation and trigger key are immutable.", nameof(execution));
            var stored = new StoredAutomationExecution(scope, execution, existing.Revision + 1);
            executions[(scope, execution.Id)] = stored;
            Index(scope, execution);
            return Task.FromResult<AutomationWriteResult<StoredAutomationExecution>>(new AutomationWriteApplied<StoredAutomationExecution>(stored));
        }
    }

    public Task<StoredAutomationExecution?> GetAsync(TicketScope scope, Guid execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(executions.GetValueOrDefault((scope, execution)));
    }

    public Task<StoredAutomationExecution?> FindAsync(TicketScope scope, AutomationRuleId rule, ConversationId? conversation, string triggerKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(keys.TryGetValue(Key(scope, rule, conversation, triggerKey), out var id) ? executions[(scope, id)] : null);
    }

    public Task<IReadOnlyDictionary<AutomationRuleId, StoredAutomationExecution>> FindManyAsync(TicketScope scope, ConversationId? conversation, string triggerKey,
        IReadOnlyCollection<AutomationRuleId> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count > IAutomationExecutionStore.MaximumFindMany)
            throw new ArgumentException($"At most {IAutomationExecutionStore.MaximumFindMany} rules.", nameof(rules));
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var found = new Dictionary<AutomationRuleId, StoredAutomationExecution>();
            foreach (var rule in rules)
                if (keys.TryGetValue(Key(scope, rule, conversation, triggerKey), out var id))
                    found[rule] = executions[(scope, id)];
            return Task.FromResult<IReadOnlyDictionary<AutomationRuleId, StoredAutomationExecution>>(found);
        }
    }

    public Task<int> PurgeAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAutomationExecutionStore.MaximumPurge);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var victims = executions.Values.Where(stored => stored.Execution.Status != AutomationExecutionStatus.Running && stored.Execution.StartedAt < before)
                .OrderBy(stored => stored.Execution.StartedAt).Take(limit).ToList();
            foreach (var stored in victims)
                Remove(stored);
            return Task.FromResult(victims.Count);
        }
    }

    public Task<AutomationCause?> FindCauseAsync(TicketScope scope, ConversationId conversation, long sequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(causes.GetValueOrDefault((scope, conversation.Value, sequence)));
    }

    public Task<int> CountSinceAsync(TicketScope scope, ConversationId conversation, DateTimeOffset since, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(executions.Values.Where(stored => stored.Scope == scope && stored.Execution.Conversation == conversation && stored.Execution.StartedAt >= since)
                .Take(limit).Count());
    }

    public Task<AutomationExecutionPage> ListAsync(TicketScope scope, AutomationExecutionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var rows = executions.Values.Where(stored => stored.Scope == scope && query.Matches(stored.Execution))
                .OrderByDescending(stored => stored.Execution.StartedAt).ThenByDescending(stored => stored.Execution.Id).Take(query.Limit + 1).ToList();
            if (rows.Count <= query.Limit)
                return Task.FromResult(new AutomationExecutionPage(rows, null));
            var page = rows[..query.Limit];
            return Task.FromResult(new AutomationExecutionPage(page, new AutomationExecutionCursor(page[^1].Execution.StartedAt, page[^1].Id)));
        }
    }

    public Task<long?> GetCheckpointAsync(TicketScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireName(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult(checkpoints.TryGetValue((scope, name), out var value) ? value : (long?)null);
    }

    public Task<bool> SetCheckpointAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireName(name);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var current = checkpoints.TryGetValue((scope, name), out var stored) ? stored : (long?)null;
            if (current != expected)
                return Task.FromResult(false);
            checkpoints[(scope, name)] = value;
            return Task.FromResult(true);
        }
    }

    internal static void RequireName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!AutomationTextTokens.IsToken(name))
            throw new ArgumentException("Checkpoint names are lowercase tokens of at most 64 characters.", nameof(name));
    }

    private void Index(TicketScope scope, AutomationExecution execution)
    {
        if (execution.Conversation is not { } conversation)
            return;
        foreach (var sequence in execution.ProducedSequences)
            causes[(scope, conversation.Value, sequence)] = new AutomationCause(execution.Id, execution.Rule, execution.Depth);
    }

    private void Evict(TicketScope scope)
    {
        var inScope = executions.Values.Where(stored => stored.Scope == scope).ToList();
        if (inScope.Count < maximumExecutionsPerScope)
            return;
        var oldest = inScope.Where(stored => stored.Execution.Status != AutomationExecutionStatus.Running).OrderBy(stored => stored.Execution.StartedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("The in-memory execution store is full.");
        Remove(oldest);
    }

    private void Remove(StoredAutomationExecution stored)
    {
        executions.Remove((stored.Scope, stored.Id));
        keys.Remove(Key(stored.Scope, stored.Execution.Rule, stored.Execution.Conversation, stored.Execution.TriggerKey));
        if (stored.Execution.Conversation is { } conversation)
            foreach (var sequence in stored.Execution.ProducedSequences)
                if (causes.TryGetValue((stored.Scope, conversation.Value, sequence), out var cause) && cause.Execution == stored.Id)
                    causes.Remove((stored.Scope, conversation.Value, sequence));
    }

    private static (TicketScope, string, string, string) Key(TicketScope scope, AutomationRuleId rule, ConversationId? conversation, string triggerKey) =>
        (scope, rule.Value, conversation?.Value ?? string.Empty, triggerKey);
}

/// <summary>Development-only, process-local macro store. Not durable.</summary>
public sealed class InMemoryMacroStore : IMacroStore
{
    private readonly InMemoryRevisioned<Macro> macros = new(IMacroStore.MaximumMacrosPerWorkspace);

    public Task<StoredMacro?> GetAsync(TicketScope scope, MacroId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (macros.Gate)
            return Task.FromResult(macros.Entries.TryGetValue((scope, id.Value), out var entry) ? new StoredMacro(scope, entry.Value, entry.Revision) : null);
    }

    public Task<IReadOnlyList<StoredMacro>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (macros.Gate)
            return Task.FromResult<IReadOnlyList<StoredMacro>>([.. macros.InScope(scope).OrderBy(entry => entry.Id, StringComparer.Ordinal)
                .Select(entry => new StoredMacro(scope, entry.Value, entry.Revision))]);
    }

    public Task<AutomationWriteResult<StoredMacro>> CreateAsync(TicketScope scope, Macro macro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(macro);
        cancellationToken.ThrowIfCancellationRequested();
        lock (macros.Gate)
            return Task.FromResult(macros.Create(scope, macro.Id.Value, macro, (value, revision) => new StoredMacro(scope, value, revision)));
    }

    public Task<AutomationWriteResult<StoredMacro>> UpdateAsync(TicketScope scope, Macro macro, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(macro);
        cancellationToken.ThrowIfCancellationRequested();
        lock (macros.Gate)
            return Task.FromResult(macros.Update(scope, macro.Id.Value, macro, expectedRevision, (value, revision) => new StoredMacro(scope, value, revision)));
    }

    public Task<AutomationWriteResult<StoredMacro>> DeleteAsync(TicketScope scope, MacroId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (macros.Gate)
            return Task.FromResult(macros.Delete(scope, id.Value, expectedRevision, (value, revision) => new StoredMacro(scope, value, revision)));
    }
}

/// <summary>Development-only, process-local canned response store. Not durable.</summary>
public sealed class InMemoryCannedResponseStore : ICannedResponseStore
{
    private readonly InMemoryRevisioned<CannedResponse> responses = new(ICannedResponseStore.MaximumResponsesPerWorkspace);

    public Task<StoredCannedResponse?> GetAsync(TicketScope scope, CannedResponseId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (responses.Gate)
            return Task.FromResult(responses.Entries.TryGetValue((scope, id.Value), out var entry) ? new StoredCannedResponse(scope, entry.Value, entry.Revision) : null);
    }

    public Task<StoredCannedResponse?> FindByShortcutAsync(TicketScope scope, string shortcut, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(shortcut);
        cancellationToken.ThrowIfCancellationRequested();
        lock (responses.Gate)
            return Task.FromResult(responses.InScope(scope).Where(entry => entry.Value.Shortcut == shortcut)
                .Select(entry => new StoredCannedResponse(scope, entry.Value, entry.Revision)).FirstOrDefault());
    }

    public Task<CannedResponsePage> ListAsync(TicketScope scope, CannedResponseQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        lock (responses.Gate)
        {
            var rows = responses.InScope(scope).Where(entry => query.Matches(entry.Value)).OrderBy(entry => entry.Value.Shortcut, StringComparer.Ordinal).Take(query.Limit + 1)
                .Select(entry => new StoredCannedResponse(scope, entry.Value, entry.Revision)).ToList();
            return Task.FromResult(rows.Count <= query.Limit
                ? new CannedResponsePage(rows, null)
                : new CannedResponsePage(rows[..query.Limit], rows[query.Limit - 1].Response.Shortcut));
        }
    }

    public Task<AutomationWriteResult<StoredCannedResponse>> CreateAsync(TicketScope scope, CannedResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        lock (responses.Gate)
            return Task.FromResult(responses.Create(scope, response.Id.Value, response, (value, revision) => new StoredCannedResponse(scope, value, revision),
                () => ShortcutTaken(scope, response) ? AutomationWriteRejection.DuplicateShortcut : null));
    }

    public Task<AutomationWriteResult<StoredCannedResponse>> UpdateAsync(TicketScope scope, CannedResponse response, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        lock (responses.Gate)
            return Task.FromResult(responses.Update(scope, response.Id.Value, response, expectedRevision, (value, revision) => new StoredCannedResponse(scope, value, revision),
                () => ShortcutTaken(scope, response) ? AutomationWriteRejection.DuplicateShortcut : null));
    }

    public Task<AutomationWriteResult<StoredCannedResponse>> DeleteAsync(TicketScope scope, CannedResponseId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();
        lock (responses.Gate)
            return Task.FromResult(responses.Delete(scope, id.Value, expectedRevision, (value, revision) => new StoredCannedResponse(scope, value, revision)));
    }

    private bool ShortcutTaken(TicketScope scope, CannedResponse response) =>
        responses.InScope(scope).Any(entry => entry.Id != response.Id.Value && entry.Value.Shortcut == response.Shortcut);
}
