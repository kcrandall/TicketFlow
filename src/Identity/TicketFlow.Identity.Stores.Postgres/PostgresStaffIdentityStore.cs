using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Identity.Stores.Postgres;

/// <summary>Feature-owned, append-only migration history for staff identity tables. Never edit an applied step; append one.</summary>
public static class PostgresStaffIdentityMigrations
{
    private const string Scope = "tenant_id text NOT NULL, project_id text NOT NULL, environment_id text NOT NULL, app_id text NOT NULL, workspace_id text NOT NULL";

    public static PostgresMigrationCatalog Catalog { get; } = new("ticketflow.identity", [new(1, "initial-schema", PostgresBoundedKeys.Version1Sql + $"""
        CREATE TABLE tf_staff_identities (
            {Scope}, provider text COLLATE "C" NOT NULL, subject text COLLATE "C" NOT NULL, user_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            linked_at timestamptz NOT NULL, linked_by_kind smallint NOT NULL CHECK (linked_by_kind BETWEEN 1 AND 4), linked_by_id text NOT NULL,
            identity_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, provider, subject)) STORED NOT NULL,
            user_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, user_id)) STORED NOT NULL,
            PRIMARY KEY (identity_key));
        CREATE INDEX tf_staff_identities_user ON tf_staff_identities (user_key, provider, subject);

        CREATE TABLE tf_staff_profiles (
            {Scope}, user_id text COLLATE "C" NOT NULL,
            format_version smallint NOT NULL CHECK (format_version = 1),
            display_name text NULL, avatar_ref text NULL, notification_email text NULL,
            updated_at timestamptz NOT NULL, revision bigint NOT NULL CHECK (revision > 0),
            user_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id, user_id)) STORED NOT NULL,
            PRIMARY KEY (user_key));

        CREATE TABLE tf_staff_identity_audit (
            event_id uuid PRIMARY KEY, {Scope},
            format_version smallint NOT NULL CHECK (format_version = 1), occurred_at timestamptz NOT NULL,
            kind smallint NOT NULL CHECK (kind BETWEEN 1 AND 3), actor_kind smallint NOT NULL CHECK (actor_kind BETWEEN 1 AND 4), actor_id text NOT NULL,
            user_id text NOT NULL, provider text NULL, justification text NULL,
            scope_key bytea GENERATED ALWAYS AS (tf_key_v1(tenant_id, project_id, environment_id, app_id, workspace_id)) STORED NOT NULL);
        CREATE INDEX tf_staff_identity_audit_scope ON tf_staff_identity_audit (scope_key, occurred_at, event_id);
        """)]);
}

