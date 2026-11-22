using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>The store write was applied; <see cref="Value"/> is the stored state (for deletes, the removed state).</summary>
public sealed record AutomationWriteApplied<T>(T Value);

/// <summary>No record exists for the key in the requested scope. Nothing was written.</summary>
public sealed record AutomationWriteNotFound
{
    private AutomationWriteNotFound()
    {
    }

    public static AutomationWriteNotFound Instance { get; } = new();
}

/// <summary>The expected revision did not match, or a create found an existing record. Nothing was written.</summary>
public sealed record AutomationWriteConflict(long CurrentRevision);

public enum AutomationWriteRejection
{
    /// <summary>A per-workspace capacity would be exceeded.</summary>
    CapacityExceeded = 1,

    /// <summary>Another canned response in the workspace already uses the shortcut.</summary>
    DuplicateShortcut = 2,
}

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record AutomationWriteRejected(AutomationWriteRejection Reason);

/// <summary>Outcome of an automation store write. Switch over every case without a discard arm.</summary>
public union AutomationWriteResult<T>(AutomationWriteApplied<T>, AutomationWriteNotFound, AutomationWriteConflict, AutomationWriteRejected);

/// <summary>
/// A leased claim of one due scheduled rule. <see cref="Fence"/> increases with every claim and every rule write;
/// <see cref="IAutomationRuleStore.CompleteScheduleAsync"/> and <see cref="IAutomationRuleStore.ReleaseScheduleAsync"/> succeed only with the
/// current fence while the lease is held.
/// </summary>
public sealed record AutomationScheduleClaim(TicketScope Scope, AutomationRuleId Rule, DateTimeOffset DueAt, long Fence, DateTimeOffset LeaseUntil)
{
    public override string ToString() => $"AutomationScheduleClaim {{ Rule = {Rule}, Fence = {Fence} }}";
}

/// <summary>
/// Workspace-scoped automation rules with their schedule timers. Contract:
/// <list type="bullet">
/// <item>Scope-confined; creates start at revision 1 (conflict when present); updates and deletes are compare-and-set on revision; at
/// most <see cref="MaximumRulesPerWorkspace"/> rules per scope, checked atomically with the insert.</item>
/// <item><see cref="ListAsync"/> orders by <see cref="AutomationRule.Priority"/>, then identifier (ordinal).</item>
/// <item><see cref="GetRuleSetVersionAsync"/> returns a per-scope version that increases with every applied create, update and delete
/// (0 before the first write), so callers can cache the decoded rule set and revalidate it with one cheap read.</item>
/// <item>Create and update atomically set the rule's schedule timer to <c>nextRunAt</c> (<see langword="null"/> = none), clear its lease
/// and increment its fence; delete removes it.</item>
/// <item><see cref="ClaimDueSchedulesAsync"/> leases timers due at or before the store clock's now, earliest first, whose lease is free or
/// expired, incrementing each fence; concurrent claimers never receive the same timer under a live lease.</item>
/// <item><see cref="CompleteScheduleAsync"/> sets the next run and clears the lease; <see cref="ReleaseScheduleAsync"/> reschedules to
/// <c>retryAt</c>; both return <see langword="false"/> when the fence is stale or the lease expired.</item>
/// </list>
/// </summary>
public interface IAutomationRuleStore
{
    public const int MaximumRulesPerWorkspace = 256;
    public const int MaximumClaim = 100;
    public static readonly TimeSpan MaximumLease = TimeSpan.FromHours(1);

