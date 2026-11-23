using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.HelpCenter.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IHelpCenterStore"/> and <see cref="IArticleFeedbackStore"/> over an
/// application-owned <see cref="NpgsqlDataSource"/>. Tables live in the data source's current schema and are created only by
/// <see cref="InitializeAsync"/>. Every statement is parameterized and every key includes the full scope. Structural writes for one
/// workspace serialize on a transaction-scoped advisory lock, so capacity, reference, depth and emptiness checks are atomic with
/// the write; updates and deletes are compare-and-set on revision. Article writes append the revision and rebuild the published
/// read model (and its <c>tsvector</c>) in the same transaction. Text search configurations come from the fixed
/// <see cref="HelpCenterSearchConfiguration"/> allow-list and are bound as parameters.
/// </summary>
public sealed partial class PostgresHelpCenterStore : IHelpCenterStore, IArticleFeedbackStore
{
    private const string WorkspaceWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKeyWhere = "scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND " + WorkspaceWhere;
    private const string PortalKeyWhere = "portal_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@portal) AND " + WorkspaceWhere + " AND portal_id=@portal";

    private readonly NpgsqlDataSource source;

    public PostgresHelpCenterStore(NpgsqlDataSource dataSource) => source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Explicitly applies <see cref="PostgresHelpCenterMigrations.Catalog"/> through <see cref="PostgresMigrationRunner"/>
    /// (advisory-locked, checksum-verified, idempotent). Never called implicitly.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresHelpCenterMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    // ---------------------------------------------------------------- portals

    private const string PortalColumns = "portal_id, format_version, slug, name, default_locale, supported_locales, visibility, custom_domain, revision";

