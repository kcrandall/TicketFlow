using Npgsql;
using NpgsqlTypes;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Core;

namespace TicketFlow.Contacts.Stores.Postgres;

/// <summary>
/// <see cref="IContactLinkingStore"/>: the workspace policy row (compare-and-set) and link suggestions. A partial unique index keeps
/// one pending suggestion per (candidate, account) pair; suggestion listing scans at most a bounded number of rows in
/// (scope, status, id) order and evaluates the translated access filter on both joined contacts in the same statement.
/// </summary>
public sealed partial class PostgresContactStore
{
    private const string SuggestionColumns = "suggestion_id, format_version, candidate_id, account_id, evidence_kind, evidence_namespace, evidence_value, evidence_verified, "
        + "status, created_at, updated_at, created_by_kind, created_by_id, decided_by_kind, decided_by_id, merge_id, revision";

    private const string SuggestionKey = "suggestion_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@suggestion::text) AND suggestion_id=@suggestion AND " + ScopeWhere;

    private static readonly PostgresAccessFilterColumns AccountFilterColumns = new()
    {
        TenantId = "a.tenant_id",
        ProjectId = "a.project_id",
        EnvironmentId = "a.environment_id",
        AppId = "a.app_id",
        WorkspaceId = "a.workspace_id",
        TeamId = "n.team_id",
        InboxId = "n.inbox_id",
        AssigneeId = "n.assignee_id",
        Labels = "a.labels",
        Attributes = "n.attributes",
    };

    public async Task<StoredContactLinkingPolicy?> GetPolicyAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var command = source.CreateCommand($"SELECT policy::text, format_version, revision FROM tf_contact_linking_policies WHERE scope_key={ScopeKey} AND {ScopeWhere}");
        AddScope(command, scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return Decode(() =>
        {
            if (reader.GetInt16(1) != 1)
                throw new FormatException("Unsupported linking policy format version.");
            return new StoredContactLinkingPolicy(scope, ContactLinkingCodec.DeserializePolicy(reader.GetString(0)), reader.GetInt64(2));
        }, "linking policy");
    }

    public async Task<ContactLinkingWriteResult<StoredContactLinkingPolicy>> PutPolicyAsync(TicketScope scope, ContactLinkingPolicy policy, long? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (policy.Value is null)
            throw new ArgumentException("A policy is required.", nameof(policy));
        if (expectedRevision is < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        var json = ContactLinkingCodec.SerializePolicy(policy);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var write = new NpgsqlCommand(expectedRevision is null
            ? """
              INSERT INTO tf_contact_linking_policies(tenant_id, project_id, environment_id, app_id, workspace_id, format_version, policy, revision)
              VALUES(@tenant, @project, @environment, @app, @workspace, 1, @policy::jsonb, 1) ON CONFLICT DO NOTHING RETURNING revision
              """
            : $"""
              UPDATE tf_contact_linking_policies SET policy=@policy::jsonb, revision=revision+1
              WHERE scope_key={ScopeKey} AND {ScopeWhere} AND revision=@expected RETURNING revision
              """, connection))
        {
            AddScope(write, scope);
            AddText(write, "policy", json);
            if (expectedRevision is { } expected)
                write.Parameters.AddWithValue("expected", expected);
            if (await write.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision)
                return new LinkingWriteApplied<StoredContactLinkingPolicy>(new(scope, policy, revision), expectedRevision is null);
        }
        await using var current = new NpgsqlCommand($"SELECT revision FROM tf_contact_linking_policies WHERE scope_key={ScopeKey} AND {ScopeWhere}", connection);
        AddScope(current, scope);
        return await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long existing
            ? new LinkingWriteConflict(existing)
            : LinkingWriteNotFound.Instance;
    }