    Task<StoredAutomationRule?> GetAsync(TicketScope scope, AutomationRuleId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StoredAutomationRule>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<long> GetRuleSetVersionAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredAutomationRule>> CreateAsync(TicketScope scope, AutomationRule rule, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredAutomationRule>> UpdateAsync(TicketScope scope, AutomationRule rule, long expectedRevision, DateTimeOffset? nextRunAt,
        CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredAutomationRule>> DeleteAsync(TicketScope scope, AutomationRuleId id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationScheduleClaim>> ClaimDueSchedulesAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default);

    Task<bool> CompleteScheduleAsync(AutomationScheduleClaim claim, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default);

    Task<bool> ReleaseScheduleAsync(AutomationScheduleClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default);
}

/// <summary>A new execution record was created.</summary>
public sealed record AutomationExecutionStarted(StoredAutomationExecution Stored);

/// <summary>A record for the same (rule, conversation, trigger key) already exists; nothing was written.</summary>
public sealed record AutomationExecutionExists(StoredAutomationExecution Stored);

/// <summary>Outcome of <see cref="IAutomationExecutionStore.BeginAsync"/>. Switch over every case without a discard arm.</summary>
public union AutomationBeginResult(AutomationExecutionStarted, AutomationExecutionExists);

/// <summary>Keyset position of the execution log: start instant and identifier (both descending).</summary>
public sealed record AutomationExecutionCursor(DateTimeOffset StartedAt, Guid Execution);

/// <summary>A bounded execution log query ordered by start instant descending, then identifier descending. Every supplied criterion must match.</summary>
public sealed record AutomationExecutionQuery
{
    public const int MaximumLimit = 100;
    public const int DefaultLimit = 25;

    public AutomationExecutionQuery(AutomationRuleId? rule = null, ConversationId? conversation = null, DateTimeOffset? from = null, DateTimeOffset? to = null,
        int limit = DefaultLimit, AutomationExecutionCursor? after = null)
    {
        if (rule is { IsInitialized: false } || conversation == default(ConversationId))
            throw new ArgumentException("Criteria must be initialized when supplied.");
        if (limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limits are 1-{MaximumLimit}.");
        if (from is { } start && to is { } end && end < start)
            throw new ArgumentException("The range must not end before it starts.", nameof(to));
        Rule = rule;
        Conversation = conversation;
        From = AutomationText.Instant(from);
        To = AutomationText.Instant(to);
        Limit = limit;
        After = after;
    }

    public AutomationRuleId? Rule { get; }
    public ConversationId? Conversation { get; }

    /// <summary>Inclusive lower bound on <see cref="AutomationExecution.StartedAt"/>.</summary>
    public DateTimeOffset? From { get; }

    /// <summary>Exclusive upper bound on <see cref="AutomationExecution.StartedAt"/>.</summary>
    public DateTimeOffset? To { get; }

    public int Limit { get; }
    public AutomationExecutionCursor? After { get; }

    public AutomationExecutionQuery WithAfter(AutomationExecutionCursor? after) => new(Rule, Conversation, From, To, Limit, after);

    public bool Matches(AutomationExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return (Rule is null || execution.Rule == Rule) && (Conversation is null || execution.Conversation == Conversation)
            && (From is null || execution.StartedAt >= From) && (To is null || execution.StartedAt < To)
            && (After is not { } cursor || execution.StartedAt < cursor.StartedAt || (execution.StartedAt == cursor.StartedAt && execution.Id.CompareTo(cursor.Execution) < 0));
    }
}

/// <summary>One page of the execution log; <see cref="Next"/> is <see langword="null"/> at the end.</summary>
public sealed record AutomationExecutionPage(IReadOnlyList<StoredAutomationExecution> Executions, AutomationExecutionCursor? Next);

/// <summary>
/// The durable execution log and loop-prevention state. Contract:
/// <list type="bullet">
/// <item>Scope-confined. <see cref="BeginAsync"/> creates a record at revision 1 unless one exists for the same (rule, conversation,
/// trigger key), atomically (concurrent beginners: exactly one <see cref="AutomationExecutionStarted"/>).</item>
/// <item><see cref="UpdateAsync"/> is compare-and-set on revision and, atomically, records every
/// <see cref="AutomationExecution.ProducedSequences"/> in the cause index (<see cref="FindCauseAsync"/>).</item>
/// <item><see cref="CountSinceAsync"/> counts records of a conversation started at or after an instant, up to <c>limit</c>.</item>
/// <item><see cref="ListAsync"/> pages the log with the query applied inside the bounded keyset scan.</item>
/// <item>Checkpoints are named, scope-confined counters written compare-and-set (<see langword="null"/> expected = absent).</item>
/// <item><see cref="FindManyAsync"/> returns, in one round trip, the records of up to <see cref="MaximumFindMany"/> rules for one
/// (conversation, trigger key).</item>
/// <item><see cref="PurgeAsync"/> (retention) deletes, store-wide, up to <c>limit</c> finished (completed or suppressed) records started
/// before a cutoff together with their cause-index rows; concurrent purgers never block on each other (skip locked rows). Running
/// records are kept.</item>
/// </list>
/// </summary>
public interface IAutomationExecutionStore
{
    public const int MaximumFindMany = IAutomationRuleStore.MaximumRulesPerWorkspace;
    public const int MaximumPurge = 10_000;

    Task<IReadOnlyDictionary<AutomationRuleId, StoredAutomationExecution>> FindManyAsync(TicketScope scope, ConversationId? conversation, string triggerKey,
        IReadOnlyCollection<AutomationRuleId> rules, CancellationToken cancellationToken = default);

    Task<int> PurgeAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken = default);

    Task<AutomationBeginResult> BeginAsync(TicketScope scope, AutomationExecution execution, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredAutomationExecution>> UpdateAsync(TicketScope scope, AutomationExecution execution, long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<StoredAutomationExecution?> GetAsync(TicketScope scope, Guid execution, CancellationToken cancellationToken = default);

    Task<StoredAutomationExecution?> FindAsync(TicketScope scope, AutomationRuleId rule, ConversationId? conversation, string triggerKey, CancellationToken cancellationToken = default);

    Task<AutomationCause?> FindCauseAsync(TicketScope scope, ConversationId conversation, long sequence, CancellationToken cancellationToken = default);

    Task<int> CountSinceAsync(TicketScope scope, ConversationId conversation, DateTimeOffset since, int limit, CancellationToken cancellationToken = default);

    Task<AutomationExecutionPage> ListAsync(TicketScope scope, AutomationExecutionQuery query, CancellationToken cancellationToken = default);

    Task<long?> GetCheckpointAsync(TicketScope scope, string name, CancellationToken cancellationToken = default);

    Task<bool> SetCheckpointAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default);
}

/// <summary>Workspace macros. Scope-confined; create at revision 1; compare-and-set update/delete; ≤ <see cref="MaximumMacrosPerWorkspace"/>, atomically.</summary>
public interface IMacroStore
{
    public const int MaximumMacrosPerWorkspace = 1024;

    Task<StoredMacro?> GetAsync(TicketScope scope, MacroId id, CancellationToken cancellationToken = default);

    /// <summary>Macros ordered by identifier (ordinal).</summary>
    Task<IReadOnlyList<StoredMacro>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredMacro>> CreateAsync(TicketScope scope, Macro macro, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredMacro>> UpdateAsync(TicketScope scope, Macro macro, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredMacro>> DeleteAsync(TicketScope scope, MacroId id, long expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>A bounded canned response list ordered by shortcut (ordinal), optionally restricted to a shortcut prefix.</summary>
public sealed record CannedResponseQuery
{
    public const int MaximumLimit = 100;

    public CannedResponseQuery(string? prefix = null, string? afterShortcut = null, int limit = 25)
    {
        if (prefix is { Length: > CannedResponse.MaximumShortcutLength } || prefix is not null && !prefix.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
            throw new ArgumentException("Prefixes use shortcut characters.", nameof(prefix));
        if (afterShortcut is not null)
            AutomationText.RequireToken(afterShortcut, CannedResponse.MaximumShortcutLength, nameof(afterShortcut), "Shortcuts");
        if (limit is < 1 or > MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), $"Limits are 1-{MaximumLimit}.");
        Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
        AfterShortcut = afterShortcut;
        Limit = limit;
    }

    public string? Prefix { get; }
    public string? AfterShortcut { get; }
    public int Limit { get; }

    public bool Matches(CannedResponse response) =>
        (Prefix is null || response.Shortcut.StartsWith(Prefix, StringComparison.Ordinal))
        && (AfterShortcut is null || string.CompareOrdinal(response.Shortcut, AfterShortcut) > 0);
}

public sealed record CannedResponsePage(IReadOnlyList<StoredCannedResponse> Responses, string? NextAfterShortcut);

/// <summary>
/// Workspace canned responses. Scope-confined; create at revision 1; compare-and-set update/delete; shortcuts unique per workspace
/// (<see cref="AutomationWriteRejection.DuplicateShortcut"/>); ≤ <see cref="MaximumResponsesPerWorkspace"/>, atomically.
/// </summary>
public interface ICannedResponseStore
{
    public const int MaximumResponsesPerWorkspace = 1024;

    Task<StoredCannedResponse?> GetAsync(TicketScope scope, CannedResponseId id, CancellationToken cancellationToken = default);

    Task<StoredCannedResponse?> FindByShortcutAsync(TicketScope scope, string shortcut, CancellationToken cancellationToken = default);

    Task<CannedResponsePage> ListAsync(TicketScope scope, CannedResponseQuery query, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredCannedResponse>> CreateAsync(TicketScope scope, CannedResponse response, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredCannedResponse>> UpdateAsync(TicketScope scope, CannedResponse response, long expectedRevision, CancellationToken cancellationToken = default);

    Task<AutomationWriteResult<StoredCannedResponse>> DeleteAsync(TicketScope scope, CannedResponseId id, long expectedRevision, CancellationToken cancellationToken = default);
}
