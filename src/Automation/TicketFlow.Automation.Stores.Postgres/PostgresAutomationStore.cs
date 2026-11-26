using System.Data;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Automation.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IAutomationRuleStore"/>, <see cref="IAutomationExecutionStore"/>,
/// <see cref="IMacroStore"/> and <see cref="ICannedResponseStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables
/// live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized
/// and confined to one full scope (schedule claims are store-wide and return their scope). Capacities are checked under workspace
/// advisory locks; execution records are unique on a <c>tf_key_v1</c> natural key and update the cause index in the same atomic
/// statement; schedule timers are claimed with <c>FOR UPDATE SKIP LOCKED</c> plus a lease and fence. Rows that fail to decode throw.
/// Lease times come from the supplied <see cref="TimeProvider"/>, so every client must use a synchronized clock.
/// </summary>
public sealed partial class PostgresAutomationStore : IAutomationRuleStore, IAutomationExecutionStore, IIncrementalAutomationExecutionStore, IMacroStore, ICannedResponseStore
{
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeValues = "@tenant, @project, @environment, @app, @workspace";
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeWhereE = "e.tenant_id=@tenant AND e.project_id=@project AND e.environment_id=@environment AND e.app_id=@app AND e.workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + ScopeWhere;
    private const string RuleWhere = "rule_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@rule) AND rule_id=@rule AND " + ScopeWhere;
    private const string ExecutionWhere = "execution_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@execution::text) AND execution_id=@execution AND " + ScopeWhere;
    private const string MacroWhere = "macro_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@macro) AND macro_id=@macro AND " + ScopeWhere;
    private const string ResponseWhere = "response_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@response) AND response_id=@response AND " + ScopeWhere;

    private const string CreateRuleSql = $"""
        WITH existing AS (
            SELECT revision FROM tf_automation_rules WHERE {RuleWhere}
        ),
        inserted AS (
            INSERT INTO tf_automation_rules({ScopeColumns}, rule_id, priority, format_version, definition, revision, next_run_at, lease_until, fence)
            SELECT {ScopeValues}, @rule, @priority, 1, @definition, 1, @next, NULL, 0
            WHERE NOT EXISTS (SELECT 1 FROM existing)
              AND (SELECT count(*) FROM tf_automation_rules WHERE {ScopeKeyWhere}) < @max
            RETURNING revision
        ),
        bumped AS (
            INSERT INTO tf_automation_rule_sets({ScopeColumns}, version)
            SELECT {ScopeValues}, 1 FROM inserted
            ON CONFLICT (rule_set_key) DO UPDATE SET version=tf_automation_rule_sets.version+1
        )
        SELECT (SELECT revision FROM inserted), (SELECT revision FROM existing)
        """;

    private const string UpdateRuleSql = $"""
        WITH updated AS (
            UPDATE tf_automation_rules SET priority=@priority, definition=@definition, revision=revision+1, next_run_at=@next, lease_until=NULL, fence=fence+1
            WHERE {RuleWhere} AND revision=@expected
            RETURNING revision
        ),
        bumped AS (
            INSERT INTO tf_automation_rule_sets({ScopeColumns}, version)
            SELECT {ScopeValues}, 1 FROM updated
            ON CONFLICT (rule_set_key) DO UPDATE SET version=tf_automation_rule_sets.version+1
        )
        SELECT revision FROM updated
        """;

    private const string DeleteRuleSql = $"""
        WITH deleted AS (
            DELETE FROM tf_automation_rules WHERE {RuleWhere} AND revision=@expected
            RETURNING rule_id, format_version, definition, revision
        ),
        bumped AS (
            INSERT INTO tf_automation_rule_sets({ScopeColumns}, version)
            SELECT {ScopeValues}, 1 FROM deleted
            ON CONFLICT (rule_set_key) DO UPDATE SET version=tf_automation_rule_sets.version+1
        )
        SELECT rule_id, format_version, definition, revision FROM deleted
        """;

    private readonly NpgsqlDataSource source;
    private readonly TimeProvider time;

    public PostgresAutomationStore(NpgsqlDataSource dataSource, TimeProvider? time = null)
    {
        source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Explicitly applies <see cref="PostgresAutomationMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent). Never called implicitly.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresAutomationMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    // ---- Rules and schedules ----

    public async Task<StoredAutomationRule?> GetAsync(TicketScope scope, AutomationRuleId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT rule_id, format_version, definition, revision FROM tf_automation_rules WHERE {RuleWhere}");
        AddScope(command, scope);
        AddText(command, "rule", id.Value);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRule(reader, scope) : null;
    }

    public async Task<long> GetRuleSetVersionAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT version FROM tf_automation_rule_sets WHERE rule_set_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND {ScopeWhere}");
        AddScope(command, scope);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long? ?? 0;
    }

