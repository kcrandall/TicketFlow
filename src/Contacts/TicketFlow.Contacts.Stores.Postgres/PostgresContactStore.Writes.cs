using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.Contacts.Stores.Postgres;

public sealed partial class PostgresContactStore
{
    public async Task<ContactWriteResult> WriteAsync(TicketScope scope, ContactWriteBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(batch);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Contact rows first (ordered by identifier so concurrent batches lock in the same order).
        var stored = new Dictionary<ContactId, StoredContact>(batch.Puts.Count);
        var sortedPuts = batch.Puts.Count <= 1 ? batch.Puts : [.. batch.Puts.OrderBy(put => put.Contact.Id.Value, StringComparer.Ordinal)];
        foreach (var put in sortedPuts)
        {
            var revision = await PutContactAsync(connection, transaction, scope, put, cancellationToken).ConfigureAwait(false);
            if (revision is null)
                return await MissAsync(connection, transaction, scope, put.Contact.Id, cancellationToken).ConfigureAwait(false);
            stored[put.Contact.Id] = new StoredContact(scope, put.Contact, revision.Value);
        }
        var deleted = new List<ContactId>();
        var sortedDeletes = batch.Deletes.Count <= 1 ? batch.Deletes : [.. batch.Deletes.OrderBy(delete => delete.Id.Value, StringComparer.Ordinal)];
        foreach (var delete in sortedDeletes)
        {
            var removed = await DeleteContactAsync(connection, transaction, scope, delete, cancellationToken).ConfigureAwait(false);
            if (removed is null)
                return await MissAsync(connection, transaction, scope, delete.Id, cancellationToken).ConfigureAwait(false);
            if (removed.Count == 0)
            {
                // The cascade differs from the caller's authorized set: report a conflict at the current revision.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ContactWriteConflict(delete.Id, delete.ExpectedRevision);
            }
            deleted.AddRange(removed);
        }

        // Sync identity rows after contact rows are written.
        // Fast paths: single create skips DELETE; single update diffs in SQL so unchanged identities don't churn indexes/SHA-256.
        if (batch.Puts.Count == 1)
        {
            var put = batch.Puts[0];
            var taken = put.ExpectedRevision is null
                ? await InsertIdentitiesAsync(connection, transaction, scope, put.Contact, cancellationToken).ConfigureAwait(false)
                : await SyncIdentitiesAsync(connection, transaction, scope, put.Contact, cancellationToken).ConfigureAwait(false);
            if (taken is { } conflict)
                return conflict;
        }
        else if (batch.Puts.Count > 1)
        {
            var updatedIds = new List<string>(batch.Puts.Count);
            foreach (var put in batch.Puts)
                if (put.ExpectedRevision is not null)
                    updatedIds.Add(put.Contact.Id.Value);
            if (updatedIds.Count > 0)
            {
                await using var clear = new NpgsqlCommand($"""
                    DELETE FROM tf_contact_identities
                    WHERE contact_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id) AND {ScopeWhere}
                    """, connection, transaction);
                AddScope(clear, scope);
                AddTextArray(clear, "ids", [.. updatedIds]);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var put in batch.Puts)
                if (await InsertIdentitiesAsync(connection, transaction, scope, put.Contact, cancellationToken).ConfigureAwait(false) is { } taken)
                    return taken;
        }

        if (batch.DeleteMerge is { } deleteMerge)
        {
            await using var delete = new NpgsqlCommand($"""
                DELETE FROM tf_contact_merges WHERE merge_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@merge::text) AND merge_id=@merge AND {ScopeWhere}
                """, connection, transaction);
            AddScope(delete, scope);
            AddUuid(delete, "merge", deleteMerge);
            if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return new ContactWriteNotFound(Merge: deleteMerge);
        }
        if (batch.CreateMerge is { } merge)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO tf_contact_merges(tenant_id, project_id, environment_id, app_id, workspace_id, merge_id, target_id, source_id, format_version,
                    merged_at, actor_kind, actor_id, contributions)
                VALUES(@tenant, @project, @environment, @app, @workspace, @merge, @target, @source, 1, @merged_at, @actor_kind, @actor_id, @contributions::jsonb)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            AddScope(insert, scope);
            AddUuid(insert, "merge", merge.Id);
            AddText(insert, "target", merge.Target.Value);
            AddText(insert, "source", merge.Source.Value);
            AddInstant(insert, "merged_at", merge.MergedAt);
            AddSmallInt(insert, "actor_kind", (short)merge.Actor.Kind);
            AddText(insert, "actor_id", merge.Actor.Id);
            AddText(insert, "contributions", ContactJsonCodec.SerializeMergeContributions(merge));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return new ContactWriteRejected(ContactWriteRejection.DuplicateMerge);
        }