    public async Task<StoredPortal?> GetPortalAsync(TicketScope scope, PortalId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT {PortalColumns} FROM tf_helpcenter_portals WHERE {PortalKeyWhere}");
        AddScope(command, scope);
        AddText(command, "portal", id.Value);
        return await ReadSingleAsync(command, reader => ReadPortal(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredPortal?> FindPortalBySlugAsync(TicketScope scope, HelpCenterSlug slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(slug.IsInitialized, nameof(slug));
        await using var command = source.CreateCommand($"SELECT {PortalColumns} FROM tf_helpcenter_portals WHERE {ScopeKeyWhere} AND slug=@slug");
        AddScope(command, scope);
        AddText(command, "slug", slug.Value);
        return await ReadSingleAsync(command, reader => ReadPortal(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredPortal>> ListPortalsAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {PortalColumns} FROM tf_helpcenter_portals WHERE {ScopeKeyWhere} ORDER BY portal_id COLLATE \"C\" LIMIT {HelpCenterLimits.MaximumPortalsPerWorkspace}");
        AddScope(command, scope);
        return await ReadListAsync(command, reader => ReadPortal(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<HelpCenterWriteResult<StoredPortal>> CreatePortalAsync(TicketScope scope, Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(portal);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        if (await PortalRevisionAsync(connection, transaction, scope, portal.Id, cancellationToken).ConfigureAwait(false) is { } existing)
            return new HelpCenterWriteConflict(existing);
        if (await ScalarAsync<long>(connection, transaction, $"SELECT count(*) FROM tf_helpcenter_portals WHERE {ScopeKeyWhere}", command => AddScope(command, scope), cancellationToken).ConfigureAwait(false) >= HelpCenterLimits.MaximumPortalsPerWorkspace)
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.CapacityExceeded);
        if (await SlugTakenAsync(connection, transaction, scope, portal, cancellationToken).ConfigureAwait(false))
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.SlugConflict);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_helpcenter_portals({ScopeColumns}, portal_id, format_version, slug, name, default_locale, supported_locales, visibility, custom_domain, revision)
            VALUES({ScopeParameters}, @portal, 1, @slug, @name, @default, @locales, @visibility, @domain, 1)
            """, connection, transaction))
        {
            BindPortal(insert, scope, portal);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HelpCenterWriteApplied<StoredPortal>(new(scope, portal, 1));
    }

    public async Task<HelpCenterWriteResult<StoredPortal>> UpdatePortalAsync(TicketScope scope, Portal portal, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var current = await PortalRevisionAsync(connection, transaction, scope, portal.Id, cancellationToken).ConfigureAwait(false);
        if (Miss<StoredPortal>(current, expectedRevision) is { } miss)
            return miss;
        if (await SlugTakenAsync(connection, transaction, scope, portal, cancellationToken).ConfigureAwait(false))
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.SlugConflict);
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_helpcenter_portals SET slug=@slug, name=@name, default_locale=@default, supported_locales=@locales, visibility=@visibility,
                custom_domain=@domain, revision=revision+1
            WHERE {PortalKeyWhere}
            """, connection, transaction))
        {
            BindPortal(update, scope, portal);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HelpCenterWriteApplied<StoredPortal>(new(scope, portal, expectedRevision + 1));
    }

    public async Task<HelpCenterWriteResult<StoredPortal>> DeletePortalAsync(TicketScope scope, PortalId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var current = await PortalRevisionAsync(connection, transaction, scope, id, cancellationToken).ConfigureAwait(false);
        if (Miss<StoredPortal>(current, expectedRevision) is { } miss)
            return miss;
        void Bind(NpgsqlCommand command)
        {
            AddScope(command, scope);
            AddText(command, "portal", id.Value);
        }
        var used = await ScalarAsync<bool>(connection, transaction, $"""
            SELECT EXISTS (SELECT 1 FROM tf_helpcenter_collections WHERE {PortalKeyWhere}) OR EXISTS (SELECT 1 FROM tf_helpcenter_articles WHERE {PortalKeyWhere})
            """, Bind, cancellationToken).ConfigureAwait(false);
        if (used)
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.NotEmpty);
        StoredPortal? removed;
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_helpcenter_portals WHERE {PortalKeyWhere} RETURNING {PortalColumns}", connection, transaction))
        {
            Bind(delete);
            removed = await ReadSingleAsync(delete, reader => ReadPortal(reader, scope), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HelpCenterWriteApplied<StoredPortal>(removed!);
    }

    private static Task<long?> PortalRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, PortalId id, CancellationToken cancellationToken) =>
        NullableScalarAsync<long>(connection, transaction, $"SELECT revision FROM tf_helpcenter_portals WHERE {PortalKeyWhere}", command =>
        {
            AddScope(command, scope);
            AddText(command, "portal", id.Value);
        }, cancellationToken);

    private static Task<bool> SlugTakenAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Portal portal, CancellationToken cancellationToken) =>
        ScalarAsync<bool>(connection, transaction, $"SELECT EXISTS (SELECT 1 FROM tf_helpcenter_portals WHERE {ScopeKeyWhere} AND slug=@slug AND portal_id<>@portal)", command =>
        {
            AddScope(command, scope);
            AddText(command, "slug", portal.Slug.Value);
            AddText(command, "portal", portal.Id.Value);
        }, cancellationToken);

    private static void BindPortal(NpgsqlCommand command, TicketScope scope, Portal portal)
    {
        AddScope(command, scope);
        AddText(command, "portal", portal.Id.Value);
        AddText(command, "slug", portal.Slug.Value);
        AddText(command, "name", portal.Name);
        AddText(command, "default", portal.DefaultLocale.Value);
        command.Parameters.Add(new NpgsqlParameter("locales", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = portal.SupportedLocales.Select(locale => locale.Value).ToArray() });
        command.Parameters.AddWithValue("visibility", (short)portal.Visibility);
        AddText(command, "domain", portal.CustomDomain);
    }

    private static StoredPortal ReadPortal(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported portal format version.");
        var visibility = (PortalVisibility)reader.GetInt16(6);
        if (!Enum.IsDefined(visibility))
            throw new FormatException("Unknown portal visibility.");
        var portal = new Portal(new PortalId(reader.GetString(0)), new HelpCenterSlug(reader.GetString(2)), reader.GetString(3), new HelpCenterLocale(reader.GetString(4)),
            reader.GetFieldValue<string[]>(5).Select(locale => new HelpCenterLocale(locale)), visibility, reader.IsDBNull(7) ? null : reader.GetString(7));
        return new StoredPortal(scope, portal, reader.GetInt64(8));
    }, "portal");

    // ---------------------------------------------------------------- shared helpers

    private const string ScopeColumns = "tenant_id, project_id, environment_id, app_id, workspace_id";
    private const string ScopeParameters = "@tenant, @project, @environment, @app, @workspace";

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

    private static void AddJson(NpgsqlCommand command, string name, string json) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = json });

    private static void AddActor(NpgsqlCommand command, string prefix, AccessActor actor)
    {
        command.Parameters.AddWithValue(prefix + "_kind", (short)actor.Kind);
        AddText(command, prefix + "_id", actor.Id);
    }

    private static AccessActor ReadActor(NpgsqlDataReader reader, int kind, int id)
    {
        var actorKind = (AccessActorKind)reader.GetInt16(kind);
        if (!Enum.IsDefined(actorKind))
            throw new FormatException("Unknown actor kind.");
        return new AccessActor(actorKind, reader.GetString(id));
    }

    /// <summary>Serializes structural writers for one workspace within the current transaction.</summary>
    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = new[] { "ticketflow.helpcenter", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value },
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static HelpCenterWriteResult<T>? Miss<T>(long? current, long expectedRevision) => current switch
    {
        null => HelpCenterWriteNotFound.Instance,
        { } revision when revision != expectedRevision => new HelpCenterWriteConflict(revision),
        _ => null,
    };

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        bind(command);
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<T?> NullableScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
        where T : struct
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        bind(command);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is T value ? value : null;
    }

    private static async Task<T?> ReadSingleAsync<T>(NpgsqlCommand command, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
        where T : class
    {
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
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
            throw new InvalidOperationException($"A stored help center {description} is invalid or incompatible.", error);
        }
    }
}
