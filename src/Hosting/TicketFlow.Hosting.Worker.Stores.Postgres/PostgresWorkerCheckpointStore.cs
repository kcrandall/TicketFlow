using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Hosting.Worker.Stores.Postgres;

/// <summary>Append-only migration history of the worker checkpoint table. Never edit an applied step; append a new one.</summary>
public static class PostgresWorkerMigrations
{
    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.worker", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_worker_checkpoints (
            tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL,
            name text NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            position bigint NOT NULL CHECK (position >= 0),
            checkpoint_key bytea GENERATED ALWAYS AS (tf_key_v1({ScopeColumns}, name)) STORED NOT NULL,
            PRIMARY KEY (checkpoint_key));
        """)]);
}

/// <summary>
/// Durable <see cref="IWorkerCheckpointStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Keys are <c>tf_key_v1</c> over the
/// raw scope and name (raw columns are also compared); writes are single-statement compare-and-set; every statement is parameterized.
/// Tables are created only by <see cref="InitializeAsync"/>.
/// </summary>
public sealed class PostgresWorkerCheckpointStore(NpgsqlDataSource dataSource) : IWorkerCheckpointStore
{
    private const string KeyWhere = """
        checkpoint_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@name) AND tenant_id=@tenant AND project_id=@project
        AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace AND name=@name
        """;

    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Applies <see cref="PostgresWorkerMigrations.Catalog"/> (advisory-locked, checksum-verified, idempotent). Never called implicitly.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresWorkerMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<long?> GetAsync(TicketScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        WorkerCheckpointNames.Require(name, nameof(name));
        await using var command = source.CreateCommand($"SELECT format_version, position FROM tf_worker_checkpoints WHERE {KeyWhere}");
        Bind(command, scope, name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        if (reader.GetInt16(0) != 1)
            throw new InvalidOperationException("A stored worker checkpoint has an unsupported format version.");
        return reader.GetInt64(1);
    }

    public async Task<bool> SetAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        WorkerCheckpointNames.Require(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        await using var command = source.CreateCommand(expected is null
            ? """
              INSERT INTO tf_worker_checkpoints(tenant_id, project_id, environment_id, app_id, workspace_id, name, format_version, position)
              VALUES(@tenant, @project, @environment, @app, @workspace, @name, 1, @value) ON CONFLICT DO NOTHING
              """
            : $"UPDATE tf_worker_checkpoints SET position=@value WHERE {KeyWhere} AND position=@expected");
        Bind(command, scope, name);
        command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Bigint) { Value = value });
        if (expected is { } current)
            command.Parameters.Add(new NpgsqlParameter("expected", NpgsqlDbType.Bigint) { Value = current });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void Bind(NpgsqlCommand command, TicketScope scope, string name)
    {
        Text(command, "tenant", scope.TenantId);
        Text(command, "project", scope.ProjectId);
        Text(command, "environment", scope.EnvironmentId);
        Text(command, "app", scope.AppId);
        Text(command, "workspace", scope.WorkspaceId.Value);
        Text(command, "name", name);
    }

    private static void Text(NpgsqlCommand command, string parameter, string value) =>
        command.Parameters.Add(new NpgsqlParameter(parameter, NpgsqlDbType.Text) { Value = value });
}

public static class PostgresWorkerTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresWorkerCheckpointStore"/> as <see cref="IWorkerCheckpointStore"/>, replacing only the in-memory default of
    /// <c>AddTicketFlowWorkers()</c> (an application-registered store wins). Schema creation is explicit: call
    /// <see cref="PostgresWorkerCheckpointStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresWorkerCheckpoints(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IWorkerCheckpointStore) && item.ImplementationType == typeof(InMemoryWorkerCheckpointStore)).ToList())
            services.Remove(descriptor);
        services.TryAddSingleton(new PostgresWorkerCheckpointStore(dataSource));
        services.TryAddSingleton<IWorkerCheckpointStore>(provider => provider.GetRequiredService<PostgresWorkerCheckpointStore>());
        return builder;
    }
}