        var changes = await AppendChangesAsync(connection, transaction, scope, batch.Changes, cancellationToken).ConfigureAwait(false);
        if (batch.Audit is { } auditEvent)
        {
            await using var insert = new NpgsqlCommand(AuditInsert, connection, transaction);
            BindAudit(insert, auditEvent);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var appliedContacts = new StoredContact[batch.Puts.Count];
        for (var i = 0; i < batch.Puts.Count; i++)
            appliedContacts[i] = stored[batch.Puts[i].Contact.Id];
        return new ContactWriteApplied(appliedContacts, changes) { Deleted = deleted };
    }

    private static async Task<long?> PutContactAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ContactPut put, CancellationToken cancellationToken)
    {
        var sql = put.ExpectedRevision is null
            ? """
              INSERT INTO tf_contacts(tenant_id, project_id, environment_id, app_id, workspace_id, contact_id, format_version, display_name, name_folded,
                  avatar_ref, locale, time_zone, company, attributes, labels, blocked, merged_into, created_at, updated_at, revision)
              VALUES(@tenant, @project, @environment, @app, @workspace, @contact, 1, @display_name, @name_folded, @avatar_ref, @locale, @time_zone, @company,
                  @attributes::jsonb, @labels, @blocked, @merged_into, @created_at, @updated_at, 1)
              ON CONFLICT DO NOTHING RETURNING revision
              """
            : $"""
              UPDATE tf_contacts SET display_name=@display_name, name_folded=@name_folded, avatar_ref=@avatar_ref, locale=@locale, time_zone=@time_zone,
                  company=@company, attributes=@attributes::jsonb, labels=@labels, blocked=@blocked, merged_into=@merged_into, created_at=@created_at,
                  updated_at=@updated_at, revision=revision+1
              WHERE {ContactKey} AND revision=@expected RETURNING revision
              """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var contact = put.Contact;
        AddScope(command, scope);
        AddText(command, "contact", contact.Id.Value);
        AddText(command, "display_name", contact.Profile.DisplayName);
        AddText(command, "name_folded", ContactSearch.FoldName(contact.Profile.DisplayName));
        AddText(command, "avatar_ref", contact.Profile.AvatarRef);
        AddText(command, "locale", contact.Profile.Locale);
        AddText(command, "time_zone", contact.Profile.TimeZone);
        AddText(command, "company", contact.Profile.Company);
        AddText(command, "attributes", ContactJsonCodec.SerializeAttributes(contact.Attributes));
        var labelValues = contact.Labels.Count == 0 ? [] : new string[contact.Labels.Count];
        for (var i = 0; i < contact.Labels.Count; i++)
            labelValues[i] = contact.Labels[i].Value;
        AddTextArray(command, "labels", labelValues);
        AddBool(command, "blocked", contact.Blocked);
        AddText(command, "merged_into", contact.MergedInto?.Value);
        AddInstant(command, "created_at", contact.CreatedAt);
        AddInstant(command, "updated_at", contact.UpdatedAt);
        if (put.ExpectedRevision is { } expected)
            AddBigInt(command, "expected", expected);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as long?;
    }

    /// <summary>
    /// Fenced delete of the root plus recursive removal of tombstones merged into it and every merge record naming them. Returns
    /// <see langword="null"/> on a missed fence, an empty list when the cascade differs from <see cref="ContactDelete.ExpectedCascade"/>,
    /// otherwise the removed identifiers (root first, then tombstones by identifier).
    /// </summary>
    private static async Task<List<ContactId>?> DeleteContactAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ContactDelete delete, CancellationToken cancellationToken)
    {
        string[] doomed;
        await using (var cascade = new NpgsqlCommand($"""
            WITH RECURSIVE doomed(contact_id) AS (
                SELECT @contact::text COLLATE "C"
                UNION
                SELECT c.contact_id FROM tf_contacts c JOIN doomed d
                  ON c.merged_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,d.contact_id) AND c.merged_into=d.contact_id
                WHERE {ContactScopeWhere})
            SELECT contact_id FROM doomed LIMIT 10001
            """, connection, transaction))
        {
            AddScope(cascade, scope);
            AddText(cascade, "contact", delete.Id.Value);
            var ids = new List<string>();
            await using var reader = await cascade.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(reader.GetString(0));
            if (ids.Count > 10_000)
                throw new InvalidOperationException("The contact merge history is too large to delete in one transaction.");
            doomed = [.. ids];
        }
        var tombstones = new HashSet<ContactId>(Math.Max(0, doomed.Length - 1));
        foreach (var id in doomed)
            if (!string.Equals(id, delete.Id.Value, StringComparison.Ordinal))
                tombstones.Add(new ContactId(id));
        if (delete.ExpectedCascade is { } expected && !expected.SetEquals(tombstones))
        {
            await using var exists = new NpgsqlCommand($"SELECT revision FROM tf_contacts WHERE {ContactKey} AND revision=@expected", connection, transaction);
            AddScope(exists, scope);
            AddText(exists, "contact", delete.Id.Value);
            AddBigInt(exists, "expected", delete.ExpectedRevision);
            return await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long ? [] : null;
        }
        await using (var root = new NpgsqlCommand($"DELETE FROM tf_contacts WHERE {ContactKey} AND revision=@expected", connection, transaction))
        {
            AddScope(root, scope);
            AddText(root, "contact", delete.Id.Value);
            AddBigInt(root, "expected", delete.ExpectedRevision);
            if (await root.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return null;
        }
        await using (var rest = new NpgsqlBatch(connection, transaction))
        {
            if (tombstones.Count > 0)
            {
                var delTombstones = new NpgsqlBatchCommand($"""
                    DELETE FROM tf_contacts WHERE contact_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id) AND {ScopeWhere}
                    """);
                AddScope(delTombstones, scope);
                AddTextArray(delTombstones, "ids", doomed);
                rest.BatchCommands.Add(delTombstones);
            }
            var delTargetMerges = new NpgsqlBatchCommand($"""
                DELETE FROM tf_contact_merges WHERE target_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id) AND {ScopeWhere}
                """);
            AddScope(delTargetMerges, scope);
            AddTextArray(delTargetMerges, "ids", doomed);
            rest.BatchCommands.Add(delTargetMerges);

            var delSourceMerges = new NpgsqlBatchCommand($"""
                DELETE FROM tf_contact_merges WHERE source_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id) AND {ScopeWhere}
                """);
            AddScope(delSourceMerges, scope);
            AddTextArray(delSourceMerges, "ids", doomed);
            rest.BatchCommands.Add(delSourceMerges);

            await rest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var result = new List<ContactId>(tombstones.Count + 1) { delete.Id };
        if (tombstones.Count > 0)
        {
            var sortedTombstones = new List<ContactId>(tombstones);
            sortedTombstones.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            result.AddRange(sortedTombstones);
        }
        return result;
    }

    private static async Task<ContactWriteResult?> SyncIdentitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Contact contact, CancellationToken cancellationToken)
    {
        if (contact.Identities.Count == 0)
        {
            await using var clear = new NpgsqlCommand($"DELETE FROM tf_contact_identities WHERE {ContactKey}", connection, transaction);
            AddScope(clear, scope);
            AddText(clear, "contact", contact.Id.Value);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        var (kinds, namespaces, values, verified, sources, ats) = ExtractIdentityArrays(contact.Identities);
        await using (var syncBatch = new NpgsqlBatch(connection, transaction))
        {
            var deleteStale = new NpgsqlBatchCommand($"""
                DELETE FROM tf_contact_identities i
                WHERE i.contact_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@contact) AND i.contact_id=@contact
                  AND i.tenant_id=@tenant AND i.project_id=@project AND i.environment_id=@environment AND i.app_id=@app AND i.workspace_id=@workspace
                  AND NOT EXISTS (
                      SELECT 1 FROM unnest(@kinds, @namespaces, @values, @verified, @sources, @ats) AS d(kind, namespace, value, verified, source, verified_at)
                      WHERE d.kind=i.kind AND d.namespace=i.namespace AND d.value=i.value
                        AND d.verified=i.verified AND d.source=i.source AND d.verified_at=i.verified_at
                  )
                """);
            BindIdentityBatchParams(deleteStale, scope, contact.Id.Value, kinds, namespaces, values, verified, sources, ats);
            syncBatch.BatchCommands.Add(deleteStale);

            var insertMissing = new NpgsqlBatchCommand($"""
                WITH needed AS (
                    SELECT d.kind, d.namespace, d.value, d.verified, d.source, d.verified_at
                    FROM unnest(@kinds, @namespaces, @values, @verified, @sources, @ats) AS d(kind, namespace, value, verified, source, verified_at)
                    WHERE NOT EXISTS (
                        SELECT 1 FROM tf_contact_identities i
                        WHERE i.contact_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@contact) AND i.contact_id=@contact
                          AND i.tenant_id=@tenant AND i.project_id=@project AND i.environment_id=@environment AND i.app_id=@app AND i.workspace_id=@workspace
                          AND i.kind=d.kind AND i.namespace=d.namespace AND i.value=d.value
                          AND i.verified=d.verified AND i.source=d.source AND i.verified_at=d.verified_at
                    )
                ),
                inserted AS (
                    INSERT INTO tf_contact_identities(tenant_id, project_id, environment_id, app_id, workspace_id, contact_id, kind, namespace, value, verified, source, verified_at)
                    SELECT @tenant, @project, @environment, @app, @workspace, @contact, kind, namespace, value, verified, source, verified_at
                    FROM needed
                    ON CONFLICT (identity_key) WHERE verified DO NOTHING
                    RETURNING 1
                )
                SELECT (SELECT count(*) FROM inserted) = (SELECT count(*) FROM needed)
                """);
            BindIdentityBatchParams(insertMissing, scope, contact.Id.Value, kinds, namespaces, values, verified, sources, ats);
            syncBatch.BatchCommands.Add(insertMissing);

            await using var reader = await syncBatch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetBoolean(0))
                return null;
        }
        return await FindTakenIdentityAsync(connection, transaction, scope, contact, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ContactWriteResult?> InsertIdentitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Contact contact, CancellationToken cancellationToken)
    {
        if (contact.Identities.Count == 0)
            return null;
        var (kinds, namespaces, values, verified, sources, ats) = ExtractIdentityArrays(contact.Identities);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_contact_identities(tenant_id, project_id, environment_id, app_id, workspace_id, contact_id, kind, namespace, value, verified, source, verified_at)
            SELECT @tenant, @project, @environment, @app, @workspace, @contact, kind, namespace, value, verified, source, verified_at
            FROM unnest(@kinds, @namespaces, @values, @verified, @sources, @ats) AS item(kind, namespace, value, verified, source, verified_at)
            ON CONFLICT (identity_key) WHERE verified DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "contact", contact.Id.Value);
            insert.Parameters.Add(new NpgsqlParameter<short[]>("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = kinds });
            AddTextArray(insert, "namespaces", namespaces);
            AddTextArray(insert, "values", values);
            insert.Parameters.Add(new NpgsqlParameter<bool[]>("verified", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { TypedValue = verified });
            AddTextArray(insert, "sources", sources);
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = ats });
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == contact.Identities.Count)
                return null;
        }
        return await FindTakenIdentityAsync(connection, transaction, scope, contact, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ContactWriteResult> FindTakenIdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Contact contact, CancellationToken cancellationToken)
    {
        // A verified identity is already held by another contact: report the first one (in contact order) and roll back.
        foreach (var identity in contact.Identities)
        {
            if (!identity.Verification.IsVerified)
                continue;
            await using var owner = new NpgsqlCommand($"""
                SELECT contact_id FROM tf_contact_identities
                WHERE identity_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@kind::text,@namespace,@value) AND {ScopeWhere}
                  AND kind=@kind AND namespace=@namespace AND value=@value AND verified AND contact_id<>@contact
                """, connection, transaction);
            AddScope(owner, scope);
            BindAddress(owner, identity.Address);
            AddText(owner, "contact", contact.Id.Value);
            if (await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string holder)
                return new ContactIdentityTaken(identity.Address, new ContactId(holder));
        }
        throw new InvalidOperationException("Contact identities could not be written.");
    }

    private static (short[] Kinds, string[] Namespaces, string[] Values, bool[] Verified, string[] Sources, DateTimeOffset[] Ats) ExtractIdentityArrays(IReadOnlyList<ContactIdentity> identities)
    {
        var count = identities.Count;
        var kinds = new short[count];
        var namespaces = new string[count];
        var values = new string[count];
        var verified = new bool[count];
        var sources = new string[count];
        var ats = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            var identity = identities[i];
            kinds[i] = (short)identity.Address.Kind;
            namespaces[i] = identity.Address.Namespace;
            values[i] = identity.Address.Value;
            verified[i] = identity.Verification.IsVerified;
            sources[i] = identity.Verification.Source;
            ats[i] = identity.Verification.At;
        }
        return (kinds, namespaces, values, verified, sources, ats);
    }

    private static void BindIdentityBatchParams(NpgsqlBatchCommand command, TicketScope scope, string contactId,
        short[] kinds, string[] namespaces, string[] values, bool[] verified, string[] sources, DateTimeOffset[] ats)
    {
        AddScope(command, scope);
        AddText(command, "contact", contactId);
        command.Parameters.Add(new NpgsqlParameter<short[]>("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = kinds });
        AddTextArray(command, "namespaces", namespaces);
        AddTextArray(command, "values", values);
        command.Parameters.Add(new NpgsqlParameter<bool[]>("verified", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { TypedValue = verified });
        AddTextArray(command, "sources", sources);
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = ats });
    }

    private static async Task<IReadOnlyList<ContactChange>> AppendChangesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope,
        IReadOnlyList<ContactChangeDraft> drafts, CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
            return [];
        var count = drafts.Count;
        var events = new Guid[count];
        var kinds = new short[count];
        var contacts = new string[count];
        var related = new string?[count];
        var merges = new Guid?[count];
        var occurred = new DateTimeOffset[count];
        for (var i = 0; i < count; i++)
        {
            var draft = drafts[i];
            events[i] = draft.Id;
            kinds[i] = (short)draft.Kind;
            contacts[i] = draft.Contact.Value;
            related[i] = draft.Related?.Value;
            merges[i] = draft.Merge;
            occurred[i] = draft.OccurredAt;
        }
        long last;
        await using (var command = new NpgsqlCommand("""
            WITH head AS (
                INSERT INTO tf_contact_change_heads(tenant_id, project_id, environment_id, app_id, workspace_id, last_sequence)
                VALUES(@tenant, @project, @environment, @app, @workspace, @count)
                ON CONFLICT (scope_key) DO UPDATE SET last_sequence=tf_contact_change_heads.last_sequence+@count
                RETURNING last_sequence
            ),
            inserted AS (
                INSERT INTO tf_contact_changes(tenant_id, project_id, environment_id, app_id, workspace_id, sequence, event_id, format_version, kind, contact_id,
                    related_id, merge_id, occurred_at)
                SELECT @tenant, @project, @environment, @app, @workspace, (head.last_sequence - @count + 1) + item.ordinality - 1,
                    item.event_id, 1, item.kind, item.contact_id, item.related_id, item.merge_id, item.occurred_at
                FROM head
                CROSS JOIN unnest(@events, @kinds, @contacts, @related, @merges, @occurred) WITH ORDINALITY AS item(event_id, kind, contact_id, related_id, merge_id, occurred_at, ordinality)
            )
            SELECT last_sequence FROM head
            """, connection, transaction))
        {
            AddScope(command, scope);
            AddBigInt(command, "count", count);
            command.Parameters.Add(new NpgsqlParameter<Guid[]>("events", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = events });
            command.Parameters.Add(new NpgsqlParameter<short[]>("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { TypedValue = kinds });
            AddTextArray(command, "contacts", contacts);
            command.Parameters.Add(new NpgsqlParameter<string?[]>("related", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = related });
            command.Parameters.Add(new NpgsqlParameter<Guid?[]>("merges", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { TypedValue = merges });
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { TypedValue = occurred });
            last = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        var first = last - count + 1;
        var result = new ContactChange[count];
        for (var i = 0; i < count; i++)
            result[i] = new ContactChange(scope, first + i, drafts[i]);
        return result;
    }

    /// <summary>Distinguishes a missed compare-and-set (not found or conflict) after rolling back.</summary>
    private static async Task<ContactWriteResult> MissAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ContactId id, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT revision FROM tf_contacts WHERE {ContactKey}", connection);
        AddScope(command, scope);
        AddText(command, "contact", id.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long revision
            ? new ContactWriteConflict(id, revision)
            : new ContactWriteNotFound(id);
    }
}
