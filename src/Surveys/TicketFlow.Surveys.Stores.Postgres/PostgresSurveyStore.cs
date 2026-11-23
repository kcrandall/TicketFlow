using System.Data;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Surveys.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="ISurveyStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables
/// live in the data source's current schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized
/// and every key includes the full scope. Survey writes serialize per workspace and invitation creates per (survey, contact) on
/// transaction-scoped advisory locks, so capacity and cooldown checks cannot be raced; invitation and response writes lock the
/// invitation row. Response lists and aggregates translate the <see cref="Access.AccessFilter"/> with
/// <c>PostgresAccessFilterTranslator</c>; untranslatable filters return no rows.
/// </summary>
public sealed partial class PostgresSurveyStore : ISurveyStore
{
    private const string WorkspaceWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + WorkspaceWhere;
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeParameters = "@tenant, @project, @environment, @app, @workspace";
    private const string SurveyKeyWhere = "survey_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@survey) AND " + WorkspaceWhere + " AND survey_id=@survey";
    private const string SurveyColumns = "survey_id, format_version, name, kind, default_locale, questions::text, trigger, delay_seconds, cooldown_seconds, lifetime_seconds, inbox_id, enabled, revision";

    private readonly NpgsqlDataSource source;

    public PostgresSurveyStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Explicitly applies <see cref="PostgresSurveyMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent).</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresSurveyMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<StoredSurvey?> GetSurveyAsync(TicketScope scope, SurveyId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT {SurveyColumns} FROM tf_survey_definitions WHERE {SurveyKeyWhere}");
        BindSurveyKey(command, scope, id);
        return await ReadSingleAsync(command, reader => ReadSurvey(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredSurvey>> ListSurveysAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {SurveyColumns} FROM tf_survey_definitions WHERE {ScopeKeyWhere} ORDER BY survey_id COLLATE \"C\" LIMIT {SurveyLimits.MaximumSurveysPerWorkspace}");
        AddScope(command, scope);
        return await ReadListAsync(command, reader => ReadSurvey(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredSurvey>> ListEnabledSurveysAsync(TicketScope scope, SurveyTrigger trigger, InboxId? inbox, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand(
            $"SELECT {SurveyColumns} FROM tf_survey_definitions WHERE {ScopeKeyWhere} AND enabled AND trigger=@trigger AND (inbox_id IS NULL OR inbox_id=@inbox) ORDER BY survey_id COLLATE \"C\" LIMIT {SurveyLimits.MaximumSurveysPerWorkspace}");
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter<short>("trigger", NpgsqlDbType.Smallint) { TypedValue = (short)trigger });
        AddText(command, "inbox", inbox?.Value);
        return await ReadListAsync(command, reader => ReadSurvey(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoreWriteResult<StoredSurvey, SurveyWriteRejection>> CreateSurveyAsync(TicketScope scope, SurveyDefinition survey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(survey);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken, "definitions", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value).ConfigureAwait(false);
        if (await SurveyRevisionAsync(connection, transaction, scope, survey.Id, cancellationToken).ConfigureAwait(false) is { } existing)
            return new StoreConflict(existing);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_survey_definitions WHERE {ScopeKeyWhere}", connection, transaction))
        {
            AddScope(count, scope);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= SurveyLimits.MaximumSurveysPerWorkspace)
                return new StoreRejected<SurveyWriteRejection>(SurveyWriteRejection.CapacityExceeded);
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_survey_definitions({ScopeColumns}, survey_id, format_version, name, kind, default_locale, questions, trigger, delay_seconds, cooldown_seconds,
                lifetime_seconds, inbox_id, enabled, revision)
            VALUES({ScopeParameters}, @survey, 1, @name, @kind, @locale, @questions, @trigger, @delay, @cooldown, @lifetime, @inbox, @enabled, 1)
            """, connection, transaction))
        {
            BindSurvey(insert, scope, survey);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<StoredSurvey>(new(scope, survey, 1));
    }

    public async Task<StoreWriteResult<StoredSurvey, SurveyWriteRejection>> UpdateSurveyAsync(TicketScope scope, SurveyDefinition survey, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_survey_definitions SET name=@name, kind=@kind, default_locale=@locale, questions=@questions, trigger=@trigger, delay_seconds=@delay,
                cooldown_seconds=@cooldown, lifetime_seconds=@lifetime, inbox_id=@inbox, enabled=@enabled, revision=revision+1
            WHERE {SurveyKeyWhere} AND revision=@expected RETURNING revision
            """, connection, transaction))
        {
            BindSurvey(update, scope, survey);
            update.Parameters.AddWithValue("expected", expectedRevision);
            if (await update.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StoreApplied<StoredSurvey>(new(scope, survey, revision));
            }
        }
        return Miss<StoredSurvey>(await SurveyRevisionAsync(connection, transaction, scope, survey.Id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<StoreWriteResult<StoredSurvey, SurveyWriteRejection>> DeleteSurveyAsync(TicketScope scope, SurveyId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, cancellationToken, "definitions", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value).ConfigureAwait(false);
        var current = await SurveyRevisionAsync(connection, transaction, scope, id, cancellationToken).ConfigureAwait(false);
        if (current != expectedRevision)
            return Miss<StoredSurvey>(current);
        await using (var used = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM tf_survey_invitations WHERE {SurveyKeyWhere})", connection, transaction))
        {
            BindSurveyKey(used, scope, id);
            if ((bool)(await used.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
                return new StoreRejected<SurveyWriteRejection>(SurveyWriteRejection.InUse);
        }
        StoredSurvey? removed;
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_survey_definitions WHERE {SurveyKeyWhere} RETURNING {SurveyColumns}", connection, transaction))
        {
            BindSurveyKey(delete, scope, id);
            removed = await ReadSingleAsync(delete, reader => ReadSurvey(reader, scope), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<StoredSurvey>(removed!);
    }

    private static async Task<long?> SurveyRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, SurveyId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_survey_definitions WHERE {SurveyKeyWhere}", connection, transaction);
        BindSurveyKey(command, scope, id);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    private static void BindSurveyKey(NpgsqlCommand command, TicketScope scope, SurveyId id)
    {
        AddScope(command, scope);
        AddText(command, "survey", id.Value);
    }

    private static void BindSurvey(NpgsqlCommand command, TicketScope scope, SurveyDefinition survey)
    {
        BindSurveyKey(command, scope, survey.Id);
        AddText(command, "name", survey.Name);
        command.Parameters.AddWithValue("kind", (short)survey.Kind);
        AddText(command, "locale", survey.DefaultLocale.Value);
        command.Parameters.Add(new NpgsqlParameter("questions", NpgsqlDbType.Jsonb) { Value = SurveyJsonCodec.EncodeQuestions(survey.Questions) });
        command.Parameters.AddWithValue("trigger", (short)survey.Policy.Trigger);
        command.Parameters.AddWithValue("delay", (long)survey.Policy.Delay.TotalSeconds);
        command.Parameters.AddWithValue("cooldown", (long)survey.Policy.ContactCooldown.TotalSeconds);
        command.Parameters.AddWithValue("lifetime", (long)survey.Policy.InvitationLifetime.TotalSeconds);
        AddText(command, "inbox", survey.Inbox?.Value);
        command.Parameters.AddWithValue("enabled", survey.Enabled);
    }

    private static StoredSurvey ReadSurvey(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported survey format version.");
        var policy = new SurveyTriggerPolicy(TimeSpan.FromSeconds(reader.GetInt64(7)), TimeSpan.FromSeconds(reader.GetInt64(8)), TimeSpan.FromSeconds(reader.GetInt64(9)), Enum<SurveyTrigger>(reader.GetInt16(6)));
        var survey = new SurveyDefinition(new SurveyId(reader.GetString(0)), reader.GetString(2), Enum<SurveyKind>(reader.GetInt16(3)), new SurveyLocale(reader.GetString(4)),
            SurveyJsonCodec.DecodeQuestions(reader.GetString(5)), policy, reader.IsDBNull(10) ? null : new InboxId(reader.GetString(10)), reader.GetBoolean(11));
        return new StoredSurvey(scope, survey, reader.GetInt64(12));
    }, "survey");

    // ---------------------------------------------------------------- shared helpers

    private static T Enum<T>(short value)
        where T : struct, System.Enum
    {
        int raw = value;
        var result = Unsafe.As<int, T>(ref raw);
        return System.Enum.IsDefined(result) ? result : throw new FormatException($"Unknown {typeof(T).Name} value.");
    }

    private static StoreWriteResult<T, SurveyWriteRejection> Miss<T>(long? current) =>
        current is { } revision ? new StoreConflict(revision) : StoreNotFound.Instance;

    private static void Require(bool initialized, string parameterName)
    {
        if (!initialized)
            throw new ArgumentException("Identifiers must be initialized.", parameterName);
    }

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddTime(NpgsqlCommand command, string name, DateTimeOffset? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value is { } time ? time : DBNull.Value });

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken, params string[] parts)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (string[])["ticketflow.surveys", .. parts] });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T?> ReadSingleAsync<T>(NpgsqlCommand command, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
        where T : class
    {
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? read(reader) : null;
    }

    private static async Task<List<T>> ReadListAsync<T>(NpgsqlCommand command, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(read(reader));
        return rows;
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException($"A stored {description} is invalid or incompatible.", error);
        }
    }
}
