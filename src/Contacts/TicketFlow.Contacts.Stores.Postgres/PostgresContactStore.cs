using System.Data;
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
    private const string ContactScopeWhere = "c.tenant_id=@tenant AND c.project_id=@project AND c.environment_id=@environment AND c.app_id=@app AND c.workspace_id=@workspace";
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

    public async Task<IReadOnlyDictionary<ContactId, StoredContact>> GetManyAsync(TicketScope scope, IReadOnlyCollection<ContactId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
            RequireId(id);
        if (ids.Count == 0)
            return new Dictionary<ContactId, StoredContact>();
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadAsync(connection, null, scope, ids, cancellationToken).ConfigureAwait(false);
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
        {
            try
            {
                owners.Add(new ContactIdentityOwner(new ContactId(reader.GetString(0)),
                    ContactJsonCodec.RestoreVerification(reader.GetBoolean(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3))));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
            {
                throw StoredDataError(error, "identity");
            }
        }
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
        var visible = new List<(string Key, ContactId Id)>(Math.Min(search.Limit, 64));
        (string Key, ContactId Id)? tailCursor = null;
        await using (var command = SearchCommand(scope, search, filter, maximumRowsExamined))
        {
            command.Connection = connection;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var key = reader.GetString(0);
                    var id = new ContactId(reader.GetString(1));
                    if (reader.GetBoolean(2))
                        visible.Add((key, id));
                    else
                        tailCursor = (key, id);
                }
                catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
                {
                    throw StoredDataError(error, "search row");
                }
            }
        }

        List<ContactId> distinct;
        if (visible.Count == 0)
            distinct = [];
        else
        {
            distinct = new List<ContactId>(visible.Count);
            var seen = new HashSet<ContactId>(visible.Count);
            foreach (var row in visible)
                if (seen.Add(row.Id))
                    distinct.Add(row.Id);
        }
        var contacts = await LoadAsync(connection, null, scope, distinct, cancellationToken).ConfigureAwait(false);
        var page = new List<StoredContact>(distinct.Count);
        foreach (var id in distinct)
            if (contacts.TryGetValue(id, out var stored) && stored.Contact.MergedInto is null)
                page.Add(stored);
        ContactSearchCursor? next = visible.Count == search.Limit
            ? new ContactSearchCursor(visible[^1].Key, visible[^1].Id)
            : tailCursor is { } tail ? new ContactSearchCursor(tail.Key, tail.Id) : null;
        return new ContactPage(page, next);
    }

    /// <summary>
    /// The search statement (exposed internally so tests can EXPLAIN the exact SQL). An inner keyset query reads at most
    /// <paramref name="maximumRowsExamined"/> rows in index order; the translated access filter is evaluated per examined row, and the
    /// CTE returns only up to <see cref="ContactSearch.Limit"/> visible rows (plus one tail cursor row when the scan bound is reached).
    /// </summary>
    internal static NpgsqlCommand SearchCommand(TicketScope scope, ContactSearch search, AccessFilter filter, int maximumRowsExamined)
    {
        var translated = PostgresAccessFilterTranslator.Translate(filter, FilterColumns).OrDenyAll();
        var command = new NpgsqlCommand();
        AddScope(command, scope);
        foreach (var parameter in translated.Parameters)
            command.Parameters.Add(parameter.Clone());
        AddInt(command, "scan", maximumRowsExamined);
        AddInt(command, "limit", search.Limit);
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

        string inner;
        if (search.Mode == ContactSearchMode.Name)
        {
            inner = $"""
                SELECT s.name_folded AS sort_key, s.contact_id, s.tenant_id, s.project_id, s.environment_id, s.app_id, s.workspace_id, s.labels
                FROM tf_contacts s
                WHERE s.scope_key={ScopeKey} AND s.tenant_id=@tenant AND s.project_id=@project AND s.environment_id=@environment AND s.app_id=@app
                  AND s.workspace_id=@workspace AND s.merged_into IS NULL{string.Format(prefix, "s.name_folded")}{string.Format(after, "s.name_folded", "s.contact_id")}
                ORDER BY s.name_folded, s.contact_id LIMIT @scan
                """;
        }
        else
        {
            AddSmallInt(command, "kind", (short)search.Kind!.Value);
            var ns = search.Namespace is null ? string.Empty : " AND i.namespace=@namespace";
            if (search.Namespace is not null)
                AddText(command, "namespace", search.Namespace);
            inner = $"""
                SELECT i.value AS sort_key, i.contact_id, s.tenant_id, s.project_id, s.environment_id, s.app_id, s.workspace_id, s.labels
                FROM tf_contact_identities i JOIN tf_contacts s ON s.contact_key=i.contact_key AND s.contact_id=i.contact_id
                WHERE i.scope_key={ScopeKey} AND i.tenant_id=@tenant AND i.project_id=@project AND i.environment_id=@environment AND i.app_id=@app
                  AND i.workspace_id=@workspace AND i.kind=@kind{ns}{string.Format(prefix, "i.value")}{string.Format(after, "i.value", "i.contact_id")}
                  AND s.merged_into IS NULL
                ORDER BY i.value, i.contact_id LIMIT @scan
                """;
        }
        command.CommandText = $"""
            WITH examined AS (
                SELECT c.sort_key, c.contact_id, ({translated.Sql}) IS TRUE AS visible,
                       row_number() OVER (ORDER BY c.sort_key, c.contact_id) AS rn
                FROM ({inner}) c {FilterJoin}
            ),
            visible AS (
                SELECT sort_key, contact_id, true AS visible, rn
                FROM examined WHERE visible
                ORDER BY rn LIMIT @limit
            )
            SELECT sort_key, contact_id, visible FROM (
                SELECT sort_key, contact_id, visible, rn, 0 AS is_tail FROM visible
                UNION ALL
                SELECT sort_key, contact_id, false AS visible, rn, 1 AS is_tail FROM examined
                WHERE rn = @scan AND (SELECT count(*) FROM visible) < @limit
            ) r
            ORDER BY is_tail, rn
            """;
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
        AddInt(command, "limit", limit);
        var ids = new List<ContactId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                ids.Add(new ContactId(reader.GetString(0)));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
            {
                throw StoredDataError(error, "tombstone");
            }
        }
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
        AddUuid(command, "merge", mergeId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        try
        {
            if (reader.GetInt16(6) != 1)
                throw new FormatException("Unsupported merge format version.");
            return ContactJsonCodec.DeserializeMergeRecord(reader.GetString(5), mergeId, new ContactId(reader.GetString(0)), new ContactId(reader.GetString(1)),
                reader.GetFieldValue<DateTimeOffset>(2), new AccessActor((AccessActorKind)reader.GetInt16(3), reader.GetString(4)));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
        {
            throw StoredDataError(error, "merge record");
        }
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
        {
            try
            {
                if (reader.GetInt16(7) != 1)
                    throw new FormatException("Unsupported change format version.");
                changes.Add(new ContactChange(scope, reader.GetInt64(0), new ContactChangeDraft(reader.GetGuid(1), (ContactChangeKind)reader.GetInt16(2),
                    new ContactId(reader.GetString(3)), reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(4) ? null : new ContactId(reader.GetString(4)), reader.IsDBNull(5) ? null : reader.GetGuid(5))));
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
            {
                throw StoredDataError(error, "change");
            }
        }
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
        AddBigInt(command, "after", afterSequence);
        AddInt(command, "limit", limit);
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
        AddUuid(command, "event", auditEvent.Id);
        AddInstant(command, "occurred", auditEvent.OccurredAt);
        AddSmallInt(command, "kind", (short)auditEvent.Kind);
        AddSmallInt(command, "actor_kind", (short)auditEvent.Actor.Kind);
        AddText(command, "actor_id", auditEvent.Actor.Id);
        AddText(command, "contact", auditEvent.Contact?.Value);
        AddText(command, "related", auditEvent.Related?.Value);
        AddNullableUuid(command, "merge", auditEvent.Merge);
        AddNullableInt(command, "count", auditEvent.Count);
        AddText(command, "justification", auditEvent.Justification);
        AddNullableUuid(command, "suggestion", auditEvent.Suggestion);
    }

    /// <summary>Loads contacts (with identities) by identifier in one scope in a single pipelined batch RTT.</summary>
    private static async Task<Dictionary<ContactId, StoredContact>> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope,
        IReadOnlyCollection<ContactId> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ContactId, StoredContact>(ids.Count);
        if (ids.Count == 0)
            return result;
        var idArray = new string[ids.Count];
        var idx = 0;
        foreach (var id in ids)
            idArray[idx++] = id.Value;
        const string keys = "SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id";
        await using var batch = new NpgsqlBatch(connection, transaction);
        var identitiesCmd = new NpgsqlBatchCommand($"""
            SELECT contact_id, kind, namespace, value, verified, source, verified_at FROM tf_contact_identities
            WHERE contact_key IN ({keys}) AND {ScopeWhere} AND contact_id = ANY(@ids)
            """);
        AddScope(identitiesCmd, scope);
        AddTextArray(identitiesCmd, "ids", idArray);
        batch.BatchCommands.Add(identitiesCmd);

        var contactsCmd = new NpgsqlBatchCommand($"""
            SELECT {ContactColumns} FROM tf_contacts WHERE contact_key IN ({keys}) AND {ScopeWhere} AND contact_id = ANY(@ids)
            """);
        AddScope(contactsCmd, scope);
        AddTextArray(contactsCmd, "ids", idArray);
        batch.BatchCommands.Add(contactsCmd);

        var identities = new Dictionary<string, List<ContactIdentity>>(StringComparer.Ordinal);
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ContactIdentity identity;
            string owner;
            try
            {
                identity = new ContactIdentity(
                    ContactJsonCodec.RestoreAddress((ContactIdentityKind)reader.GetInt16(1), reader.GetString(2), reader.GetString(3)),
                    ContactJsonCodec.RestoreVerification(reader.GetBoolean(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)));
                owner = reader.GetString(0);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
            {
                throw StoredDataError(error, "identity");
            }
            if (!identities.TryGetValue(owner, out var list))
                identities[owner] = list = [];
            list.Add(identity);
        }

        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (reader.GetInt16(1) != 1)
                        throw new FormatException("Unsupported contact format version.");
                    var id = reader.GetString(0);
                    var rawLabels = reader.GetFieldValue<string[]>(8);
                    LabelId[] labels;
                    if (rawLabels.Length == 0)
                        labels = [];
                    else
                    {
                        labels = new LabelId[rawLabels.Length];
                        for (var i = 0; i < rawLabels.Length; i++)
                            labels[i] = new LabelId(rawLabels[i]);
                    }
                    var contact = new Contact(
                        new ContactId(id),
                        new ContactProfile(reader.NullableText(2), reader.NullableText(3), reader.NullableText(4), reader.NullableText(5), reader.NullableText(6)),
                        identities.GetValueOrDefault(id) ?? [],
                        ContactJsonCodec.DeserializeAttributes(reader.GetString(7)),
                        labels,
                        reader.GetBoolean(9),
                        reader.GetFieldValue<DateTimeOffset>(11),
                        reader.GetFieldValue<DateTimeOffset>(12),
                        reader.NullableText(10) is { } mergedInto ? new ContactId(mergedInto) : null);
                    var stored = new StoredContact(scope, contact, reader.GetInt64(13));
                    result[stored.Id] = stored;
                }
                catch (Exception error) when (error is ArgumentException or FormatException or InvalidCastException or InvalidOperationException)
                {
                    throw StoredDataError(error, "contact");
                }
            }
        }
        return result;
    }

    private static void AddScope(NpgsqlCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void AddScope(NpgsqlBatchCommand command, TicketScope scope)
    {
        AddText(command, "tenant", scope.TenantId);
        AddText(command, "project", scope.ProjectId);
        AddText(command, "environment", scope.EnvironmentId);
        AddText(command, "app", scope.AppId);
        AddText(command, "workspace", scope.WorkspaceId.Value);
    }

    private static void BindAddress(NpgsqlCommand command, ContactIdentityAddress address)
    {
        AddSmallInt(command, "kind", (short)address.Kind);
        AddText(command, "namespace", address.Namespace);
        AddText(command, "value", address.Value);
    }

    private static void AddText(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(value is null ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value } : new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });

    private static void AddText(NpgsqlBatchCommand command, string name, string? value) =>
        command.Parameters.Add(value is null ? new NpgsqlParameter(name, NpgsqlDbType.Text) { Value = DBNull.Value } : new NpgsqlParameter<string>(name, NpgsqlDbType.Text) { TypedValue = value });

    private static void AddTextArray(NpgsqlCommand command, string name, string[] values) =>
        command.Parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values });

    private static void AddTextArray(NpgsqlBatchCommand command, string name, string[] values) =>
        command.Parameters.Add(new NpgsqlParameter<string[]>(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = values });

    private static void AddSmallInt(NpgsqlCommand command, string name, short value) =>
        command.Parameters.Add(new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = value });

    private static void AddInt(NpgsqlCommand command, string name, int value) =>
        command.Parameters.Add(new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = value });

    private static void AddBigInt(NpgsqlCommand command, string name, long value) =>
        command.Parameters.Add(new NpgsqlParameter<long>(name, NpgsqlDbType.Bigint) { TypedValue = value });

    private static void AddBool(NpgsqlCommand command, string name, bool value) =>
        command.Parameters.Add(new NpgsqlParameter<bool>(name, NpgsqlDbType.Boolean) { TypedValue = value });

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid) { TypedValue = value });

    private static void AddNullableSmallInt(NpgsqlCommand command, string name, short? value) =>
        command.Parameters.Add(value is { } v ? new NpgsqlParameter<short>(name, NpgsqlDbType.Smallint) { TypedValue = v } : new NpgsqlParameter(name, NpgsqlDbType.Smallint) { Value = DBNull.Value });

    private static void AddNullableInt(NpgsqlCommand command, string name, int? value) =>
        command.Parameters.Add(value is { } v ? new NpgsqlParameter<int>(name, NpgsqlDbType.Integer) { TypedValue = v } : new NpgsqlParameter(name, NpgsqlDbType.Integer) { Value = DBNull.Value });

    private static void AddNullableUuid(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(value is { } v ? new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid) { TypedValue = v } : new NpgsqlParameter(name, NpgsqlDbType.Uuid) { Value = DBNull.Value });

    private static void AddInstant(NpgsqlCommand command, string name, DateTimeOffset value) =>
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(name, NpgsqlDbType.TimestampTz) { TypedValue = value });

    private static void RequireId(ContactId id)
    {
        if (id == default)
            throw new ArgumentException("A contact identifier is required.", nameof(id));
    }

    private static InvalidOperationException StoredDataError(Exception error, string description) =>
        new($"A stored contact {description} is invalid or incompatible.", error);
}
