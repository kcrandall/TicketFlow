using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.HelpCenter;

namespace TicketFlow.Mcp.Tools;

/// <summary>Help center tools: search, portals, articles, drafts and publishing.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.HelpCenter)]
public sealed class HelpCenterTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    private IHelpCenterService Service => context.Service<IHelpCenterService>();

    [McpServerTool(Name = "helpcenter_search", Title = "Search help center", ReadOnly = true, Idempotent = true)]
    [Description("Full-text search over published help center articles of one portal and locale; returns ranked snippets.")]
    [TicketFlowTool("helpcenter:read", Service = typeof(IHelpCenterSearch))]
    public Task<CallToolResult> Search(
        [Description("Portal id.")] string portalId,
        [Description("Locale, for example 'en'.")] string locale,
        [Description("Search text.")] string query,
        [Description("Results, 1-50 (default 10).")] int limit = 10,
        [Description("Offset, 0-200.")] int offset = 0,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IHelpCenterSearch>().SearchAsync(principal,
            new HelpCenterSearchQuery(new PortalId(portalId), new HelpCenterLocale(locale), query, limit, offset), cancellationToken), snippets => new
            {
                items = snippets.Select(snippet => new
                {
                    articleId = snippet.Article.Value,
                    portalId = snippet.Portal.Value,
                    locale = snippet.Locale.Value,
                    title = snippet.Title,
                    slug = snippet.Slug.Value,
                    excerpt = snippet.Excerpt,
                    snippet = snippet.Snippet,
                    rank = snippet.Rank,
                    revisionNumber = snippet.RevisionNumber,
                }).ToList(),
            }), cancellationToken);

    [McpServerTool(Name = "helpcenter_list_portals", Title = "List portals", ReadOnly = true, Idempotent = true)]
    [Description("Lists help center portals (slug, name, locales, visibility).")]
    [TicketFlowTool("helpcenter:read")]
    public Task<CallToolResult> ListPortals(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.ListPortalsAsync(principal, cancellationToken), list => new
        {
            items = list.Select(stored => new
            {
                id = stored.Portal.Id.Value,
                slug = stored.Portal.Slug.Value,
                name = stored.Portal.Name,
                defaultLocale = stored.Portal.DefaultLocale.Value,
                supportedLocales = stored.Portal.SupportedLocales.Select(locale => locale.Value).ToList(),
                visibility = TicketFlowToolJson.Token(stored.Portal.Visibility),
                revision = stored.Revision,
            }).ToList(),
        }), cancellationToken);

    [McpServerTool(Name = "helpcenter_list_articles", Title = "List articles", ReadOnly = true, Idempotent = true)]
    [Description("Lists articles of a portal (drafts included), optionally by collection and state (draft, published, archived). Titles only; read one with helpcenter_get_article.")]
    [TicketFlowTool("helpcenter:read")]
    public Task<CallToolResult> ListArticles(
        [Description("Portal id.")] string portalId,
        [Description("Only this collection.")] string? collectionId = null,
        [Description("draft, published or archived.")] string? state = null,
        [Description("Page size, 1-100 (default 50).")] int limit = 50,
        [Description("The 'next' cursor of a previous page.")] string? cursor = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var query = new ArticleListQuery(new PortalId(portalId), collectionId is null ? null : new CollectionId(collectionId),
                TicketFlowToolJson.ParseOptional<ArticleState>(state, nameof(state)), limit, cursor is null ? null : new ArticleId(TicketFlowToolJson.DecodeCursor(cursor, 1)[0]));
            return Map(await Service.ListArticlesAsync(principal, query, cancellationToken), page => new
            {
                items = page.Items.Select(stored => new
                {
                    id = stored.Id.Value,
                    state = TicketFlowToolJson.Token(stored.Record.State),
                    collectionId = stored.Record.Article.Collection?.Value,
                    publishedRevision = stored.Record.PublishedRevision,
                    titles = stored.Record.Article.Translations.Select(translation => new { locale = translation.Locale.Value, title = translation.Title }).ToList(),
                    updatedAt = stored.Record.UpdatedAt,
                    revision = stored.Revision,
                }).ToList(),
                next = page.HasMore && page.Items.Count > 0 ? TicketFlowToolJson.EncodeCursor(page.Items[^1].Id.Value) : null,
            });
        }, cancellationToken);

    [McpServerTool(Name = "helpcenter_get_article", Title = "Get article", ReadOnly = true, Idempotent = true)]
    [Description("Reads an article's current draft content (all translations, Markdown bodies) and state. Long bodies are truncated (truncated: true).")]
    [TicketFlowTool("helpcenter:read")]
    public Task<CallToolResult> GetArticle(
        [Description("Article id.")] string articleId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.GetArticleAsync(principal, new ArticleId(articleId), cancellationToken), Article), cancellationToken);

    [McpServerTool(Name = "helpcenter_create_draft", Title = "Create article draft", Destructive = false, Idempotent = false)]
    [Description("Creates a draft article with one translation (Markdown body). Drafts are not visible to visitors until published.")]
    [TicketFlowTool("helpcenter:write")]
    public Task<CallToolResult> CreateDraft(
        [Description("Portal id.")] string portalId,
        [Description("Locale (must be supported by the portal).")] string locale,
        [Description("Title.")] string title,
        [Description("URL slug (lowercase letters, digits and '-').")] string slug,
        [Description("Markdown body.")] string body,
        [Description("Short excerpt.")] string? excerpt = null,
        [Description("Tags.")] string[]? tags = null,
        [Description("Collection id.")] string? collectionId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var article = new Article(new ArticleId(Guid.CreateVersion7().ToString("N")), new PortalId(portalId),
                [new ArticleTranslation(new HelpCenterLocale(locale), title, new HelpCenterSlug(slug), body, excerpt, tags)], collectionId is null ? null : new CollectionId(collectionId));
            return Map(await Service.CreateArticleAsync(principal, article, cancellationToken), Article);
        }, cancellationToken);

    [McpServerTool(Name = "helpcenter_update_draft", Title = "Update article draft", Destructive = false, Idempotent = true)]
    [Description("Adds or replaces one translation of an article's draft (omitted fields keep the current translation's values). The live published revision is unchanged until helpcenter_publish.")]
    [TicketFlowTool("helpcenter:write")]
    public Task<CallToolResult> UpdateDraft(
        [Description("Article id.")] string articleId,
        [Description("Current article revision.")] long expectedRevision,
        [Description("Locale of the translation to add or replace.")] string locale,
        [Description("Title.")] string? title = null,
        [Description("URL slug.")] string? slug = null,
        [Description("Markdown body.")] string? body = null,
        [Description("Short excerpt.")] string? excerpt = null,
        [Description("Tags.")] string[]? tags = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var read = await Service.GetArticleAsync(principal, new ArticleId(articleId), cancellationToken);
            if (read is not HelpCenterSucceeded<StoredArticle> current)
                return Map(read, Article);
            var article = current.Value.Record.Article;
            var target = new HelpCenterLocale(locale);
            var existing = article.Translations.FirstOrDefault(translation => translation.Locale == target);
            var translation = new ArticleTranslation(target,
                title ?? existing?.Title ?? throw new ArgumentException("'title' is required for a new translation.", nameof(title)),
                slug is not null ? new HelpCenterSlug(slug) : existing?.Slug ?? throw new ArgumentException("'slug' is required for a new translation.", nameof(slug)),
                body ?? existing?.Body ?? throw new ArgumentException("'body' is required for a new translation.", nameof(body)),
                excerpt ?? existing?.Excerpt, tags ?? existing?.Tags);
            var updated = new Article(article.Id, article.Portal, [.. article.Translations.Where(item => item.Locale != target), translation], article.Collection, article.Position);
            return Map(await Service.UpdateArticleAsync(principal, updated, expectedRevision, cancellationToken), Article);
        }, cancellationToken);

    [McpServerTool(Name = "helpcenter_publish", Title = "Publish article", Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Publishes the article's draft as a new immutable revision and serves it to visitors (public portals are visible to anyone).")]
    [TicketFlowTool("helpcenter:publish")]
    public Task<CallToolResult> Publish(
        [Description("Article id.")] string articleId,
        [Description("Current article revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.PublishArticleAsync(principal, new ArticleId(articleId), expectedRevision, cancellationToken), Article),
            cancellationToken);

    [McpServerTool(Name = "helpcenter_unpublish", Title = "Unpublish article", Destructive = true, Idempotent = true)]
    [Description("Stops serving the article to visitors (it returns to draft; revisions are kept).")]
    [TicketFlowTool("helpcenter:publish")]
    public Task<CallToolResult> Unpublish(
        [Description("Article id.")] string articleId,
        [Description("Current article revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.UnpublishArticleAsync(principal, new ArticleId(articleId), expectedRevision, cancellationToken), Article),
            cancellationToken);

    private object Article(StoredArticle stored) => new
    {
        id = stored.Id.Value,
        portalId = stored.Record.Article.Portal.Value,
        collectionId = stored.Record.Article.Collection?.Value,
        state = TicketFlowToolJson.Token(stored.Record.State),
        publishedRevision = stored.Record.PublishedRevision,
        translations = stored.Record.Article.Translations.Select(translation => new
        {
            locale = translation.Locale.Value,
            title = translation.Title,
            slug = translation.Slug.Value,
            excerpt = translation.Excerpt,
            tags = translation.Tags,
            body = context.Text(translation.Body, out var truncated),
            truncated,
        }).ToList(),
        createdBy = ToolViews.Actor(stored.Record.CreatedBy),
        updatedBy = ToolViews.Actor(stored.Record.UpdatedBy),
        updatedAt = stored.Record.UpdatedAt,
        revision = stored.Revision,
    };

    private static object Map<T>(HelpCenterResult<T> result, Func<T, object> success) => result switch
    {
        HelpCenterSucceeded<T> succeeded => success(succeeded.Value),
        HelpCenterRefused refused => refused.Reason switch
        {
            HelpCenterRefusal.Forbidden => TicketFlowToolError.Forbidden(),
            HelpCenterRefusal.Invalid => TicketFlowToolError.Invalid(refused.Errors),
            HelpCenterRefusal.NotFound => TicketFlowToolError.NotFound(),
            HelpCenterRefusal.Conflict => TicketFlowToolError.Conflict(refused.CurrentRevision),
            _ => TicketFlowToolError.Rejected(refused.Errors),
        },
    };
}
