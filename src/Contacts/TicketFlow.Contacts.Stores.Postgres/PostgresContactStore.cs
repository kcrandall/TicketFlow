using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Core;
using TicketFlow.Stores.Migrations.Postgres;

namespace TicketFlow.Contacts.Stores.Postgres;

/// <summary>
/// Durable PostgreSQL implementation of <see cref="IContactStore"/>, <see cref="IContactAttributeDefinitionStore"/> and
/// <see cref="IContactAuditSink"/> over an application-owned <see cref="NpgsqlDataSource"/>. Tables live in the data source's current
/// schema and are created only by <see cref="InitializeAsync"/>. Every statement is parameterized and confined to one full scope;
/// batches run in one transaction; verified identities are unique through a partial unique index; change sequences are gapless per
/// scope (a per-scope head row serializes appenders until commit); search uses keyset pagination over indexed columns with the
/// translated <see cref="AccessFilter"/> in the same statement. Rows that fail to decode throw.
/// </summary>
public sealed partial class PostgresContactStore : IContactStore, IContactAttributeDefinitionStore, IContactAuditSink, IContactLinkingStore
{
    private const string ScopeWhere = "tenant_id=@tenant AND project_id=@project AND environment_id=@environment AND app_id=@app AND workspace_id=@workspace";
    private const string ScopeKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace)";
    private const string ContactKey = "contact_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@contact) AND contact_id=@contact AND " + ScopeWhere;
    private const string ContactColumns = "contact_id, format_version, display_name, avatar_ref, locale, time_zone, company, attributes::text, labels, blocked, merged_into, created_at, updated_at, revision";

    private static readonly PostgresAccessFilterColumns FilterColumns = new()
    {
        TenantId = "c.tenant_id",
        ProjectId = "c.project_id",
        EnvironmentId = "c.environment_id",
        AppId = "c.app_id",
        WorkspaceId = "c.workspace_id",
        TeamId = "n.team_id",
        InboxId = "n.inbox_id",
        AssigneeId = "n.assignee_id",
        Labels = "c.labels",
        Attributes = "n.attributes",
    };

    // Contacts have no team, inbox, assignee or condition attributes: the lateral row supplies those as SQL constants so the
    // translated filter has exactly the semantics of ContactAccess.Resource (no team, unknown attributes).
    private const string FilterJoin = "CROSS JOIN LATERAL (SELECT NULL::text AS team_id, NULL::text AS inbox_id, NULL::text AS assignee_id, '{}'::jsonb AS attributes) n";

    private readonly NpgsqlDataSource source;

    private readonly int maximumRowsExamined;

    public PostgresContactStore(NpgsqlDataSource dataSource)
        : this(dataSource, ContactSearch.DefaultMaximumRowsExamined)
    {
    }

    /// <param name="dataSource">The application-owned data source.</param>
    /// <param name="maximumRowsExamined">Rows examined per search page before the access filter (1-100,000).</param>
    public PostgresContactStore(NpgsqlDataSource dataSource, int maximumRowsExamined)
    {
        source = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRowsExamined, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumRowsExamined, 100_000);
        this.maximumRowsExamined = maximumRowsExamined;
    }

    /// <summary>
    /// Explicitly applies <see cref="PostgresContactMigrations.Catalog"/> through <see cref="PostgresMigrationRunner"/> (advisory-locked,
    /// checksum-verified, idempotent). Never called implicitly.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        new PostgresMigrationRunner(source, PostgresContactMigrations.Catalog).ApplyAsync(cancellationToken: cancellationToken);

    public async Task<StoredContact?> GetAsync(TicketScope scope, ContactId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(id);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var contacts = await LoadAsync(connection, null, scope, [id], cancellationToken).ConfigureAwait(false);
        return contacts.GetValueOrDefault(id);
    }

    public async Task<IReadOnlyList<ContactIdentityOwner>> FindByIdentityAsync(TicketScope scope, ContactIdentityAddress address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(address);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = FindByIdentityCommand(scope, address);
        command.Connection = connection;
        var owners = new List<ContactIdentityOwner>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            owners.Add(Decode(() => new ContactIdentityOwner(new ContactId(reader.GetString(0)),
                ContactJsonCodec.RestoreVerification(reader.GetBoolean(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3))), "identity"));
        return owners;
    }

    /// <summary>The identity lookup statement (exposed internally so tests can EXPLAIN the exact SQL).</summary>
    internal static NpgsqlCommand FindByIdentityCommand(TicketScope scope, ContactIdentityAddress address)
    {
        var command = new NpgsqlCommand($"""
            SELECT contact_id, verified, source, verified_at FROM tf_contact_identities
            WHERE identity_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@kind::text,@namespace,@value)
              AND {ScopeWhere} AND kind=@kind AND namespace=@namespace AND value=@value
            ORDER BY verified DESC, contact_id LIMIT {IContactStore.MaximumIdentityOwners}
            """);
        AddScope(command, scope);
        BindAddress(command, address);
        return command;
    }

    public async Task<ContactPage> SearchAsync(TicketScope scope, ContactSearch search, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(filter);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        List<(string Key, ContactId Id, bool Visible)> examined = [];
        await using (var command = SearchCommand(scope, search, filter, maximumRowsExamined))
        {
            command.Connection = connection;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                examined.Add(Decode(() => (reader.GetString(0), new ContactId(reader.GetString(1)), reader.GetBoolean(2)), "search row"));
        }

        var visible = new List<(string Key, ContactId Id)>();
        foreach (var row in examined)
        {
            if (!row.Visible)
                continue;
            visible.Add((row.Key, row.Id));
            if (visible.Count == search.Limit)
                break;
        }
        var distinct = visible.Select(row => row.Id).Distinct().ToList();
        var contacts = await LoadAsync(connection, null, scope, distinct, cancellationToken).ConfigureAwait(false);
        var page = distinct.Where(contacts.ContainsKey).Select(id => contacts[id]).Where(stored => stored.Contact.MergedInto is null).ToList();
        ContactSearchCursor? next = visible.Count == search.Limit
            ? new ContactSearchCursor(visible[^1].Key, visible[^1].Id)
            : examined.Count == maximumRowsExamined ? new ContactSearchCursor(examined[^1].Key, examined[^1].Id) : null;
        return new ContactPage(page, next);
    }

    /// <summary>
    /// The search statement (exposed internally so tests can EXPLAIN the exact SQL). An inner keyset query reads at most
    /// <paramref name="maximumRowsExamined"/> rows in index order; the translated access filter is evaluated per examined row, and the
    /// caller keeps the first visible rows, continuing after the last examined row when the scan bound is reached.
    /// </summary>
    internal static NpgsqlCommand SearchCommand(TicketScope scope, ContactSearch search, AccessFilter filter, int maximumRowsExamined)
    {
        var translated = PostgresAccessFilterTranslator.Translate(filter, FilterColumns).OrDenyAll();
        var command = new NpgsqlCommand();
        AddScope(command, scope);
        foreach (var parameter in translated.Parameters)
            command.Parameters.Add(parameter.Clone());
        command.Parameters.AddWithValue("scan", maximumRowsExamined);
        var prefix = search.Prefix.Length == 0 ? string.Empty : " AND {0} >= @prefix COLLATE \"C\" AND {0} < @prefix_high COLLATE \"C\" AND starts_with({0}, @prefix)";
        if (search.Prefix.Length > 0)
        {
            AddText(command, "prefix", search.Prefix);
            AddText(command, "prefix_high", search.Prefix + char.ConvertFromUtf32(0x10FFFF));
        }
        var after = search.After is null ? string.Empty : " AND ({0}, {1}) > (@after_key COLLATE \"C\", @after_id COLLATE \"C\")";
        if (search.After is { } cursor)
        {
            AddText(command, "after_key", cursor.SortKey);
            AddText(command, "after_id", cursor.Contact.Value);
        }

        if (search.Mode == ContactSearchMode.Name)
        {
            command.CommandText = $"""
                SELECT c.sort_key, c.contact_id, ({translated.Sql}) IS TRUE FROM (
                    SELECT s.name_folded AS sort_key, s.contact_id, s.tenant_id, s.project_id, s.environment_id, s.app_id, s.workspace_id, s.labels
                    FROM tf_contacts s
                    WHERE s.scope_key={ScopeKey} AND s.tenant_id=@tenant AND s.project_id=@project AND s.environment_id=@environment AND s.app_id=@app
                      AND s.workspace_id=@workspace AND s.merged_into IS NULL{string.Format(prefix, "s.name_folded")}{string.Format(after, "s.name_folded", "s.contact_id")}
                    ORDER BY s.name_folded, s.contact_id LIMIT @scan) c {FilterJoin}
                ORDER BY c.sort_key, c.contact_id
                """;
        }
        else
        {
            command.Parameters.AddWithValue("kind", (short)search.Kind!.Value);
            var ns = search.Namespace is null ? string.Empty : " AND i.namespace=@namespace";
            if (search.Namespace is not null)
                AddText(command, "namespace", search.Namespace);
            command.CommandText = $"""
                SELECT c.sort_key, c.contact_id, ({translated.Sql}) IS TRUE FROM (
                    SELECT i.value AS sort_key, i.contact_id, s.tenant_id, s.project_id, s.environment_id, s.app_id, s.workspace_id, s.labels
                    FROM tf_contact_identities i JOIN tf_contacts s ON s.contact_key=i.contact_key AND s.contact_id=i.contact_id
                    WHERE i.scope_key={ScopeKey} AND i.tenant_id=@tenant AND i.project_id=@project AND i.environment_id=@environment AND i.app_id=@app
                      AND i.workspace_id=@workspace AND i.kind=@kind{ns}{string.Format(prefix, "i.value")}{string.Format(after, "i.value", "i.contact_id")}
                      AND s.merged_into IS NULL
                    ORDER BY i.value, i.contact_id LIMIT @scan) c {FilterJoin}
                ORDER BY c.sort_key, c.contact_id
                """;
        }
        return command;
    }

    public async Task<IReadOnlyList<ContactId>> ListMergedIntoAsync(TicketScope scope, ContactId target, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireId(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, ContactDelete.MaximumCascade);
        await using var command = source.CreateCommand($"""
            SELECT contact_id FROM tf_contacts
            WHERE merged_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@target) AND merged_into=@target AND {ScopeWhere}
            ORDER BY contact_id LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "target", target.Value);
        command.Parameters.AddWithValue("limit", limit);
        var ids = new List<ContactId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(Decode(() => new ContactId(reader.GetString(0)), "tombstone"));
        return ids;
    }

    public async Task<ContactMergeRecord?> GetMergeAsync(TicketScope scope, Guid mergeId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"""
            SELECT target_id, source_id, merged_at, actor_kind, actor_id, contributions::text, format_version FROM tf_contact_merges
            WHERE merge_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@merge::text) AND merge_id=@merge AND {ScopeWhere}
            """);
        AddScope(command, scope);
        command.Parameters.AddWithValue("merge", mergeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(6) != 1)
                throw new FormatException("Unsupported merge format version.");
            return ContactJsonCodec.DeserializeMergeRecord(reader.GetString(5), mergeId, new ContactId(reader.GetString(0)), new ContactId(reader.GetString(1)),
                reader.GetFieldValue<DateTimeOffset>(2), new AccessActor((AccessActorKind)reader.GetInt16(3), reader.GetString(4)));
        }, "merge record");
    }

    public async Task<IReadOnlyList<ContactChange>> ReadChangesAsync(TicketScope scope, long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IContactStore.MaximumChangesPerRead);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = ReadChangesCommand(scope, afterSequence, limit);
        command.Connection = connection;
        var changes = new List<ContactChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            changes.Add(Decode(() =>
            {
                if (reader.GetInt16(7) != 1)
                    throw new FormatException("Unsupported change format version.");
                return new ContactChange(scope, reader.GetInt64(0), new ContactChangeDraft(reader.GetGuid(1), (ContactChangeKind)reader.GetInt16(2),
                    new ContactId(reader.GetString(3)), reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(4) ? null : new ContactId(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetGuid(5)));
            }, "change"));
        return changes;
    }

    /// <summary>The outbox read statement (exposed internally so tests can EXPLAIN the exact SQL).</summary>
    internal static NpgsqlCommand ReadChangesCommand(TicketScope scope, long afterSequence, int limit)
    {
        var command = new NpgsqlCommand($"""
            SELECT sequence, event_id, kind, contact_id, related_id, merge_id, occurred_at, format_version FROM tf_contact_changes
            WHERE scope_key={ScopeKey} AND {ScopeWhere} AND sequence > @after ORDER BY sequence LIMIT @limit
            """);
        AddScope(command, scope);
        command.Parameters.AddWithValue("after", afterSequence);
        command.Parameters.AddWithValue("limit", limit);
        return command;
    }

    public async Task RecordAsync(ContactAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var command = source.CreateCommand(AuditInsert);
        BindAudit(command, auditEvent);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string AuditInsert = """
        INSERT INTO tf_contact_audit(event_id, tenant_id, project_id, environment_id, app_id, workspace_id, format_version, occurred_at, kind, actor_kind, actor_id,
            contact_id, related_id, merge_id, count, justification, suggestion_id)
        VALUES(@event, @tenant, @project, @environment, @app, @workspace, 1, @occurred, @kind, @actor_kind, @actor_id, @contact, @related, @merge, @count, @justification, @suggestion)
        """;

    private static void BindAudit(NpgsqlCommand command, ContactAuditEvent auditEvent)
    {
        AddScope(command, auditEvent.Scope);
        command.Parameters.AddWithValue("event", auditEvent.Id);
        AddInstant(command, "occurred", auditEvent.OccurredAt);
        command.Parameters.AddWithValue("kind", (short)auditEvent.Kind);
        command.Parameters.AddWithValue("actor_kind", (short)auditEvent.Actor.Kind);
        AddText(command, "actor_id", auditEvent.Actor.Id);
        AddText(command, "contact", auditEvent.Contact?.Value);
        AddText(command, "related", auditEvent.Related?.Value);
        command.Parameters.Add(new NpgsqlParameter("merge", NpgsqlDbType.Uuid) { Value = (object?)auditEvent.Merge ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("count", NpgsqlDbType.Integer) { Value = (object?)auditEvent.Count ?? DBNull.Value });
        AddText(command, "justification", auditEvent.Justification);
        command.Parameters.Add(new NpgsqlParameter("suggestion", NpgsqlDbType.Uuid) { Value = (object?)auditEvent.Suggestion ?? DBNull.Value });
    }

    /// <summary>Loads contacts (with identities) by identifier in one scope.</summary>
    private static async Task<Dictionary<ContactId, StoredContact>> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope,
        IReadOnlyCollection<ContactId> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ContactId, StoredContact>();
        if (ids.Count == 0)
            return result;
        var keys = $"SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id";
        var identities = new Dictionary<string, List<ContactIdentity>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand($"""
            SELECT contact_id, kind, namespace, value, verified, source, verified_at FROM tf_contact_identities
            WHERE contact_key IN ({keys}) AND {ScopeWhere} AND contact_id = ANY(@ids)
            """, connection, transaction))
        {
            AddScope(command, scope);
            AddTextArray(command, "ids", ids.Select(id => id.Value));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var identity = Decode(() => new ContactIdentity(
                    ContactJsonCodec.RestoreAddress((ContactIdentityKind)reader.GetInt16(1), reader.GetString(2), reader.GetString(3)),
                    ContactJsonCodec.RestoreVerification(reader.GetBoolean(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6))), "identity");
                var owner = reader.GetString(0);
                if (!identities.TryGetValue(owner, out var list))
                    identities[owner] = list = [];
                list.Add(identity);
            }
        }
        await using (var command = new NpgsqlCommand($"""
            SELECT {ContactColumns} FROM tf_contacts WHERE contact_key IN ({keys}) AND {ScopeWhere} AND contact_id = ANY(@ids)
            """, connection, transaction))
        {
            AddScope(command, scope);
            AddTextArray(command, "ids", ids.Select(id => id.Value));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var stored = Decode(() =>
                {
                    if (reader.GetInt16(1) != 1)
                        throw new FormatException("Unsupported contact format version.");
                    var id = reader.GetString(0);
                    var contact = new Contact(
                        new ContactId(id),
                        new ContactProfile(NullableText(reader, 2), NullableText(reader, 3), NullableText(reader, 4), NullableText(reader, 5), NullableText(reader, 6)),
                        identities.GetValueOrDefault(id) ?? [],
                        ContactJsonCodec.DeserializeAttributes(reader.GetString(7)),
                        reader.GetFieldValue<string[]>(8).Select(label => new LabelId(label)),
                        reader.GetBoolean(9),
                        reader.GetFieldValue<DateTimeOffset>(11),
                        reader.GetFieldValue<DateTimeOffset>(12),
                        reader.IsDBNull(10) ? null : new ContactId(reader.GetString(10)));
                    return new StoredContact(scope, contact, reader.GetInt64(13));
                }, "contact");
                result[stored.Id] = stored;
            }
        }
        return result;
    }

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void BindAddress(NpgsqlCommand command, ContactIdentityAddress address)
    {
        command.Parameters.AddWithValue("kind", (short)address.Kind);
        AddText(command, "namespace", address.Namespace);
        AddText(command, "value", address.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });

    private static void AddTextArray(NpgsqlCommand command, string name, IEnumerable<string> values) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.TimestampTz) { Value = value });

    private static void RequireId(ContactId id)
    {
        if (id == default)
            throw new ArgumentException("A contact identifier is required.", nameof(id));
    }

    private static T Decode<T>(Func<T> decode, string description)
    {
        try
        {
            return decode();
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw new InvalidOperationException($"A stored contact {description} is invalid or incompatible.", error);
        }
    }
}
