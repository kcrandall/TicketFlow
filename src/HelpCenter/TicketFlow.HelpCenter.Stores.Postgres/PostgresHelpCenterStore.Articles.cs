using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter.Stores.Postgres;

public sealed partial class PostgresHelpCenterStore
{
    private const string ArticleColumns =
        "article_id, portal_id, collection_id, format_version, position, state, published_revision, translations::text, created_by_kind, created_by_id, created_at, updated_by_kind, updated_by_id, updated_at, revision";

    private const string ArticleKeyWhere = "article_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@article) AND " + WorkspaceWhere + " AND article_id=@article";
    private const string RevisionColumns = "number, format_version, translations::text, published_by_kind, published_by_id, published_at";

    public async Task<StoredArticle?> GetArticleAsync(TicketScope scope, ArticleId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        await using var command = source.CreateCommand($"SELECT {ArticleColumns} FROM tf_helpcenter_articles WHERE {ArticleKeyWhere}");
        BindArticleKey(command, scope, id);
        return await ReadSingleAsync(command, reader => ReadArticle(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<HelpCenterPage<StoredArticle>> ListArticlesAsync(TicketScope scope, ArticleListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        await using var command = source.CreateCommand($"""
            SELECT {ArticleColumns} FROM tf_helpcenter_articles WHERE {PortalKeyWhere}
              AND (@collection::text IS NULL OR collection_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@collection::text))
              AND (@collection::text IS NULL OR collection_id=@collection::text)
              AND (@state::smallint IS NULL OR state=@state::smallint)
              AND (@after::text IS NULL OR article_id COLLATE "C" > @after::text)
            ORDER BY article_id COLLATE "C" LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "portal", query.Portal.Value);
        AddText(command, "collection", query.Collection?.Value);
        command.Parameters.Add(new NpgsqlParameter("state", NpgsqlDbType.Smallint) { Value = query.State is { } state ? (short)state : DBNull.Value });
        AddText(command, "after", query.After?.Value);
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        var rows = await ReadListAsync(command, reader => ReadArticle(reader, scope), cancellationToken).ConfigureAwait(false);
        return rows.Count > query.Limit ? new(rows[..query.Limit], true) : new(rows, false);
    }

    public async Task<StoreWriteResult<StoredArticle, HelpCenterWriteRejection>> CreateArticleAsync(TicketScope scope, ArticleRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(record);
        var article = record.Article;
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        if (await ArticleStateAsync(connection, transaction, scope, article.Id, cancellationToken).ConfigureAwait(false) is { } existing)
            return new StoreConflict(existing.Revision);
        if (record.State == ArticleState.Published || record.PublishedRevision is not null)
            return new StoreRejected<HelpCenterWriteRejection>(HelpCenterWriteRejection.InvalidRevision);
        if (await InvalidReferencesAsync(connection, transaction, scope, article, cancellationToken).ConfigureAwait(false))
            return new StoreRejected<HelpCenterWriteRejection>(HelpCenterWriteRejection.InvalidReference);
        var count = await ScalarAsync<long>(connection, transaction, $"SELECT count(*) FROM tf_helpcenter_articles WHERE {PortalKeyWhere}", command =>
        {
            AddScope(command, scope);
            AddText(command, "portal", article.Portal.Value);
        }, cancellationToken).ConfigureAwait(false);
        if (count >= HelpCenterLimits.MaximumArticlesPerPortal)
            return new StoreRejected<HelpCenterWriteRejection>(HelpCenterWriteRejection.CapacityExceeded);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_helpcenter_articles({ScopeColumns}, article_id, portal_id, collection_id, format_version, position, state, published_revision, translations,
                created_by_kind, created_by_id, created_at, updated_by_kind, updated_by_id, updated_at, revision)
            VALUES({ScopeParameters}, @article, @portal, @collection, 1, @position, @state, @published, @translations,
                @created_kind, @created_id, @created_at, @updated_kind, @updated_id, @updated_at, 1)
            """, connection, transaction))
        {
            BindArticle(insert, scope, record);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<StoredArticle>(new(scope, record, 1));
    }

    public async Task<StoreWriteResult<StoredArticle, HelpCenterWriteRejection>> UpdateArticleAsync(TicketScope scope, ArticleRecord record, long expectedRevision, ArticleRevision? appendRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        var article = record.Article;
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        var existing = await ArticleStateAsync(connection, transaction, scope, article.Id, cancellationToken).ConfigureAwait(false);
        if (Miss<StoredArticle>(existing?.Revision, expectedRevision) is { } miss)
            return miss;
        var current = existing!.Value;
        if (!string.Equals(current.Portal, article.Portal.Value, StringComparison.Ordinal) || await InvalidReferencesAsync(connection, transaction, scope, article, cancellationToken).ConfigureAwait(false))
            return new StoreRejected<HelpCenterWriteRejection>(HelpCenterWriteRejection.InvalidReference);
        if (appendRevision is not null || record.PublishedRevision != current.PublishedRevision)
        {
            var latest = await ScalarAsync<int>(connection, transaction, $"SELECT coalesce(max(number), 0) FROM tf_helpcenter_revisions WHERE {ArticleKeyWhere}",
                command => BindArticleKey(command, scope, article.Id), cancellationToken).ConfigureAwait(false);
            if (appendRevision is not null && (appendRevision.Article != article.Id || appendRevision.Number != latest + 1))
                return new StoreRejected<HelpCenterWriteRejection>(HelpCenterWriteRejection.InvalidRevision);
            var available = latest + (appendRevision is null ? 0 : 1);
            if (record.PublishedRevision is { } live && live > available)
                return new StoreRejected<HelpCenterWriteRejection>(HelpCenterWriteRejection.InvalidRevision);
        }

        if (appendRevision is not null)
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO tf_helpcenter_revisions({ScopeColumns}, article_id, number, format_version, translations, published_by_kind, published_by_id, published_at)
                VALUES({ScopeParameters}, @article, @number, 1, @translations, @published_kind, @published_id, @published_at)
                """, connection, transaction);
            BindArticleKey(insert, scope, article.Id);
            insert.Parameters.AddWithValue("number", appendRevision.Number);
            AddJson(insert, "translations", HelpCenterJsonCodec.EncodePublishedTranslations(appendRevision.Translations));
            AddActor(insert, "published", appendRevision.PublishedBy);
            insert.Parameters.AddWithValue("published_at", appendRevision.PublishedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var update = new NpgsqlCommand($"""
            UPDATE tf_helpcenter_articles SET collection_id=@collection, position=@position, state=@state, published_revision=@published, translations=@translations,
                created_by_kind=@created_kind, created_by_id=@created_id, created_at=@created_at,
                updated_by_kind=@updated_kind, updated_by_id=@updated_id, updated_at=@updated_at, revision=revision+1
            WHERE {ArticleKeyWhere}
            """, connection, transaction))
        {
            BindArticle(update, scope, record);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var wasPublished = current.State == ArticleState.Published;
        var nowPublished = record.State == ArticleState.Published;
        if (!wasPublished && !nowPublished)
        {
            // Draft/Archived edit: no published rows exist before or after.
        }
        else if (wasPublished && nowPublished && appendRevision is null && record.PublishedRevision == current.PublishedRevision)
        {
            // Draft-only edit of an already-published article: only update collection/position if they changed.
            if (!string.Equals(current.Collection, article.Collection?.Value, StringComparison.Ordinal) || current.Position != article.Position)
            {
                await using var syncMeta = new NpgsqlCommand(
                    $"UPDATE tf_helpcenter_published SET collection_id=@collection, position=@position WHERE {ArticleKeyWhere}", connection, transaction);
                BindArticleKey(syncMeta, scope, article.Id);
                AddText(syncMeta, "collection", article.Collection?.Value);
                syncMeta.Parameters.AddWithValue("position", article.Position);
                await syncMeta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (wasPublished)
            {
                await using var clear = new NpgsqlCommand($"DELETE FROM tf_helpcenter_published WHERE {ArticleKeyWhere}", connection, transaction);
                BindArticleKey(clear, scope, article.Id);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            if (nowPublished)
            {
                var revision = appendRevision is not null && appendRevision.Number == record.PublishedRevision
                    ? appendRevision
                    : await LoadRevisionAsync(connection, transaction, scope, article.Id, record.PublishedRevision!.Value, cancellationToken).ConfigureAwait(false);
                try
                {
                    await InsertPublishedBatchAsync(connection, transaction, scope, record, revision!, cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException error) when (string.Equals(error.SqlState, PostgresErrorCodes.UniqueViolation, StringComparison.Ordinal) && string.Equals(error.ConstraintName, "tf_helpcenter_published_slug", StringComparison.Ordinal))
                {
                    // The transaction is aborted; disposing it rolls back the revision insert and article update.
                    return new StoreRejected<HelpCenterWriteRejection>(HelpCenterWriteRejection.SlugConflict);
                }
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<StoredArticle>(new(scope, record, expectedRevision + 1));
    }

    public async Task<StoreWriteResult<StoredArticle, HelpCenterWriteRejection>> DeleteArticleAsync(TicketScope scope, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, scope, cancellationToken).ConfigureAwait(false);
        StoredArticle? removed;
        await using (var delete = new NpgsqlCommand($"DELETE FROM tf_helpcenter_articles WHERE {ArticleKeyWhere} AND revision=@expected RETURNING {ArticleColumns}", connection, transaction))
        {
            BindArticleKey(delete, scope, id);
            delete.Parameters.AddWithValue("expected", expectedRevision);
            removed = await ReadSingleAsync(delete, reader => ReadArticle(reader, scope), cancellationToken).ConfigureAwait(false);
        }
        if (removed is null)
            return Miss<StoredArticle>((await ArticleStateAsync(connection, transaction, scope, id, cancellationToken).ConfigureAwait(false))?.Revision, expectedRevision)
                ?? throw new InvalidOperationException("The article changed during deletion.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreApplied<StoredArticle>(removed);
    }

    public async Task<ArticleRevision?> GetRevisionAsync(TicketScope scope, ArticleId article, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized, nameof(article));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await LoadRevisionAsync(connection, null, scope, article, number, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HelpCenterPage<ArticleRevision>> ListRevisionsAsync(TicketScope scope, ArticleId article, int limit, int? before = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized, nameof(article));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, HelpCenterLimits.MaximumPageSize);
        await using var command = source.CreateCommand($"""
            SELECT {RevisionColumns} FROM tf_helpcenter_revisions WHERE {ArticleKeyWhere} AND (@before::integer IS NULL OR number < @before::integer)
            ORDER BY number DESC LIMIT @limit
            """);
        BindArticleKey(command, scope, article);
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Integer) { Value = before is { } value ? value : DBNull.Value });
        command.Parameters.AddWithValue("limit", limit + 1);
        var rows = await ReadListAsync(command, reader => ReadRevision(reader, article), cancellationToken).ConfigureAwait(false);
        return rows.Count > limit ? new(rows[..limit], true) : new(rows, false);
    }

    public async Task<int> GetLatestRevisionNumberAsync(TicketScope scope, ArticleId article, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized, nameof(article));
        await using var command = source.CreateCommand($"SELECT coalesce(max(number), 0) FROM tf_helpcenter_revisions WHERE {ArticleKeyWhere}");
        BindArticleKey(command, scope, article);
        return (int)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<bool> IsArticlePublishedInPortalAsync(TicketScope scope, PortalId portal, ArticleId article, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(portal.IsInitialized && article.IsInitialized, nameof(article));
        await using var command = source.CreateCommand($"SELECT 1 FROM tf_helpcenter_articles WHERE {ArticleKeyWhere} AND portal_id=@portal AND state=2");
        BindArticleKey(command, scope, article);
        AddText(command, "portal", portal.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<ArticleRevision?> LoadRevisionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, TicketScope scope, ArticleId article, int number, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {RevisionColumns} FROM tf_helpcenter_revisions WHERE {ArticleKeyWhere} AND number=@number", connection, transaction);
        BindArticleKey(command, scope, article);
        command.Parameters.AddWithValue("number", number);
        return await ReadSingleAsync(command, reader => ReadRevision(reader, article), cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPublishedBatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ArticleRecord record, ArticleRevision revision, CancellationToken cancellationToken)
    {
        var translations = revision.Translations;
        if (translations.Count == 1)
        {
            await InsertPublishedAsync(connection, transaction, scope, record, revision, translations[0], cancellationToken).ConfigureAwait(false);
            return;
        }
        var article = record.Article;
        var n = translations.Count;
        var locales = new string[n];
        var slugs = new string[n];
        var titles = new string[n];
        var excerpts = new string?[n];
        var tagsJson = new string[n];
        var tagsTexts = new string[n];
        var bodies = new string[n];
        var plains = new string[n];
        var configs = new string[n];
        for (var i = 0; i < n; i++)
        {
            var t = translations[i];
            var content = t.Content;
            locales[i] = content.Locale.Value;
            slugs[i] = content.Slug.Value;
            titles[i] = content.Title;
            excerpts[i] = content.Excerpt;
            tagsJson[i] = content.Tags.Count == 0 ? "[]" : System.Text.Json.JsonSerializer.Serialize(content.Tags);
            tagsTexts[i] = string.Join(' ', content.Tags.Select(tag => tag.Replace('-', ' ').Replace('_', ' ')));
            bodies[i] = content.Body;
            plains[i] = t.PlainText;
            configs[i] = HelpCenterSearchConfiguration.ForLocale(content.Locale);
        }
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO tf_helpcenter_published({ScopeColumns}, article_id, portal_id, collection_id, locale, slug, position, revision_number, published_at,
                title, excerpt, tags, tags_text, body, plain_text, ts_config)
            SELECT {ScopeParameters}, @article, @portal, @collection, r.locale, r.slug, @position, @number, @published_at,
                r.title, r.excerpt, ARRAY(SELECT jsonb_array_elements_text(r.tags)), r.tags_text, r.body, r.plain, r.config::regconfig
            FROM unnest(@locales, @slugs, @titles, @excerpts, @tags, @tags_texts, @bodies, @plains, @configs)
                AS r(locale, slug, title, excerpt, tags, tags_text, body, plain, config)
            """, connection, transaction);
        BindArticleKey(insert, scope, article.Id);
        AddText(insert, "portal", article.Portal.Value);
        AddText(insert, "collection", article.Collection?.Value);
        insert.Parameters.AddWithValue("position", article.Position);
        insert.Parameters.AddWithValue("number", revision.Number);
        insert.Parameters.AddWithValue("published_at", revision.PublishedAt);
        insert.Parameters.Add(new NpgsqlParameter<string[]>("locales", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = locales });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("slugs", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = slugs });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("titles", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = titles });
        insert.Parameters.Add(new NpgsqlParameter<string?[]>("excerpts", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = excerpts });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("tags", NpgsqlDbType.Array | NpgsqlDbType.Jsonb) { TypedValue = tagsJson });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("tags_texts", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = tagsTexts });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("bodies", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = bodies });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("plains", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = plains });
        insert.Parameters.Add(new NpgsqlParameter<string[]>("configs", NpgsqlDbType.Array | NpgsqlDbType.Text) { TypedValue = configs });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPublishedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ArticleRecord record, ArticleRevision revision, PublishedTranslation translation, CancellationToken cancellationToken)
    {
        var content = translation.Content;
        var article = record.Article;
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO tf_helpcenter_published({ScopeColumns}, article_id, portal_id, collection_id, locale, slug, position, revision_number, published_at,
                title, excerpt, tags, tags_text, body, plain_text, ts_config)
            VALUES({ScopeParameters}, @article, @portal, @collection, @locale, @slug, @position, @number, @published_at,
                @title, @excerpt, @tags, @tags_text, @body, @plain, @config::regconfig)
            """, connection, transaction);
        BindArticleKey(insert, scope, article.Id);
        AddText(insert, "portal", article.Portal.Value);
        AddText(insert, "collection", article.Collection?.Value);
        AddText(insert, "locale", content.Locale.Value);
        AddText(insert, "slug", content.Slug.Value);
        insert.Parameters.AddWithValue("position", article.Position);
        insert.Parameters.AddWithValue("number", revision.Number);
        insert.Parameters.AddWithValue("published_at", revision.PublishedAt);
        AddText(insert, "title", content.Title);
        AddText(insert, "excerpt", content.Excerpt);
        insert.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = content.Tags.ToArray() });
        AddText(insert, "tags_text", string.Join(' ', content.Tags.Select(tag => tag.Replace('-', ' ').Replace('_', ' '))));
        AddText(insert, "body", content.Body);
        AddText(insert, "plain", translation.PlainText);
        AddText(insert, "config", HelpCenterSearchConfiguration.ForLocale(content.Locale));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct ArticleRow(long Revision, string Portal, ArticleState State, int? PublishedRevision, string? Collection, int Position);

    private static async Task<ArticleRow?> ArticleStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ArticleId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT revision, portal_id, state, published_revision, collection_id, position FROM tf_helpcenter_articles WHERE {ArticleKeyWhere}", connection, transaction);
        BindArticleKey(command, scope, id);
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ArticleRow(reader.GetInt64(0), reader.GetString(1), (ArticleState)reader.GetInt16(2),
                await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(3), await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4), reader.GetInt32(5))
            : null;
    }

    private static async Task<bool> InvalidReferencesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, Article article, CancellationToken cancellationToken)
    {
        if (await PortalRevisionAsync(connection, transaction, scope, article.Portal, cancellationToken).ConfigureAwait(false) is null)
            return true;
        return article.Collection is { } collection
            && (await CollectionStateAsync(connection, transaction, scope, collection, cancellationToken).ConfigureAwait(false) is not { } stored || !string.Equals(stored.Portal, article.Portal.Value, StringComparison.Ordinal));
    }

    private static void BindArticleKey(NpgsqlCommand command, TicketScope scope, ArticleId id)
    {
        AddScope(command, scope);
        AddText(command, "article", id.Value);
    }

    private static void BindArticle(NpgsqlCommand command, TicketScope scope, ArticleRecord record)
    {
        var article = record.Article;
        BindArticleKey(command, scope, article.Id);
        AddText(command, "portal", article.Portal.Value);
        AddText(command, "collection", article.Collection?.Value);
        command.Parameters.AddWithValue("position", article.Position);
        command.Parameters.AddWithValue("state", (short)record.State);
        command.Parameters.Add(new NpgsqlParameter("published", NpgsqlDbType.Integer) { Value = record.PublishedRevision is { } live ? live : DBNull.Value });
        AddJson(command, "translations", HelpCenterJsonCodec.EncodeArticleTranslations(article.Translations));
        command.Parameters.AddWithValue("created_kind", (short)record.CreatedBy.Kind);
        AddText(command, "created_id", record.CreatedBy.Id);
        command.Parameters.AddWithValue("created_at", record.CreatedAt);
        command.Parameters.AddWithValue("updated_kind", (short)record.UpdatedBy.Kind);
        AddText(command, "updated_id", record.UpdatedBy.Id);
        command.Parameters.AddWithValue("updated_at", record.UpdatedAt);
    }

    private static StoredArticle ReadArticle(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        if (reader.GetInt16(3) != 1)
            throw new FormatException("Unsupported article format version.");
        var state = (ArticleState)reader.GetInt16(5);
        if (!Enum.IsDefined(state))
            throw new FormatException("Unknown article state.");
        var article = new Article(new ArticleId(reader.GetString(0)), new PortalId(reader.GetString(1)), HelpCenterJsonCodec.DecodeArticleTranslations(reader.GetString(7)),
            reader.IsDBNull(2) ? null : new CollectionId(reader.GetString(2)), reader.GetInt32(4));
        var record = new ArticleRecord(article, state, reader.IsDBNull(6) ? null : reader.GetInt32(6),
            ReadActor(reader, 8, 9), reader.GetFieldValue<DateTimeOffset>(10), ReadActor(reader, 11, 12), reader.GetFieldValue<DateTimeOffset>(13));
        return new StoredArticle(scope, record, reader.GetInt64(14));
    }, "article");

    private static ArticleRevision ReadRevision(NpgsqlDataReader reader, ArticleId article) => Decode(() =>
    {
        if (reader.GetInt16(1) != 1)
            throw new FormatException("Unsupported revision format version.");
        return new ArticleRevision(article, reader.GetInt32(0), HelpCenterJsonCodec.DecodePublishedTranslations(reader.GetString(2)), ReadActor(reader, 3, 4), reader.GetFieldValue<DateTimeOffset>(5));
    }, "revision");
}
