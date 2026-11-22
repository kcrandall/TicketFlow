using Npgsql;
using NpgsqlTypes;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter.Stores.Postgres;

public sealed partial class PostgresHelpCenterStore
{
    private const string CountsColumns = "coalesce(f.views, 0), coalesce(f.helpful, 0), coalesce(f.not_helpful, 0)";

    public async Task<ArticleFeedbackCounts?> GetCountsAsync(TicketScope scope, ArticleId article, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized, nameof(article));
        await using var command = source.CreateCommand($"""
            SELECT {CountsColumns} FROM tf_helpcenter_articles a LEFT JOIN tf_helpcenter_feedback f ON f.article_key=a.article_key
            WHERE a.{ArticleKeyWhere.Replace(" AND ", " AND a.", StringComparison.Ordinal)}
            """);
        BindArticleKey(command, scope, article);
        return await ReadSingleAsync(command, ReadCounts, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ArticleFeedbackResult> RecordViewAsync(TicketScope scope, ArticleId article, VisitorHash visitor, DateOnly day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized && visitor.IsInitialized, nameof(article));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await LockArticleAsync(connection, transaction, scope, article, cancellationToken).ConfigureAwait(false))
            return FeedbackArticleNotFound.Instance;
        int inserted;
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO tf_helpcenter_views(article_key, visitor_hash, day, scope_key)
            VALUES(tf_key_v1(@tenant,@project,@environment,@app,@workspace,@article), @visitor, @day, tf_key_v1(@tenant,@project,@environment,@app,@workspace))
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            BindArticleKey(insert, scope, article);
            AddVisitor(insert, visitor);
            insert.Parameters.Add(new NpgsqlParameter("day", NpgsqlDbType.Date) { Value = day });
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var counts = inserted == 1
            ? await BumpAsync(connection, transaction, scope, article, "views=f.views+1", (1, 0, 0), cancellationToken).ConfigureAwait(false)
            : await CountsAsync(connection, transaction, scope, article, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted == 1 ? new FeedbackCounted(counts) : new FeedbackUnchanged(counts);
    }

    public async Task<ArticleFeedbackResult> RecordVoteAsync(TicketScope scope, ArticleId article, VisitorHash visitor, bool helpful, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized && visitor.IsInitialized, nameof(article));
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await LockArticleAsync(connection, transaction, scope, article, cancellationToken).ConfigureAwait(false))
            return FeedbackArticleNotFound.Instance;
        const string VoteKey = "article_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@article) AND visitor_hash=@visitor";
        int inserted;
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO tf_helpcenter_votes(article_key, visitor_hash, helpful) VALUES(tf_key_v1(@tenant,@project,@environment,@app,@workspace,@article), @visitor, @helpful)
            ON CONFLICT DO NOTHING
            """, connection, transaction))
        {
            BindArticleKey(insert, scope, article);
            AddVisitor(insert, visitor);
            insert.Parameters.AddWithValue("helpful", helpful);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        ArticleFeedbackResult result;
        if (inserted == 1)
        {
            result = new FeedbackCounted(await BumpAsync(connection, transaction, scope, article,
                helpful ? "helpful=f.helpful+1" : "not_helpful=f.not_helpful+1", helpful ? (0, 1, 0) : (0, 0, 1), cancellationToken).ConfigureAwait(false));
        }
        else
        {
            var previous = await ScalarAsync<bool>(connection, transaction, $"SELECT helpful FROM tf_helpcenter_votes WHERE {VoteKey} FOR UPDATE", command =>
            {
                BindArticleKey(command, scope, article);
                AddVisitor(command, visitor);
            }, cancellationToken).ConfigureAwait(false);
            if (previous == helpful)
            {
                result = new FeedbackUnchanged(await CountsAsync(connection, transaction, scope, article, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                await using (var update = new NpgsqlCommand($"UPDATE tf_helpcenter_votes SET helpful=@helpful WHERE {VoteKey}", connection, transaction))
                {
                    BindArticleKey(update, scope, article);
                    AddVisitor(update, visitor);
                    update.Parameters.AddWithValue("helpful", helpful);
                    await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                result = new FeedbackCounted(await BumpAsync(connection, transaction, scope, article,
                    helpful ? "helpful=f.helpful+1, not_helpful=f.not_helpful-1" : "helpful=f.helpful-1, not_helpful=f.not_helpful+1", (0, 0, 0), cancellationToken).ConfigureAwait(false));
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<int> PurgeViewMarkersAsync(TicketScope scope, DateOnly before, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 10_000);
        await using var command = source.CreateCommand("""
            DELETE FROM tf_helpcenter_views WHERE ctid IN (
                SELECT ctid FROM tf_helpcenter_views WHERE scope_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace) AND day < @before LIMIT @limit)
            """);
        AddScope(command, scope);
        command.Parameters.Add(new NpgsqlParameter("before", NpgsqlDbType.Date) { Value = before });
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes a share lock on the article row (so a concurrent delete cannot orphan counters); false when it does not exist.</summary>
    private static async Task<bool> LockArticleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ArticleId article, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT 1 FROM tf_helpcenter_articles WHERE {ArticleKeyWhere} FOR SHARE", connection, transaction);
        BindArticleKey(command, scope, article);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<ArticleFeedbackCounts> BumpAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ArticleId article, string assignments, (int Views, int Helpful, int NotHelpful) initial, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO tf_helpcenter_feedback AS f({ScopeColumns}, article_id, views, helpful, not_helpful)
            VALUES({ScopeParameters}, @article, @views, @helpful_count, @not_helpful_count)
            ON CONFLICT (article_key) DO UPDATE SET {assignments}
            RETURNING f.views, f.helpful, f.not_helpful
            """, connection, transaction);
        BindArticleKey(command, scope, article);
        command.Parameters.AddWithValue("views", (long)initial.Views);
        command.Parameters.AddWithValue("helpful_count", (long)initial.Helpful);
        command.Parameters.AddWithValue("not_helpful_count", (long)initial.NotHelpful);
        return (await ReadSingleAsync(command, ReadCounts, cancellationToken).ConfigureAwait(false))!;
    }

    private static async Task<ArticleFeedbackCounts> CountsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TicketScope scope, ArticleId article, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT {CountsColumns} FROM (SELECT 1) AS one LEFT JOIN tf_helpcenter_feedback f
                ON f.article_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@article) AND f.article_id=@article
            """, connection, transaction);
        BindArticleKey(command, scope, article);
        return (await ReadSingleAsync(command, ReadCounts, cancellationToken).ConfigureAwait(false))!;
    }

    private static void AddVisitor(NpgsqlCommand command, VisitorHash visitor) =>
        command.Parameters.Add(new NpgsqlParameter("visitor", NpgsqlDbType.Bytea) { Value = Convert.FromHexString(visitor.Value) });

    private static ArticleFeedbackCounts ReadCounts(NpgsqlDataReader reader) => new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
}
