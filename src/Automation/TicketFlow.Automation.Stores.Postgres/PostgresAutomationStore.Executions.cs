using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.Automation.Stores.Postgres;

public sealed partial class PostgresAutomationStore
{
    private const string ExecutionColumns = "execution_id, format_version, state, revision";

    public async Task<AutomationBeginResult> BeginAsync(TicketScope scope, AutomationExecution execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(execution);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_automation_executions({ScopeColumns}, execution_id, rule_id, conversation_id, trigger_key, started_at, format_version, state, revision, finished)
            VALUES({ScopeValues}, @execution, @rule, @conversation, @trigger, @started, 1, @state, 1, @finished)
            ON CONFLICT (natural_key) DO NOTHING
            """, connection))
        {
            AddScope(insert, scope);
            AddExecution(insert, execution);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                if (execution.Conversation is { } conversation)
                    await IndexCausesAsync(connection, null, scope, conversation, execution, cancellationToken).ConfigureAwait(false);
                return new AutomationExecutionStarted(new StoredAutomationExecution(scope, execution, 1));
            }
        }
        var existing = await FindAsync(scope, execution.Rule, execution.Conversation, execution.TriggerKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A conflicting execution record disappeared.");
        return new AutomationExecutionExists(existing);
    }

    public async Task<AutomationWriteResult<StoredAutomationExecution>> UpdateAsync(TicketScope scope, AutomationExecution execution, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(execution);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long? revision;
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_automation_executions SET state=@state, finished=@finished, revision=revision+1
            WHERE {ExecutionWhere} AND revision=@expected AND rule_id=@rule AND conversation_id IS NOT DISTINCT FROM @conversation AND trigger_key=@trigger
            RETURNING revision
            """, connection, transaction))
        {
            AddScope(update, scope);
            AddExecution(update, execution);
            update.Parameters.AddWithValue("expected", expectedRevision);
            revision = await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
        }
        if (revision is null)
        {
            await using var check = new NpgsqlCommand($"SELECT revision FROM tf_automation_executions WHERE {ExecutionWhere}", connection, transaction);
            AddScope(check, scope);
            AddUuid(check, "execution", execution.Id);
            return await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long current
                ? new AutomationWriteConflict(current)
                : AutomationWriteNotFound.Instance;
        }
        if (execution.Conversation is { } conversation)
            await IndexCausesAsync(connection, transaction, scope, conversation, execution, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AutomationWriteApplied<StoredAutomationExecution>(new StoredAutomationExecution(scope, execution, revision.Value));
    }

    public async Task<StoredAutomationExecution?> GetAsync(TicketScope scope, Guid execution, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {ExecutionColumns} FROM tf_automation_executions WHERE {ExecutionWhere}");
        AddScope(command, scope);
        AddUuid(command, "execution", execution);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadExecution(reader, scope) : null;
    }

    public async Task<StoredAutomationExecution?> FindAsync(TicketScope scope, AutomationRuleId rule, ConversationId? conversation, string triggerKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(rule.IsInitialized, nameof(rule));
        AutomationExecution.RequireTriggerKey(triggerKey, nameof(triggerKey));
        await using var command = source.CreateCommand($"""
            SELECT {ExecutionColumns} FROM tf_automation_executions
            WHERE natural_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@rule,COALESCE(@conversation,''),@trigger)
              AND {ScopeWhere} AND rule_id=@rule AND conversation_id IS NOT DISTINCT FROM @conversation AND trigger_key=@trigger
            """);
        AddScope(command, scope);
        AddText(command, "rule", rule.Value);
        AddText(command, "conversation", conversation?.Value);
        AddText(command, "trigger", triggerKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadExecution(reader, scope) : null;
    }

    public async Task<IReadOnlyDictionary<AutomationRuleId, StoredAutomationExecution>> FindManyAsync(TicketScope scope, ConversationId? conversation, string triggerKey,
        IReadOnlyCollection<AutomationRuleId> rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rules);
        AutomationExecution.RequireTriggerKey(triggerKey, nameof(triggerKey));
        if (rules.Count > IAutomationExecutionStore.MaximumFindMany || rules.Any(rule => !rule.IsInitialized))
            throw new ArgumentException($"At most {IAutomationExecutionStore.MaximumFindMany} initialized rules.", nameof(rules));
        var found = new Dictionary<AutomationRuleId, StoredAutomationExecution>();
        if (rules.Count == 0)
            return found;
        await using var command = source.CreateCommand($"""
            SELECT {ExecutionColumns} FROM tf_automation_executions
            WHERE natural_key = ANY (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,r,COALESCE(@conversation,''),@trigger) FROM unnest(@rules) AS r)
              AND {ScopeWhere} AND rule_id = ANY (@rules) AND conversation_id IS NOT DISTINCT FROM @conversation AND trigger_key=@trigger
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation?.Value);
        AddText(command, "trigger", triggerKey);
        command.Parameters.Add(new NpgsqlParameter("rules", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = rules.Select(rule => rule.Value).Distinct().ToArray() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var stored = ReadExecution(reader, scope);
            found[stored.Execution.Rule] = stored;
        }
        return found;
    }

    public async Task<int> PurgeAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAutomationExecutionStore.MaximumPurge);
        // SKIP LOCKED: concurrent purgers take disjoint batches and never wait on rows a runner is updating.
        await using var command = source.CreateCommand("""
            WITH victims AS (
                SELECT execution_key FROM tf_automation_executions WHERE finished AND started_at < @before
                ORDER BY started_at, execution_key LIMIT @limit FOR UPDATE SKIP LOCKED),
            deleted AS (
                DELETE FROM tf_automation_executions e USING victims v WHERE e.execution_key=v.execution_key
                RETURNING e.tenant_id, e.project_id, e.environment_id, e.app_id, e.workspace_id, e.execution_id),
            causes AS (
                DELETE FROM tf_automation_causes c USING deleted d
                WHERE c.execution_id=d.execution_id AND c.tenant_id=d.tenant_id AND c.project_id=d.project_id AND c.environment_id=d.environment_id
                  AND c.app_id=d.app_id AND c.workspace_id=d.workspace_id
                RETURNING 1)
            SELECT count(*) FROM deleted
            """);
        AddInstant(command, "before", before);
        command.Parameters.AddWithValue("limit", limit);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<AutomationCause?> FindCauseAsync(TicketScope scope, ConversationId conversation, long sequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(conversation != default, nameof(conversation));
        await using var command = source.CreateCommand($"""
            SELECT execution_id, rule_id, depth FROM tf_automation_causes
            WHERE cause_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation,@sequence::text) AND {ScopeWhere}
              AND conversation_id=@conversation AND sequence=@sequence
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        command.Parameters.AddWithValue("sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Decode(() => new AutomationCause(reader.GetGuid(0), new AutomationRuleId(reader.GetString(1)), reader.GetInt32(2)), "cause")
            : null;
    }

    public async Task<int> CountSinceAsync(TicketScope scope, ConversationId conversation, DateTimeOffset since, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(conversation != default, nameof(conversation));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var command = source.CreateCommand($"""
            SELECT count(*) FROM (SELECT 1 FROM tf_automation_executions
                WHERE conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND {ScopeWhere} AND conversation_id=@conversation
                  AND started_at >= @since LIMIT @limit) bounded
            """);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddInstant(command, "since", since);
        command.Parameters.AddWithValue("limit", limit);
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<AutomationExecutionPage> ListAsync(TicketScope scope, AutomationExecutionQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        await using var command = source.CreateCommand();
        AddScope(command, scope);
        var criteria = new List<string>();
        if (query.Conversation is { } conversation)
        {
            criteria.Add("conversation_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@conversation) AND conversation_id=@conversation");
            AddText(command, "conversation", conversation.Value);
        }
        else
        {
            criteria.Add("scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace)");
        }
        if (query.Rule is { } rule)
        {
            criteria.Add("rule_id=@rule");
            AddText(command, "rule", rule.Value);
        }
        if (query.From is { } from)
        {
            criteria.Add("started_at >= @from");
            AddInstant(command, "from", from);
        }
        if (query.To is { } to)
        {
            criteria.Add("started_at < @to");
            AddInstant(command, "to", to);
        }
        if (query.After is { } after)
        {
            criteria.Add("(started_at, execution_id) < (@after_at, @after_id)");
            AddInstant(command, "after_at", after.StartedAt);
            AddUuid(command, "after_id", after.Execution);
        }
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        command.CommandText = $"""
            SELECT {ExecutionColumns} FROM tf_automation_executions
            WHERE {ScopeWhere}{string.Concat(criteria.Select(item => " AND " + item))}
            ORDER BY started_at DESC, execution_id DESC LIMIT @limit
            """;
        var rows = new List<StoredAutomationExecution>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                rows.Add(ReadExecution(reader, scope));
        if (rows.Count <= query.Limit)
            return new AutomationExecutionPage(rows, null);
        var page = rows[..query.Limit];
        return new AutomationExecutionPage(page, new AutomationExecutionCursor(page[^1].Execution.StartedAt, page[^1].Id));
    }

    public async Task<long?> GetCheckpointAsync(TicketScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireCheckpoint(name);
        await using var command = source.CreateCommand($"SELECT value FROM tf_automation_checkpoints WHERE checkpoint_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@name) AND {ScopeWhere} AND name=@name");
        AddScope(command, scope);
        AddText(command, "name", name);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    public async Task<bool> SetCheckpointAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireCheckpoint(name);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        await using var command = source.CreateCommand(expected is null
            ? $"INSERT INTO tf_automation_checkpoints({ScopeColumns}, name, value) VALUES({ScopeValues}, @name, @value) ON CONFLICT (checkpoint_key) DO NOTHING"
            : $"UPDATE tf_automation_checkpoints SET value=@value WHERE checkpoint_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@name) AND {ScopeWhere} AND name=@name AND value=@expected");
        AddScope(command, scope);
        AddText(command, "name", name);
        command.Parameters.AddWithValue("value", value);
        if (expected is { } current)
            command.Parameters.AddWithValue("expected", current);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task IndexCausesAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ConversationId conversation, AutomationExecution execution,
        CancellationToken cancellationToken)
    {
        var sequences = execution.ProducedSequences;
        if (sequences.Count == 0)
            return;
        await using var command = new NpgsqlCommand($"""
            INSERT INTO tf_automation_causes({ScopeColumns}, conversation_id, sequence, execution_id, rule_id, depth)
            SELECT {ScopeValues}, @conversation, s, @execution, @rule, @depth FROM unnest(@sequences) AS s
            ON CONFLICT (cause_key) DO UPDATE SET execution_id=EXCLUDED.execution_id, rule_id=EXCLUDED.rule_id, depth=EXCLUDED.depth
            """, connection, transaction);
        AddScope(command, scope);
        AddText(command, "conversation", conversation.Value);
        AddUuid(command, "execution", execution.Id);
        AddText(command, "rule", execution.Rule.Value);
        command.Parameters.AddWithValue("depth", execution.Depth);
        command.Parameters.Add(new NpgsqlParameter("sequences", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) { Value = sequences.ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddExecution(NpgsqlCommand command, AutomationExecution execution)
    {
        AddUuid(command, "execution", execution.Id);
        AddText(command, "rule", execution.Rule.Value);
        AddText(command, "conversation", execution.Conversation?.Value);
        AddText(command, "trigger", execution.TriggerKey);
        AddInstant(command, "started", execution.StartedAt);
        AddText(command, "state", AutomationCodec.SerializeExecution(execution));
        command.Parameters.AddWithValue("finished", execution.Status != AutomationExecutionStatus.Running);
    }

    private static StoredAutomationExecution ReadExecution(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        RequireFormat(reader.GetInt16(1));
        var execution = AutomationCodec.DeserializeExecution(reader.GetString(2));
        if (execution.Id != reader.GetGuid(0))
            throw new FormatException("The execution state belongs to a different record.");
        return new StoredAutomationExecution(scope, execution, reader.GetInt64(3));
    }, "execution");

    private static void RequireCheckpoint(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > 64 || name[0] is not (>= 'a' and <= 'z') || !name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
            throw new ArgumentException("Checkpoint names are lowercase tokens of at most 64 characters.", nameof(name));
    }
}
