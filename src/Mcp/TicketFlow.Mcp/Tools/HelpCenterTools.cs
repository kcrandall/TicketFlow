using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.HelpCenter;

using P = TicketFlow.Mcp.TicketFlowMcpPermissions;
using TicketFlow.Core;

namespace TicketFlow.Mcp.Tools;

/// <summary>Help center tools: search, portals, articles, drafts and publishing.</summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.HelpCenter)]
public sealed class HelpCenterTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = ConversationTools.WorkspaceHelp;

    private IHelpCenterService Service => context.Service<IHelpCenterService>();

    [McpServerTool(Name = "helpcenter_search", Title = "Search help center", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<ArticleSnippetOutput>))]
    [Description("Full-text search over published help center articles of one portal and locale; returns ranked snippets." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.HelpCenterRead, Service = typeof(IHelpCenterSearch))]
    public Task<CallToolResult> Search(
        [Description("Portal id.")] string portalId,
        [Description("Locale, for example 'en'.")] string locale,
        [Description("Search text.")] string query,
        [Description("Results, 1-50 (default 10).")] int limit = 10,
        [Description("Offset, 0-200.")] int offset = 0,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await context.Service<IHelpCenterSearch>().SearchAsync(principal,
            new HelpCenterSearchQuery(new PortalId(portalId), new HelpCenterLocale(locale), query, limit, offset), cancellationToken), snippets => new ToolList<ArticleSnippetOutput>(
            [.. snippets.Select(snippet => new ArticleSnippetOutput(snippet.Article.Value, snippet.Portal.Value, snippet.Locale.Value, context.Untrusted(ToolViews.Article, snippet.Title),
                snippet.Slug.Value, context.UntrustedOrNull(ToolViews.Article, snippet.Excerpt), context.Untrusted(ToolViews.Article, snippet.Snippet), snippet.Rank,
                snippet.RevisionNumber))])), cancellationToken);

    [McpServerTool(Name = "helpcenter_list_portals", Title = "List portals", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolList<PortalOutput>))]
    [Description("Lists help center portals (slug, name, locales, visibility).")]
    [TicketFlowTool(P.HelpCenterRead)]
    public Task<CallToolResult> ListPortals(
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.ListPortalsAsync(principal, cancellationToken), list => new ToolList<PortalOutput>(
            [.. list.Select(stored => new PortalOutput(stored.Portal.Id.Value, stored.Portal.Slug.Value, stored.Portal.Name, stored.Portal.DefaultLocale.Value,
                [.. stored.Portal.SupportedLocales.Select(locale => locale.Value)], TicketFlowToolJson.Token(stored.Portal.Visibility), stored.Revision))])), cancellationToken);

    [McpServerTool(Name = "helpcenter_list_articles", Title = "List articles", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolPage<ArticleSummaryOutput>))]
    [Description("Lists articles of a portal (drafts included), optionally by collection and state (draft, published, archived). Titles only; read one with helpcenter_get_article.")]
    [TicketFlowTool(P.HelpCenterRead)]
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
            return Map(await Service.ListArticlesAsync(principal, query, cancellationToken), page => new ToolPage<ArticleSummaryOutput>(
                [.. page.Items.Select(stored => new ArticleSummaryOutput(stored.Id.Value, TicketFlowToolJson.Token(stored.Record.State), stored.Record.Article.Collection?.Value,
                    stored.Record.PublishedRevision,
                    [.. stored.Record.Article.Translations.Select(translation => new ArticleTitleOutput(translation.Locale.Value, context.Untrusted(ToolViews.Article, translation.Title)))],
                    stored.Record.UpdatedAt, stored.Revision))],
                page.HasMore && page.Items.Count > 0 ? TicketFlowToolJson.EncodeCursor(page.Items[^1].Id.Value) : null));
        }, cancellationToken);

    [McpServerTool(Name = "helpcenter_get_article", Title = "Get article", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ArticleOutput))]
    [Description("Reads an article's current draft content (all translations, Markdown bodies) and state. Long bodies are truncated (truncated: true)." + ToolViews.UntrustedNote)]
    [TicketFlowTool(P.HelpCenterRead)]
    public Task<CallToolResult> GetArticle(
        [Description("Article id.")] string articleId,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.GetArticleAsync(principal, new ArticleId(articleId), cancellationToken), Article), cancellationToken);

    [McpServerTool(Name = "helpcenter_create_draft", Title = "Create article draft", Destructive = false, Idempotent = false, UseStructuredContent = true, OutputSchemaType = typeof(ArticleOutput))]
    [Description("Creates a draft article with one translation (Markdown body). Drafts are not visible to visitors until published.")]
    [TicketFlowTool(P.HelpCenterWrite)]
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

    [McpServerTool(Name = "helpcenter_update_draft", Title = "Update article draft", Destructive = false, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ArticleOutput))]
    [Description("Adds or replaces one translation of an article's draft (omitted fields keep the current translation's values). The live published revision is unchanged until helpcenter_publish.")]
    [TicketFlowTool(P.HelpCenterWrite)]
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
            if (read is not TicketValue<StoredArticle> current)
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

    [McpServerTool(Name = "helpcenter_publish", Title = "Publish article", Destructive = false, Idempotent = false, OpenWorld = true, UseStructuredContent = true, OutputSchemaType = typeof(ArticleOutput))]
    [Description("Publishes the article's draft as a new immutable revision and serves it to visitors (public portals are visible to anyone).")]
    [TicketFlowTool(P.HelpCenterPublish)]
    public Task<CallToolResult> Publish(
        [Description("Article id.")] string articleId,
        [Description("Current article revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.PublishArticleAsync(principal, new ArticleId(articleId), expectedRevision, cancellationToken), Article),
            cancellationToken);

    [McpServerTool(Name = "helpcenter_unpublish", Title = "Unpublish article", Destructive = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ArticleOutput))]
    [Description("Stops serving the article to visitors (it returns to draft; revisions are kept).")]
    [TicketFlowTool(P.HelpCenterPublish)]
    public Task<CallToolResult> Unpublish(
        [Description("Article id.")] string articleId,
        [Description("Current article revision.")] long expectedRevision,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal => Map(await Service.UnpublishArticleAsync(principal, new ArticleId(articleId), expectedRevision, cancellationToken), Article),
            cancellationToken);

    private ArticleOutput Article(StoredArticle stored) => ToArticleOutput(stored, context);

    internal static ArticleOutput ToArticleOutput(StoredArticle stored, TicketFlowToolContext context) => new(
        stored.Id.Value,
        stored.Record.Article.Portal.Value,
        stored.Record.Article.Collection?.Value,
        TicketFlowToolJson.Token(stored.Record.State),
        stored.Record.PublishedRevision,
        [.. stored.Record.Article.Translations.Select(translation => new ArticleTranslationOutput(translation.Locale.Value, context.Untrusted(ToolViews.Article, translation.Title),
            translation.Slug.Value, context.UntrustedOrNull(ToolViews.Article, translation.Excerpt), translation.Tags, context.Untrusted(ToolViews.Article, translation.Body)))],
        ToolViews.Actor(stored.Record.CreatedBy),
        ToolViews.Actor(stored.Record.UpdatedBy),
        stored.Record.UpdatedAt,
        stored.Revision);

    private static object Map<T>(TicketResult<T> result, Func<T, object> success) => TicketFlowToolError.Map(result, success);
}
