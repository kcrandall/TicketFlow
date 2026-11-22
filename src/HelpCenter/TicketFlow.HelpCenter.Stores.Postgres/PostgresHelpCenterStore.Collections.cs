using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter.Stores.Postgres;

public sealed partial class PostgresHelpCenterStore
{
    private const string CollectionColumns = "collection_id, portal_id, parent_id, format_version, depth, position, translations::text, revision";
    private const string CollectionKeyWhere = "collection_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@collection) AND " + WorkspaceWhere + " AND collection_id=@collection";

    public async Task<StoredCollection?> GetCollectionAsync(TicketScope scope, CollectionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT {CollectionColumns} FROM tf_helpcenter_collections WHERE {CollectionKeyWhere}");
        BindCollectionKey(command, scope, id);
        return await ReadSingleAsync(command, reader => ReadCollection(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredCollection>> ListCollectionsAsync(TicketScope scope, PortalId portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(portal.IsInitialized, nameof(portal));
        await using var command = source.CreateCommand($"""
            SELECT {CollectionColumns} FROM tf_helpcenter_collections WHERE {PortalKeyWhere}
            ORDER BY depth, position, collection_id COLLATE "C" LIMIT {HelpCenterLimits.MaximumCollectionsPerPortal}
            """);
        AddScope(command, scope);
        AddText(command, "portal", portal.Value);
        return await ReadListAsync(command, reader => ReadCollection(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<HelpCenterWriteResult<StoredCollection>> CreateCollectionAsync(TicketScope scope, HelpCollection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(collection);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var existing = await CollectionStateAsync(connection, transaction, scope, collection.Id, cancellationToken).ConfigureAwait(false);
        if (existing is { } found)
            return new HelpCenterWriteConflict(found.Revision);
        if (await PortalRevisionAsync(connection, transaction, scope, collection.Portal, cancellationToken).ConfigureAwait(false) is null)
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference);
        var depth = 1;
        if (collection.Parent is { } parentId)
        {
            if (await CollectionStateAsync(connection, transaction, scope, parentId, cancellationToken).ConfigureAwait(false) is not { } parent || parent.Portal != collection.Portal.Value)
                return new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference);
            depth = parent.Depth + 1;
            if (depth > HelpCenterLimits.MaximumCollectionDepth)
                return new HelpCenterWriteRejected(HelpCenterWriteRejection.DepthExceeded);
        }
        var count = await ScalarAsync<long>(connection, transaction, $"SELECT count(*) FROM tf_helpcenter_collections WHERE {PortalKeyWhere}", command =>
        {
            AddScope(command, scope);
            AddText(command, "portal", collection.Portal.Value);
        }, cancellationToken).ConfigureAwait(false);
        if (count >= HelpCenterLimits.MaximumCollectionsPerPortal)
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.CapacityExceeded);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_helpcenter_collections({ScopeColumns}, collection_id, portal_id, parent_id, format_version, depth, position, translations, revision)
            VALUES({ScopeParameters}, @collection, @portal, @parent, 1, @depth, @position, @translations, 1)
            """, connection, transaction))
        {
            BindCollection(insert, scope, collection);
            insert.Parameters.AddWithValue("depth", (short)depth);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HelpCenterWriteApplied<StoredCollection>(new(scope, collection, depth, 1));
    }

    public async Task<HelpCenterWriteResult<StoredCollection>> UpdateCollectionAsync(TicketScope scope, HelpCollection collection, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var existing = await CollectionStateAsync(connection, transaction, scope, collection.Id, cancellationToken).ConfigureAwait(false);
        if (Miss<StoredCollection>(existing?.Revision, expectedRevision) is { } miss)
            return miss;
        if (existing!.Value.Portal != collection.Portal.Value || existing.Value.Parent != collection.Parent?.Value)
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference);
        await using (var update = new NpgsqlCommand($"UPDATE tf_helpcenter_collections SET position=@position, translations=@translations, revision=revision+1 WHERE {CollectionKeyWhere}", connection, transaction))
        {
            BindCollection(update, scope, collection);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HelpCenterWriteApplied<StoredCollection>(new(scope, collection, existing.Value.Depth, expectedRevision + 1));
    }

    public async Task<HelpCenterWriteResult<StoredCollection>> DeleteCollectionAsync(TicketScope scope, CollectionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var existing = await CollectionStateAsync(connection, transaction, scope, id, cancellationToken).ConfigureAwait(false);
        if (Miss<StoredCollection>(existing?.Revision, expectedRevision) is { } miss)
            return miss;
        var used = await ScalarAsync<bool>(connection, transaction, """
            SELECT EXISTS (SELECT 1 FROM tf_helpcenter_collections WHERE parent_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@collection))
                OR EXISTS (SELECT 1 FROM tf_helpcenter_articles WHERE collection_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@collection))
            """, command => BindCollectionKey(command, scope, id), cancellationToken).ConfigureAwait(false);
        if (used)
            return new HelpCenterWriteRejected(HelpCenterWriteRejection.NotEmpty);
        StoredCollection? removed;
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_helpcenter_collections WHERE {CollectionKeyWhere} RETURNING {CollectionColumns}", connection, transaction))
        {
            BindCollectionKey(delete, scope, id);
            removed = await ReadSingleAsync(delete, reader => ReadCollection(reader, scope), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new HelpCenterWriteApplied<StoredCollection>(removed!);
    }

    private readonly record struct CollectionState(long Revision, string Portal, string? Parent, int Depth);

    private static async Task<CollectionState?> CollectionStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, CollectionId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision, portal_id, parent_id, depth FROM tf_helpcenter_collections WHERE {CollectionKeyWhere}", connection, transaction);
        BindCollectionKey(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        return new CollectionState(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt16(3));
    }

    private static void BindCollectionKey(NpgsqlCommand command, TicketScope scope, CollectionId id)
    {
        AddScope(command, scope);
        AddText(command, "collection", id.Value);
    }

    private static void BindCollection(NpgsqlCommand command, TicketScope scope, HelpCollection collection)
    {
        BindCollectionKey(command, scope, collection.Id);
        AddText(command, "portal", collection.Portal.Value);
        AddText(command, "parent", collection.Parent?.Value);
        command.Parameters.AddWithValue("position", collection.Position);
        AddJson(command, "translations", HelpCenterJsonCodec.EncodeCollectionTranslations(collection.Translations));
    }

    private static StoredCollection ReadCollection(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(3) != 1)
            throw new FormatException("Unsupported collection format version.");
        var collection = new HelpCollection(new CollectionId(reader.GetString(0)), new PortalId(reader.GetString(1)),
            HelpCenterJsonCodec.DecodeCollectionTranslations(reader.GetString(6)), reader.IsDBNull(2) ? null : new CollectionId(reader.GetString(2)), reader.GetInt32(5));
        return new StoredCollection(scope, collection, reader.GetInt16(4), reader.GetInt64(7));
    }, "collection");
}
