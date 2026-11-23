using System.Text;
using Npgsql;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter.Stores.Postgres;

public sealed partial class PostgresHelpCenterStore
{
    private const string PublishedColumns = "article_id, portal_id, collection_id, position, revision_number, published_at, locale, title, slug, body, excerpt, tags";
    private const string PublishedSummaryColumns = "article_id, collection_id, position, published_at, title, slug, excerpt";
    private const string PublishedScopeWhere = "portal_key=tf_key_v1(@tenant,@project,@environment,@app,@workspace,@portal) AND " + WorkspaceWhere + " AND portal_id=@portal AND locale=@locale";
    private static readonly string PublishedScopeWhereP = "p." + PublishedScopeWhere.Replace(" AND ", " AND p.", StringComparison.Ordinal);

    /// <summary>
    /// <c>ts_headline</c> options: plain text (empty selection markers), one fragment of 15-35 words. Combined with
    /// <see cref="ArticleSnippet.MaximumSnippetLength"/> trimming, snippets never carry markup.
    /// </summary>
    private const string HeadlineOptions = "StartSel=\"\", StopSel=\"\", MaxWords=35, MinWords=15, ShortWord=3, MaxFragments=1, FragmentDelimiter=\" \"";

    public async Task<PublishedArticle?> GetPublishedAsync(TicketScope scope, PortalId portal, HelpCenterLocale locale, HelpCenterSlug slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(portal.IsInitialized && locale.IsInitialized && slug.IsInitialized, nameof(portal));
        await using var command = source.CreateCommand($"SELECT {PublishedColumns} FROM tf_helpcenter_published WHERE {PublishedScopeWhere} AND slug=@slug");
        AddScope(command, scope);
        AddText(command, "portal", portal.Value);
        AddText(command, "locale", locale.Value);
        AddText(command, "slug", slug.Value);
        return await ReadSingleAsync(command, reader => ReadPublished(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublishedArticle?> GetPublishedByIdAsync(TicketScope scope, ArticleId article, HelpCenterLocale locale, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized && locale.IsInitialized, nameof(article));
        await using var command = source.CreateCommand($"SELECT {PublishedColumns} FROM tf_helpcenter_published WHERE {ArticleKeyWhere} AND locale=@locale");
        BindArticleKey(command, scope, article);
        AddText(command, "locale", locale.Value);
        return await ReadSingleAsync(command, reader => ReadPublished(reader, scope), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublishedArticlePage> ListPublishedAsync(TicketScope scope, PublishedArticleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        await using var command = source.CreateCommand($"""
            SELECT {PublishedColumns} FROM tf_helpcenter_published WHERE {PublishedScopeWhere}
              AND (@collection::text IS NULL OR collection_id=@collection::text)
              AND (@after_id::text IS NULL OR (position, article_id COLLATE "C") > (@after_position::integer, @after_id::text COLLATE "C"))
            ORDER BY position, article_id COLLATE "C" LIMIT @limit
            """);
        AddScope(command, scope);
        AddText(command, "portal", query.Portal.Value);
        AddText(command, "locale", query.Locale.Value);
        AddText(command, "collection", query.Collection?.Value);
        command.Parameters.AddWithValue("after_position", query.After?.Position ?? 0);
        AddText(command, "after_id", query.After?.Article.Value);
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        var rows = await ReadListAsync(command, reader => ReadPublished(reader, scope), cancellationToken).ConfigureAwait(false);
        if (rows.Count <= query.Limit)
            return new PublishedArticlePage(rows, null);
        var page = rows[..query.Limit];
        return new PublishedArticlePage(page, new PublishedArticleCursor(page[^1].Position, page[^1].Article));
    }

    public async Task<PublicArticlePage> ListPublishedSummariesAsync(TicketScope scope, PublishedArticleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        var sql = query.Collection is not null
            ? $"""
              SELECT {PublishedSummaryColumns} FROM tf_helpcenter_published WHERE {PublishedScopeWhere}
                AND collection_id=@collection
                AND (@after_id::text IS NULL OR (position, article_id COLLATE "C") > (@after_position::integer, @after_id::text COLLATE "C"))
              ORDER BY position, article_id COLLATE "C" LIMIT @limit
              """
            : $"""
              SELECT {PublishedSummaryColumns} FROM tf_helpcenter_published WHERE {PublishedScopeWhere}
                AND (@after_id::text IS NULL OR (position, article_id COLLATE "C") > (@after_position::integer, @after_id::text COLLATE "C"))
              ORDER BY position, article_id COLLATE "C" LIMIT @limit
              """;
        await using var command = source.CreateCommand(sql);
        AddScope(command, scope);
        AddText(command, "portal", query.Portal.Value);
        AddText(command, "locale", query.Locale.Value);
        if (query.Collection is { } col)
            AddText(command, "collection", col.Value);
        command.Parameters.AddWithValue("after_position", query.After?.Position ?? 0);
        AddText(command, "after_id", query.After?.Article.Value);
        command.Parameters.AddWithValue("limit", query.Limit + 1);
        var rows = await ReadListAsync(command, ReadPublishedSummary, cancellationToken).ConfigureAwait(false);
        if (rows.Count <= query.Limit)
            return new PublicArticlePage(rows, null);
        var page = rows[..query.Limit];
        return new PublicArticlePage(page, new PublishedArticleCursor(page[^1].Position, page[^1].Id));
    }

    public async Task<IReadOnlyList<ArticleSnippet>> SearchAsync(TicketScope scope, HelpCenterSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        // Rank and paginate in the CTE first so ts_headline only runs on the final page of rows.
        await using var command = source.CreateCommand($"""
            WITH q AS (SELECT websearch_to_tsquery(@config::regconfig, @text) AS query),
            ranked AS (
                SELECT p.article_id, p.title, p.slug, p.excerpt, p.revision_number, p.ts_config, p.plain_text,
                       ts_rank_cd(p.document, q.query, 1) AS rank
                FROM tf_helpcenter_published p, q
                WHERE {PublishedScopeWhereP} AND p.document @@ q.query
                ORDER BY rank DESC, p.article_id COLLATE "C"
                LIMIT @limit OFFSET @offset
            )
            SELECT r.article_id, r.title, r.slug, r.excerpt, r.revision_number, r.rank,
                   ts_headline(r.ts_config, r.plain_text, q.query, @headline) AS snippet
            FROM ranked r, q
            ORDER BY r.rank DESC, r.article_id COLLATE "C"
            """);
        AddScope(command, scope);
        AddText(command, "portal", query.Portal.Value);
        AddText(command, "locale", query.Locale.Value);
        AddText(command, "config", HelpCenterSearchConfiguration.ForLocale(query.Locale));
        AddText(command, "text", query.Text);
        AddText(command, "headline", HeadlineOptions);
        command.Parameters.AddWithValue("limit", query.Limit);
        command.Parameters.AddWithValue("offset", query.Offset);
        return await ReadListAsync(command, reader => Decode(() => new ArticleSnippet(
            new ArticleId(reader.GetString(0)), query.Portal, query.Locale, reader.GetString(1), new HelpCenterSlug(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3), Snippet(reader.GetString(6)), reader.GetFloat(5), reader.GetInt32(4)), "search hit"), cancellationToken).ConfigureAwait(false);
    }

    private static string Snippet(string headline)
    {
        var max = ArticleSnippet.MaximumSnippetLength;
        var builder = new StringBuilder(Math.Min(headline.Length, max));
        var inSpace = true;
        for (var i = 0; i < headline.Length && builder.Length < max; i++)
        {
            var c = headline[i];
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace)
                {
                    builder.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                builder.Append(c);
                inSpace = false;
            }
        }
        while (builder.Length > 0 && builder[^1] == ' ')
            builder.Length--;
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
            builder.Length--;
        return builder.ToString();
    }

    private static PublicArticleSummary ReadPublishedSummary(NpgsqlDataReader reader) => Decode(() =>
        new PublicArticleSummary(new ArticleId(reader.GetString(0)), reader.IsDBNull(1) ? null : new CollectionId(reader.GetString(1)),
            reader.GetInt32(2), reader.GetString(4), new HelpCenterSlug(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(3)), "published article summary");

    private static PublishedArticle ReadPublished(NpgsqlDataReader reader, TicketScope scope) => Decode(() =>
    {
        var content = new ArticleTranslation(new HelpCenterLocale(reader.GetString(6)), reader.GetString(7), new HelpCenterSlug(reader.GetString(8)), reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetFieldValue<string[]>(11));
        return new PublishedArticle(scope, new ArticleId(reader.GetString(0)), new PortalId(reader.GetString(1)), reader.IsDBNull(2) ? null : new CollectionId(reader.GetString(2)),
            reader.GetInt32(3), reader.GetInt32(4), reader.GetFieldValue<DateTimeOffset>(5), content);
    }, "published article");
}