/// <summary>
/// Durable PostgreSQL <see cref="IStaffIdentityStore"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables are created only
/// by <see cref="InitializeAsync"/>; keys are <c>tf_key_v1</c> hashes over the raw identifiers (raw columns are compared too); every
/// statement is parameterized; per-user capacity is checked under a transaction-scoped advisory lock; audit rows commit with the change.
/// </summary>
public sealed class PostgresStaffIdentityStore(NpgsqlDataSource dataSource) : IStaffIdentityStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string IdentityWhere = "identity_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@provider,@subject) AND provider=@provider AND subject=@subject AND " + ScopeWhere;
    private const string UserWhere = "user_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@user) AND user_id=@user AND " + ScopeWhere;
    private const string LinkColumns = "provider, subject, user_id, format_version, linked_at, linked_by_kind, linked_by_id";

    private readonly NpgsqlDataSource source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Explicitly applies <see cref="PostgresStaffIdentityMigrations.Catalog"/>. Never called implicitly.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresStaffIdentityMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<StoredStaffIdentityLink?> FindAsync(TicketScope scope, StaffIdentityRef identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindAsync(connection, null, scope, identity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredStaffIdentityLink>> ListForUserAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT {LinkColumns} FROM tf_staff_identities WHERE {UserWhere} ORDER BY provider, subject LIMIT {IStaffIdentityStore.MaximumIdentitiesPerUser}");
        AddScope(command, scope);
        AddText(command, "user", user.Value);
        var links = new List<StoredStaffIdentityLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            links.Add(ReadLink(reader, scope));
        return links;
    }

    public async Task<StaffIdentityWriteResult> LinkAsync(TicketScope scope, StaffIdentityLink link, StaffIdentityAuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(audit);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, ["staff-identity-user", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value, link.User.Value],
            cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_staff_identities(tenant_id, project_id, environment_id, app_id, workspace_id, {LinkColumns})
            SELECT @tenant, @project, @environment, @app, @workspace, @provider, @subject, @user, 1, @linked_at, @linked_by_kind, @linked_by_id
            WHERE (SELECT count(*) FROM tf_staff_identities WHERE {UserWhere}) < {IStaffIdentityStore.MaximumIdentitiesPerUser}
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, scope);
            BindIdentity(insert, link.Identity);
            AddText(insert, "user", link.User.Value);
            insert.Parameters.Add(new NpgsqlParameter("linked_at", NpgsqlDbType.TimestampTz) { Value = link.LinkedAt });
            insert.Parameters.AddWithValue("linked_by_kind", (short)link.LinkedBy.Kind);
            AddText(insert, "linked_by_id", link.LinkedBy.Id);
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                await InsertAuditAsync(connection, transaction, audit, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StaffIdentityWriteApplied(new StoredStaffIdentityLink(scope, link));
            }
        }
        var existing = await FindAsync(connection, transaction, scope, link.Identity, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return StaffIdentityWriteRejected.CapacityExceeded;
        return existing.Link.User == link.User ? new StaffIdentityWriteApplied(existing, false) : new StaffIdentityWriteTaken(existing.Link.User);
    }

    public async Task<StaffIdentityWriteResult> UnlinkAsync(TicketScope scope, StaffIdentityRef identity, UserId expectedUser, StaffIdentityAuditEvent audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(audit);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_staff_identities WHERE {IdentityWhere} AND user_id=@user RETURNING {LinkColumns}", connection, transaction))
        {
            AddScope(delete, scope);
            BindIdentity(delete, identity);
            AddText(delete, "user", expectedUser.Value);
            StoredStaffIdentityLink? removed = null;
            await using (var reader = await delete.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    removed = ReadLink(reader, scope);
            }
            if (removed is not null)
            {
                await InsertAuditAsync(connection, transaction, audit, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StaffIdentityWriteApplied(removed);
            }
        }
        var existing = await FindAsync(connection, transaction, scope, identity, cancellationToken).ConfigureAwait(false);
        return existing is null ? StaffIdentityWriteNotFound.Instance : new StaffIdentityWriteTaken(existing.Link.User);
    }

    public async Task<StoredStaffProfile?> GetProfileAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT display_name, avatar_ref, notification_email, updated_at, revision, format_version FROM tf_staff_profiles WHERE {UserWhere}");
        AddScope(command, scope);
        AddText(command, "user", user.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(5) != 1)
                throw new FormatException("Unsupported staff profile format version.");
            return new StoredStaffProfile(scope, new StaffProfile(user, Text(reader, 0), Text(reader, 1), Text(reader, 2)), reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt64(4));
        }, "staff profile");
    }

    public async Task<StaffIdentityProfileWriteResult> PutProfileAsync(TicketScope scope, StaffProfile profile, DateTimeOffset updatedAt, long? expectedRevision, StaffIdentityAuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(audit);
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var at = new DateTimeOffset(updatedAt.UtcTicks - updatedAt.UtcTicks % 10, TimeSpan.Zero);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var write = new NpgsqlCommand(expectedRevision is null
            ? """
              INSERT INTO tf_staff_profiles(tenant_id, project_id, environment_id, app_id, workspace_id, user_id, format_version, display_name, avatar_ref, notification_email, updated_at, revision)
              VALUES(@tenant, @project, @environment, @app, @workspace, @user, 1, @display_name, @avatar_ref, @email, @updated, 1) ON CONFLICT DO NOTHING RETURNING revision
              """
            : $"""
              UPDATE tf_staff_profiles SET display_name=@display_name, avatar_ref=@avatar_ref, notification_email=@email, updated_at=@updated, revision=revision+1
              WHERE {UserWhere} AND revision=@expected RETURNING revision
              """, connection, transaction))
        {
            AddScope(write, scope);
            AddText(write, "user", profile.User.Value);
            AddText(write, "display_name", profile.DisplayName);
            AddText(write, "avatar_ref", profile.AvatarRef);
            AddText(write, "email", profile.NotificationEmail);
            write.Parameters.Add(new NpgsqlParameter("updated", NpgsqlDbType.TimestampTz) { Value = at });
            if (expectedRevision is { } expected)
                write.Parameters.AddWithValue("expected", expected);
            if (await write.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
            {
                await InsertAuditAsync(connection, transaction, audit, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new StaffIdentityProfileWriteApplied(new StoredStaffProfile(scope, profile, at, revision));
            }
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_staff_profiles WHERE {UserWhere}", connection, transaction);
        AddScope(current, scope);
        AddText(current, "user", profile.User.Value);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new StaffIdentityWriteConflict(existing)
            : StaffIdentityWriteNotFound.Instance;
    }

    private static async Task<StoredStaffIdentityLink?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, StaffIdentityRef identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {LinkColumns} FROM tf_staff_identities WHERE {IdentityWhere}", connection, transaction);
        AddScope(command, scope);
        BindIdentity(command, identity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadLink(reader, scope) : null;
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, StaffIdentityAuditEvent audit, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand("""
            INSERT INTO tf_staff_identity_audit(event_id, tenant_id, project_id, environment_id, app_id, workspace_id, format_version, occurred_at, kind, actor_kind, actor_id,
                user_id, provider, justification)
            VALUES(@event, @tenant, @project, @environment, @app, @workspace, 1, @occurred, @kind, @actor_kind, @actor_id, @user, @provider, @justification)
            """, connection, transaction);
        AddScope(insert, audit.Scope);
        insert.Parameters.AddWithValue("event", audit.Id);
        insert.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.TimestampTz) { Value = audit.OccurredAt });
        insert.Parameters.AddWithValue("kind", (short)audit.Kind);
        insert.Parameters.AddWithValue("actor_kind", (short)audit.Actor.Kind);
        AddText(insert, "actor_id", audit.Actor.Id);
        AddText(insert, "user", audit.User.Value);
        AddText(insert, "provider", audit.Provider);
        AddText(insert, "justification", audit.Justification);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string[] parts, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("parts", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = parts });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static StoredStaffIdentityLink ReadLink(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(3) != 1)
            throw new FormatException("Unsupported staff identity format version.");
        return new StoredStaffIdentityLink(scope, new StaffIdentityLink(new StaffIdentityRef(reader.GetString(0), reader.GetString(1)), new UserId(reader.GetString(2)),
            reader.GetFieldValue<DateTimeOffset>(4), new AccessActor((AccessActorKind)reader.GetInt16(5), reader.GetString(6))));
    }, "staff identity");

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void BindIdentity(NpgsqlCommand command, StaffIdentityRef identity)
    {
        AddText(command, "provider", identity.Provider);
        AddText(command, "subject", identity.Subject);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException($"A stored {description} is invalid or incompatible.", error);
        }
    }
}