    public async Task<StoredContactLinkSuggestion?> GetSuggestionAsync(TicketScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetSuggestionAsync(connection, null, scope, id, lockRow: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContactLinkingWriteResult<StoredContactLinkSuggestion>> CreateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(suggestion);
        if (suggestion.Status != ContactLinkSuggestionStatus.Pending)
            throw new ArgumentException("New suggestions are pending.", nameof(suggestion));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(array_to_string(@parts, chr(31)), 0))", connection, transaction))
        {
            AddTextArray(lockCommand, "parts", ["contact-link-suggestions", scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value]);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var pending = new NpgsqlCommand($"""
            SELECT suggestion_id FROM tf_contact_link_suggestions
            WHERE pair_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@candidate,@account) AND candidate_id=@candidate AND account_id=@account
              AND status=1 AND {ScopeWhere}
            """, connection, transaction))
        {
            AddScope(pending, scope);
            AddText(pending, "candidate", suggestion.Candidate.Value);
            AddText(pending, "account", suggestion.Account.Value);
            if (await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid existingId)
            {
                var existing = await GetSuggestionAsync(connection, transaction, scope, existingId, lockRow: false, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LinkingWriteApplied<StoredContactLinkSuggestion>(existing!, false);
            }
        }
        if (await GetSuggestionAsync(connection, transaction, scope, suggestion.Id, lockRow: false, cancellationToken).ConfigureAwait(false) is { } duplicate)
            return new LinkingWriteConflict(duplicate.Revision);
        await using (var count = new NpgsqlCommand($"SELECT count(*) FROM tf_contact_link_suggestions WHERE scope_key={ScopeKey} AND {ScopeWhere} AND status=1", connection, transaction))
        {
            AddScope(count, scope);
            if ((long)(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! >= IContactLinkingStore.MaximumPendingSuggestions)
                return new ContactWriteRejected(ContactWriteRejection.CapacityExceeded);
        }
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_contact_link_suggestions(tenant_id, project_id, environment_id, app_id, workspace_id, {SuggestionColumns})
            VALUES(@tenant, @project, @environment, @app, @workspace, @suggestion, 1, @candidate, @account, @evidence_kind, @evidence_namespace, @evidence_value,
                @evidence_verified, @status, @created, @updated, @created_by_kind, @created_by_id, @decided_by_kind, @decided_by_id, @merge, 1)
            """, connection, transaction))
        {
            BindSuggestion(insert, scope, suggestion);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LinkingWriteApplied<StoredContactLinkSuggestion>(new(scope, suggestion, 1));
    }

    public async Task<ContactLinkingWriteResult<StoredContactLinkSuggestion>> UpdateSuggestionAsync(TicketScope scope, ContactLinkSuggestion suggestion, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await GetSuggestionAsync(connection, transaction, scope, suggestion.Id, lockRow: true, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return LinkingWriteNotFound.Instance;
        if (existing.Revision != expectedRevision)
            return new LinkingWriteConflict(existing.Revision);
        var before = existing.Suggestion;
        if (before.Candidate != suggestion.Candidate || before.Account != suggestion.Account || before.Evidence != suggestion.Evidence
            || before.EvidenceVerified != suggestion.EvidenceVerified || before.CreatedAt != suggestion.CreatedAt || before.CreatedBy != suggestion.CreatedBy)
            throw new ArgumentException("A suggestion's pair, evidence and creation fields are immutable.", nameof(suggestion));
        if (suggestion.Status == ContactLinkSuggestionStatus.Pending && before.Status != ContactLinkSuggestionStatus.Pending)
            throw new ArgumentException("A decided suggestion cannot become pending again.", nameof(suggestion));
        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_contact_link_suggestions SET status=@status, updated_at=@updated, decided_by_kind=@decided_by_kind, decided_by_id=@decided_by_id,
                merge_id=@merge, revision=revision+1
            WHERE {SuggestionKey}
            """, connection, transaction))
        {
            BindSuggestion(update, scope, suggestion);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LinkingWriteApplied<StoredContactLinkSuggestion>(new(scope, suggestion, checked(expectedRevision + 1)), false);
    }

    public async Task<ContactLinkSuggestionStorePage> ListSuggestionsAsync(TicketScope scope, ContactLinkSuggestionQuery query, AccessFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filter);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = ListSuggestionsCommand(scope, query, filter, maximumRowsExamined);
        command.Connection = connection;
        var examined = new List<(StoredContactLinkSuggestion Stored, bool Visible)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                examined.Add((ReadSuggestion(reader, scope, 1), reader.GetBoolean(0)));
        }
        var page = new List<StoredContactLinkSuggestion>();
        foreach (var row in examined)
        {
            if (!row.Visible)
                continue;
            page.Add(row.Stored);
            if (page.Count == query.Limit)
                break;
        }
        Guid? next = page.Count == query.Limit ? page[^1].Suggestion.Id
            : examined.Count == maximumRowsExamined ? examined[^1].Stored.Suggestion.Id : null;
        return new ContactLinkSuggestionStorePage(page, next);
    }

    public async Task<int> PruneDecidedSuggestionsAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IContactLinkingStore.MaximumPruneBatch);
        await using var command = PruneSuggestionsCommand(decidedBefore, limit);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        command.Connection = connection;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The retention statement (exposed internally so tests can EXPLAIN it): oldest decided rows first, skipping locked rows.</summary>
    internal static NpgsqlCommand PruneSuggestionsCommand(DateTimeOffset decidedBefore, int limit)
    {
        var command = new NpgsqlCommand("""
            DELETE FROM tf_contact_link_suggestions WHERE suggestion_key IN (
                SELECT suggestion_key FROM tf_contact_link_suggestions WHERE status <> 1 AND updated_at < @before
                ORDER BY updated_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            """);
        AddInstant(command, "before", decidedBefore);
        command.Parameters.AddWithValue("limit", limit);
        return command;
    }

    /// <summary>The suggestion listing statement (exposed internally so tests can EXPLAIN the exact SQL).</summary>
    internal static NpgsqlCommand ListSuggestionsCommand(TicketScope scope, ContactLinkSuggestionQuery query, AccessFilter filter, int maximumRowsExamined)
    {
        var candidateFilter = PostgresAccessFilterTranslator.Translate(filter, FilterColumns, "access_c_").OrDenyAll();
        var accountFilter = PostgresAccessFilterTranslator.Translate(filter, AccountFilterColumns, "access_a_").OrDenyAll();
        var command = new NpgsqlCommand();
        AddScope(command, scope);
        foreach (var parameter in candidateFilter.Parameters.Concat(accountFilter.Parameters))
            command.Parameters.Add(parameter.Clone());
        command.Parameters.AddWithValue("status", (short)query.Status);
        command.Parameters.AddWithValue("scan", maximumRowsExamined);
        var after = string.Empty;
        if (query.After is { } cursor)
        {
            after = " AND s.suggestion_id > @after";
            command.Parameters.AddWithValue("after", cursor);
        }
        const string scopeColumns = "s.tenant_id=@tenant AND s.project_id=@project AND s.environment_id=@environment AND s.app_id=@app AND s.workspace_id=@workspace";
        string scan;
        if (query.Contact is { } id)
        {
            // Two index-backed keyset scans (candidate_key / account_key, status, id) merged in id order; a pair never repeats a contact.
            AddText(command, "contact", id.Value);
            const string contactKey = "tf_key_v1(@tenant,@project,@environment,@app,@workspace,@contact)";
            scan = $"""
                SELECT u.* FROM (
                    (SELECT s.* FROM tf_contact_link_suggestions s
                     WHERE s.candidate_key={contactKey} AND s.candidate_id=@contact AND {scopeColumns} AND s.status=@status{after}
                     ORDER BY s.suggestion_id LIMIT @scan)
                    UNION ALL
                    (SELECT s.* FROM tf_contact_link_suggestions s
                     WHERE s.account_key={contactKey} AND s.account_id=@contact AND {scopeColumns} AND s.status=@status{after}
                     ORDER BY s.suggestion_id LIMIT @scan)) u
                ORDER BY u.suggestion_id LIMIT @scan
                """;
        }
        else
            scan = $"""
                SELECT s.* FROM tf_contact_link_suggestions s
                WHERE s.scope_key={ScopeKey} AND {scopeColumns} AND s.status=@status{after}
                ORDER BY s.suggestion_id LIMIT @scan
                """;
        var columns = string.Join(", ", SuggestionColumns.Split(", ").Select(column => "x." + column));
        command.CommandText = $"""
            SELECT (c.contact_id IS NOT NULL AND a.contact_id IS NOT NULL AND ({candidateFilter.Sql}) IS TRUE AND ({accountFilter.Sql}) IS TRUE), {columns} FROM (
                {scan}) x
            LEFT JOIN tf_contacts c ON c.contact_key=x.candidate_key AND c.contact_id=x.candidate_id
            LEFT JOIN tf_contacts a ON a.contact_key=x.account_key AND a.contact_id=x.account_id
            {FilterJoin}
            ORDER BY x.suggestion_id
            """;
        return command;
    }

    private static async Task<StoredContactLinkSuggestion?> GetSuggestionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, Guid id,
        bool lockRow, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {SuggestionColumns} FROM tf_contact_link_suggestions WHERE {SuggestionKey}{(lockRow ? " FOR UPDATE" : string.Empty)}",
            connection, transaction);
        AddScope(command, scope);
        command.Parameters.AddWithValue("suggestion", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSuggestion(reader, scope, 0) : null;
    }

    private static StoredContactLinkSuggestion ReadSuggestion(NpgsqlDataReader reader, TicketScope scope, int offset) => Decode(() =>
    {
        if (reader.GetInt16(offset + 1) != 1)
            throw new FormatException("Unsupported suggestion format version.");
        AccessActor? decidedBy = reader.IsDBNull(offset + 13) ? null : new AccessActor((AccessActorKind)reader.GetInt16(offset + 13), reader.GetString(offset + 14));
        var suggestion = new ContactLinkSuggestion(
            reader.GetGuid(offset),
            new ContactId(reader.GetString(offset + 2)),
            new ContactId(reader.GetString(offset + 3)),
            ContactJsonCodec.RestoreAddress((ContactIdentityKind)reader.GetInt16(offset + 4), reader.GetString(offset + 5), reader.GetString(offset + 6)),
            reader.GetBoolean(offset + 7),
            (ContactLinkSuggestionStatus)reader.GetInt16(offset + 8),
            reader.GetFieldValue<DateTimeOffset>(offset + 9),
            reader.GetFieldValue<DateTimeOffset>(offset + 10),
            new AccessActor((AccessActorKind)reader.GetInt16(offset + 11), reader.GetString(offset + 12)),
            decidedBy,
            reader.IsDBNull(offset + 15) ? null : reader.GetGuid(offset + 15));
        return new StoredContactLinkSuggestion(scope, suggestion, reader.GetInt64(offset + 16));
    }, "link suggestion");

    private static void BindSuggestion(NpgsqlCommand command, TicketScope scope, ContactLinkSuggestion suggestion)
    {
        AddScope(command, scope);
        command.Parameters.AddWithValue("suggestion", suggestion.Id);
        AddText(command, "candidate", suggestion.Candidate.Value);
        AddText(command, "account", suggestion.Account.Value);
        command.Parameters.AddWithValue("evidence_kind", (short)suggestion.Evidence.Kind);
        AddText(command, "evidence_namespace", suggestion.Evidence.Namespace);
        AddText(command, "evidence_value", suggestion.Evidence.Value);
        command.Parameters.AddWithValue("evidence_verified", suggestion.EvidenceVerified);
        command.Parameters.AddWithValue("status", (short)suggestion.Status);
        AddInstant(command, "created", suggestion.CreatedAt);
        AddInstant(command, "updated", suggestion.UpdatedAt);
        command.Parameters.AddWithValue("created_by_kind", (short)suggestion.CreatedBy.Kind);
        AddText(command, "created_by_id", suggestion.CreatedBy.Id);
        command.Parameters.Add(new NpgsqlParameter("decided_by_kind", NpgsqlDbType.Smallint) { Value = suggestion.DecidedBy is { } decided ? (object)(short)decided.Kind : DBNull.Value });
        AddText(command, "decided_by_id", suggestion.DecidedBy?.Id);
        command.Parameters.Add(new NpgsqlParameter("merge", NpgsqlDbType.Uuid) { Value = (object?)suggestion.Merge ?? DBNull.Value });
    }
}