    public async Task<IReadOnlyList<StoredAutomationRule>> ListAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT rule_id, format_version, definition, revision FROM tf_automation_rules WHERE {ScopeKeyWhere}
            ORDER BY priority, rule_id COLLATE "C" LIMIT {IAutomationRuleStore.MaximumRulesPerWorkspace}
            """);
        AddScope(command, scope);
        var rules = new List<StoredAutomationRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rules.Add(ReadRule(reader, scope));
        return rules;
    }

    public async Task<StoreWriteResult<StoredAutomationRule, AutomationWriteRejection>> CreateAsync(TicketScope scope, AutomationRule rule, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rule);
        var definition = AutomationCodec.SerializeRule(rule);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, "rules", cancellationToken).ConfigureAwait(false);
        long? insertedRevision;
        long? existingRevision;
        await using (var create = new NpgsqlCommand(CreateRuleSql, connection, transaction))
        {
            AddScope(create, scope);
            AddText(create, "rule", rule.Id.Value);
            AddInt(create, "priority", rule.Priority);
            AddText(create, "definition", definition);
            AddInstant(create, "next", nextRunAt);
            AddLong(create, "max", IAutomationRuleStore.MaximumRulesPerWorkspace);
            await using var reader = await create.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            insertedRevision = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(0);
            existingRevision = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(1);
        }
        if (insertedRevision is { } rev)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new StoreApplied<StoredAutomationRule>(new StoredAutomationRule(scope, rule, rev));
        }
        return existingRevision is { } current
            ? new StoreConflict(current)
            : new StoreRejected<AutomationWriteRejection>(AutomationWriteRejection.CapacityExceeded);
    }

    public async Task<StoreWriteResult<StoredAutomationRule, AutomationWriteRejection>> UpdateAsync(TicketScope scope, AutomationRule rule, long expectedRevision, DateTimeOffset? nextRunAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(rule);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(UpdateRuleSql, connection))
        {
            AddScope(command, scope);
            AddText(command, "rule", rule.Id.Value);
            AddInt(command, "priority", rule.Priority);
            AddText(command, "definition", AutomationCodec.SerializeRule(rule));
            AddInstant(command, "next", nextRunAt);
            AddLong(command, "expected", expectedRevision);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new StoreApplied<StoredAutomationRule>(new StoredAutomationRule(scope, rule, revision));
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_rules WHERE {RuleWhere}", scope, "rule", rule.Id.Value, cancellationToken).ConfigureAwait(false) is { } current
            ? new StoreConflict(current)
            : StoreNotFound.Instance;
    }

    public async Task<StoreWriteResult<StoredAutomationRule, AutomationWriteRejection>> DeleteAsync(TicketScope scope, AutomationRuleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireInitialized(id.IsInitialized, nameof(id));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(DeleteRuleSql, connection))
        {
            AddScope(command, scope);
            AddText(command, "rule", id.Value);
            AddLong(command, "expected", expectedRevision);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new StoreApplied<StoredAutomationRule>(ReadRule(reader, scope));
        }
        return await RevisionAsync(connection, null, $"SELECT revision FROM tf_automation_rules WHERE {RuleWhere}", scope, "rule", id.Value, cancellationToken).ConfigureAwait(false) is { } current
            ? new StoreConflict(current)
            : StoreNotFound.Instance;
    }

    public async Task<IReadOnlyList<AutomationScheduleClaim>> ClaimDueSchedulesAsync(int limit, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IAutomationRuleStore.MaximumClaim);
        if (lease <= TimeSpan.Zero || lease > IAutomationRuleStore.MaximumLease)
            throw new ArgumentOutOfRangeException(nameof(lease), "Leases are positive and at most one hour.");
        var now = time.GetUtcNow();
        await using var command = source.CreateCommand("""
            WITH candidates AS (
                SELECT rule_key FROM tf_automation_rules
                WHERE next_run_at <= @now AND (lease_until IS NULL OR lease_until <= @now)
                ORDER BY next_run_at, rule_key LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE tf_automation_rules r SET fence=r.fence+1, lease_until=@until
            FROM candidates c WHERE r.rule_key=c.rule_key
            RETURNING r.tenant_id, r.project_id, r.environment_id, r.app_id, r.workspace_id, r.rule_id, r.next_run_at, r.fence, r.lease_until,
                      r.format_version, r.definition, r.revision
            """);
        AddInstant(command, "now", now);
        AddInstant(command, "until", now + lease);
        AddInt(command, "limit", limit);
        var claims = new List<AutomationScheduleClaim>(Math.Min(limit, 16));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            claims.Add(ReadScheduleClaim(reader));
        if (claims.Count > 1)
            claims.Sort(static (a, b) => a.DueAt.CompareTo(b.DueAt));
        return claims;
    }

    public Task<bool> CompleteScheduleAsync(AutomationScheduleClaim claim, DateTimeOffset? nextRunAt, CancellationToken cancellationToken = default) =>
        FinishScheduleAsync(claim, nextRunAt, cancellationToken);

    public Task<bool> ReleaseScheduleAsync(AutomationScheduleClaim claim, DateTimeOffset retryAt, CancellationToken cancellationToken = default) =>
        FinishScheduleAsync(claim, retryAt, cancellationToken);

    private async Task<bool> FinishScheduleAsync(AutomationScheduleClaim claim, DateTimeOffset? dueAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        await using var command = source.CreateCommand($"UPDATE tf_automation_rules SET next_run_at=@due, lease_until=NULL WHERE {RuleWhere} AND fence=@fence AND lease_until > @now");
        AddScope(command, claim.Scope);
        AddText(command, "rule", claim.Rule.Value);
        AddLong(command, "fence", claim.Fence);
        AddInstant(command, "due", dueAt);
        AddInstant(command, "now", time.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    // ---- Shared helpers ----

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter<string[]>("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            TypedValue = ["ticketflow.automation", table, scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value],
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long?> RevisionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, TicketScope scope, string parameter, string id,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddScope(command, scope);
        AddText(command, parameter, id);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static StoredAutomationRule ReadRule(NpgsqlDataReader reader, TicketScope scope)
    {
        try
        {
            RequireFormat(reader.GetInt16(1));
            return new StoredAutomationRule(scope, AutomationCodec.DeserializeRule(new AutomationRuleId(reader.GetString(0)), reader.GetString(2)), reader.GetInt64(3));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored automation rule is invalid or incompatible.", error);
        }
    }

    private static AutomationScheduleClaim ReadScheduleClaim(NpgsqlDataReader reader)
    {
        try
        {
            var scope = ReadScope(reader, 0);
            var id = new AutomationRuleId(reader.GetString(5));
            var dueAt = reader.GetFieldValue<DateTimeOffset>(6);
            var fence = reader.GetInt64(7);
            var leaseUntil = reader.GetFieldValue<DateTimeOffset>(8);
            RequireFormat(reader.GetInt16(9));
            var snapshot = new StoredAutomationRule(scope, AutomationCodec.DeserializeRule(id, reader.GetString(10)), reader.GetInt64(11));
            return new AutomationScheduleClaim(scope, id, dueAt, fence, leaseUntil) { Snapshot = snapshot };
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException("A stored automation schedule is invalid or incompatible.", error);
        }
    }

    private static TicketScope ReadScope(NpgsqlDataReader reader, int offset) =>
        new(reader.GetString(offset), reader.GetString(offset + 1), reader.GetString(offset + 2), reader.GetString(offset + 3), new WorkspaceId(reader.GetString(offset + 4)));

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(value is not null
            ? new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value }
            : new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(value is { } instant
            ? new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = instant.ToUniversalTime() }
            : new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = DBNull.Value });

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid) { TypedValue = value });

    private static void AddInt(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddLong(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddShort(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddBool(NpgsqlCommand command, string name, bool value) =>
        command.Parameters.Add(new NpgsqlParameter<bool>(name, NpgsqlDbType.Boolean) { TypedValue = value });

    private static string? PrefixUpperBound(string prefix)
    {
        // Next string strictly greater than every string starting with prefix in COLLATE "C" (code point order).
        // Shortcuts are ASCII ([a-z0-9-_.]), so the last char can always be incremented safely.
        var chars = prefix.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] < '\uFFFF')
            {
                chars[i] = (char)(chars[i] + 1);
                return new string(chars, 0, i + 1);
            }
        }
        return null;
    }

    private static void RequireFormat(short version)
    {
        if (version != 1)
            throw new FormatException("Unsupported automation row format version.");
    }

    private static void RequireInitialized(bool initialized, string parameterName)
    {
        if (!initialized)
            throw new ArgumentException("An initialized identifier is required.", parameterName);
    }
}
