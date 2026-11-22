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
        var stored = new Dictionary<ContactId, StoredContact>();
        foreach (var put in batch.Puts.OrderBy(put => put.Contact.Id.Value, StringComparer.Ordinal))
        {
            var revision = await PutContactAsync(connection, transaction, scope, put, cancellationToken).ConfigureAwait(false);
            if (revision is null)
                return await MissAsync(connection, transaction, scope, put.Contact.Id, cancellationToken).ConfigureAwait(false);
            stored[put.Contact.Id] = new StoredContact(scope, put.Contact, revision.Value);
        }
        var deleted = new List<ContactId>();
        foreach (var delete in batch.Deletes.OrderBy(delete => delete.Id.Value, StringComparer.Ordinal))
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

        // Replace identity rows only after every contact row is written, so identities can move between contacts in one batch.
        if (batch.Puts.Count > 0)
        {
            await using var clear = new NpgsqlCommand($"""
                DELETE FROM tf_contact_identities
                WHERE contact_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id) AND {ScopeWhere}
                """, connection, transaction);
            AddScope(clear, scope);
            AddTextArray(clear, "ids", batch.Puts.Select(put => put.Contact.Id.Value));
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var put in batch.Puts)
            if (await InsertIdentitiesAsync(connection, transaction, scope, put.Contact, cancellationToken).ConfigureAwait(false) is { } taken)
                return taken;

        if (batch.DeleteMerge is { } deleteMerge)
        {
            await using var delete = new NpgsqlCommand($"""
                DELETE FROM tf_contact_merges WHERE merge_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@merge::text) AND merge_id=@merge AND {ScopeWhere}
                """, connection, transaction);
            AddScope(delete, scope);
            delete.Parameters.AddWithValue("merge", deleteMerge);
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
            insert.Parameters.AddWithValue("merge", merge.Id);
            AddText(insert, "target", merge.Target.Value);
            AddText(insert, "source", merge.Source.Value);
            AddInstant(insert, "merged_at", merge.MergedAt);
            insert.Parameters.AddWithValue("actor_kind", (short)merge.Actor.Kind);
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
        return new ContactWriteApplied([.. batch.Puts.Select(put => stored[put.Contact.Id])], changes) { Deleted = deleted };
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
        AddTextArray(command, "labels", contact.Labels.Select(label => label.Value));
        command.Parameters.AddWithValue("blocked", contact.Blocked);
        AddText(command, "merged_into", contact.MergedInto?.Value);
        AddInstant(command, "created_at", contact.CreatedAt);
        AddInstant(command, "updated_at", contact.UpdatedAt);
        if (put.ExpectedRevision is { } expected)
            command.Parameters.AddWithValue("expected", expected);
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
                WHERE c.{ScopeWhere.Replace(" AND ", " AND c.", StringComparison.Ordinal)})
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
        var tombstones = doomed.Where(id => id != delete.Id.Value).Select(id => new ContactId(id)).ToHashSet();
        if (delete.ExpectedCascade is { } expected && !expected.SetEquals(tombstones))
        {
            await using var exists = new NpgsqlCommand($"SELECT revision FROM tf_contacts WHERE {ContactKey} AND revision=@expected", connection, transaction);
            AddScope(exists, scope);
            AddText(exists, "contact", delete.Id.Value);
            exists.Parameters.AddWithValue("expected", delete.ExpectedRevision);
            return await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long ? [] : null;
        }
        await using (var root = new NpgsqlCommand($"DELETE FROM tf_contacts WHERE {ContactKey} AND revision=@expected", connection, transaction))
        {
            AddScope(root, scope);
            AddText(root, "contact", delete.Id.Value);
            root.Parameters.AddWithValue("expected", delete.ExpectedRevision);
            if (await root.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                return null;
        }
        await using (var rest = new NpgsqlCommand($"""
            DELETE FROM tf_contacts WHERE contact_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id) AND {ScopeWhere};
            DELETE FROM tf_contact_merges
            WHERE (target_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id)
                OR source_key IN (SELECT tf_key_v1(@tenant,@project,@environment,@app,@workspace,id) FROM unnest(@ids) AS id)) AND {ScopeWhere};
            """, connection, transaction))
        {
            AddScope(rest, scope);
            AddTextArray(rest, "ids", doomed);
            await rest.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return [delete.Id, .. tombstones.OrderBy(id => id.Value, StringComparer.Ordinal)];
    }

    private static async Task<ContactWriteResult?> InsertIdentitiesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Contact contact, CancellationToken cancellationToken)
    {
        if (contact.Identities.Count == 0)
            return null;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_contact_identities(tenant_id, project_id, environment_id, app_id, workspace_id, contact_id, kind, namespace, value, verified, source, verified_at)
            SELECT @tenant, @project, @environment, @app, @workspace, @contact, kind, namespace, value, verified, source, verified_at
            FROM unnest(@kinds, @namespaces, @values, @verified, @sources, @ats) AS item(kind, namespace, value, verified, source, verified_at)
            ON CONFLICT (identity_key) WHERE verified DO NOTHING
            """, connection, transaction))
        {
            AddScope(insert, scope);
            AddText(insert, "contact", contact.Id.Value);
            insert.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = contact.Identities.Select(identity => (short)identity.Address.Kind).ToArray() });
            AddTextArray(insert, "namespaces", contact.Identities.Select(identity => identity.Address.Namespace));
            AddTextArray(insert, "values", contact.Identities.Select(identity => identity.Address.Value));
            insert.Parameters.Add(new NpgsqlParameter("verified", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = contact.Identities.Select(identity => identity.Verification.IsVerified).ToArray() });
            AddTextArray(insert, "sources", contact.Identities.Select(identity => identity.Verification.Source));
            insert.Parameters.Add(new NpgsqlParameter("ats", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = contact.Identities.Select(identity => identity.Verification.At).ToArray() });
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == contact.Identities.Count)
                return null;
        }
        // A verified identity is already held by another contact: report the first one (in contact order) and roll back.
        foreach (var identity in contact.Identities.Where(identity => identity.Verification.IsVerified))
        {
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

    private static async Task<IReadOnlyList<ContactChange>> AppendChangesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope,
        IReadOnlyList<ContactChangeDraft> drafts, CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
            return [];
        long last;
        await using (var head = new NpgsqlCommand("""
            INSERT INTO tf_contact_change_heads(tenant_id, project_id, environment_id, app_id, workspace_id, last_sequence)
            VALUES(@tenant, @project, @environment, @app, @workspace, @count)
            ON CONFLICT (scope_key) DO UPDATE SET last_sequence=tf_contact_change_heads.last_sequence+@count
            RETURNING last_sequence
            """, connection, transaction))
        {
            AddScope(head, scope);
            head.Parameters.AddWithValue("count", (long)drafts.Count);
            last = (long)(await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }
        var first = last - drafts.Count + 1;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_contact_changes(tenant_id, project_id, environment_id, app_id, workspace_id, sequence, event_id, format_version, kind, contact_id,
                related_id, merge_id, occurred_at)
            SELECT @tenant, @project, @environment, @app, @workspace, @first + ordinality - 1, event_id, 1, kind, contact_id, related_id, merge_id, occurred_at
            FROM unnest(@events, @kinds, @contacts, @related, @merges, @occurred) WITH ORDINALITY AS item(event_id, kind, contact_id, related_id, merge_id, occurred_at, ordinality)
            """, connection, transaction))
        {
            AddScope(insert, scope);
            insert.Parameters.AddWithValue("first", first);
            insert.Parameters.Add(new NpgsqlParameter("events", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = drafts.Select(draft => draft.Id).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Array | NpgsqlDbType.Smallint) { Value = drafts.Select(draft => (short)draft.Kind).ToArray() });
            AddTextArray(insert, "contacts", drafts.Select(draft => draft.Contact.Value));
            insert.Parameters.Add(new NpgsqlParameter("related", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = drafts.Select(draft => draft.Related?.Value).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("merges", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = drafts.Select(draft => draft.Merge).ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz) { Value = drafts.Select(draft => draft.OccurredAt).ToArray() });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return [.. drafts.Select((draft, index) => new ContactChange(scope, first + index, draft))];
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
