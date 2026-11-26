using TicketFlow.Core;
using TicketFlow.Testing;
using static TicketFlow.HelpCenter.StoreContracts.HelpCenterStoreContract;
using static TicketFlow.HelpCenter.Tests.HelpCenterFixture;

namespace TicketFlow.HelpCenter.Tests;

[TestClass]
public sealed class PublicReaderTests
{
    private static async Task<(HelpCenterFixture Fixture, UserPrincipal Editor)> SeedAsync(PortalVisibility visibility = PortalVisibility.Public)
    {
        var fixture = Create();
        var editor = await fixture.EditorAsync();
        Value(await fixture.Service.CreatePortalAsync(editor, SamplePortal(visibility: visibility)));
        Value(await fixture.Service.CreateCollectionAsync(editor, SampleCollection("guides")));
        Value(await fixture.Service.CreateArticleAsync(editor, SampleArticle("live", collection: "guides", title: "Reset your password", body: "Click <script>alert(1)</script> **reset**.", slug: "reset")));
        Value(await fixture.Service.PublishArticleAsync(editor, new ArticleId("live"), 1));
        Value(await fixture.Service.CreateArticleAsync(editor, SampleArticle("draft", title: "Secret password roadmap", slug: "secret")));
        return (fixture, editor);
    }

    [TestMethod]
    public async Task VisitorsSeeOnlyPublishedArticlesOfPublicPortals()
    {
        var (fixture, _) = await SeedAsync();
        using var _fixture = fixture;
        var slug = new HelpCenterSlug("help");
        var portal = (await fixture.Public.GetPortalAsync(fixture.Scope, slug))!;
        Assert.AreEqual("Acme Help", portal.Name);
        var article = (await fixture.Public.GetArticleAsync(fixture.Scope, slug, English, new HelpCenterSlug("reset")))!;
        Assert.AreEqual("Reset your password", article.Title);
        Assert.AreEqual("<p>Click &lt;script&gt;alert(1)&lt;/script&gt; <strong>reset</strong>.</p>\n", article.Html);
        Assert.IsNull(await fixture.Public.GetArticleAsync(fixture.Scope, slug, English, new HelpCenterSlug("secret")));
        Assert.IsNull(await fixture.Public.GetArticleAsync(fixture.Scope, slug, new HelpCenterLocale("fr"), new HelpCenterSlug("reset")));
        Assert.IsNull(await fixture.Public.GetArticleAsync(TestScopes.LevelVariants(fixture.Scope)[1], slug, English, new HelpCenterSlug("reset")));
        CollectionAssert.AreEqual(new[] { "live" }, (await fixture.Public.ListArticlesAsync(fixture.Scope, slug, English)).Items.Select(item => item.Id.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "live" }, (await fixture.Public.SearchAsync(fixture.Scope, slug, English, "password")).Select(hit => hit.Article.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "live" }, (await fixture.Search.SearchPublicAsync(fixture.Scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "password"))).Select(hit => hit.Article.Value).ToArray());
        Assert.IsEmpty(await fixture.Public.SearchAsync(fixture.Scope, slug, English, "   "));
        Assert.IsEmpty(await fixture.Public.SearchAsync(fixture.Scope, slug, English, new string('a', 300)));
        Assert.IsEmpty(await fixture.Public.SearchAsync(fixture.Scope, slug, English, "bad\u0001query"));
        var collections = await fixture.Public.ListCollectionsAsync(fixture.Scope, slug, German);
        Assert.AreEqual(English, collections.Single().Locale, "Falls back to the portal's default locale.");
    }

    [TestMethod]
    public async Task PrivatePortalsLookMissing()
    {
        var (fixture, _) = await SeedAsync(PortalVisibility.Private);
        using var _fixture = fixture;
        var slug = new HelpCenterSlug("help");
        Assert.IsNull(await fixture.Public.GetPortalAsync(fixture.Scope, slug));
        Assert.IsNull(await fixture.Public.GetArticleAsync(fixture.Scope, slug, English, new HelpCenterSlug("reset")));
        Assert.IsEmpty((await fixture.Public.ListArticlesAsync(fixture.Scope, slug, English)).Items);
        Assert.IsEmpty(await fixture.Public.ListCollectionsAsync(fixture.Scope, slug, English));
        Assert.IsEmpty(await fixture.Public.SearchAsync(fixture.Scope, slug, English, "password"));
        Assert.IsInstanceOfType<FeedbackArticleNotFound>((await fixture.Public.RecordFeedbackAsync(fixture.Scope, slug, new ArticleId("live"), "visitor-123", true)).Value);
    }

    [TestMethod]
    public async Task FeedbackIsIdempotentPerVisitor()
    {
        var (fixture, editor) = await SeedAsync();
        using var _fixture = fixture;
        var slug = new HelpCenterSlug("help");
        var live = new ArticleId("live");
        Assert.IsInstanceOfType<FeedbackCounted>((await fixture.Public.RecordViewAsync(fixture.Scope, slug, live, "visitor-123")).Value);
        Assert.IsInstanceOfType<FeedbackUnchanged>((await fixture.Public.RecordViewAsync(fixture.Scope, slug, live, "visitor-123")).Value);
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        Assert.IsInstanceOfType<FeedbackCounted>((await fixture.Public.RecordViewAsync(fixture.Scope, slug, live, "visitor-123")).Value);
        Assert.IsInstanceOfType<FeedbackCounted>((await fixture.Public.RecordFeedbackAsync(fixture.Scope, slug, live, "visitor-123", true)).Value);
        Assert.IsInstanceOfType<FeedbackUnchanged>((await fixture.Public.RecordFeedbackAsync(fixture.Scope, slug, live, "visitor-123", true)).Value);
        Assert.IsInstanceOfType<FeedbackCounted>((await fixture.Public.RecordFeedbackAsync(fixture.Scope, slug, live, "visitor-456", false)).Value);
        Assert.IsInstanceOfType<FeedbackArticleNotFound>((await fixture.Public.RecordFeedbackAsync(fixture.Scope, slug, new ArticleId("draft"), "visitor-123", true)).Value);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => fixture.Public.RecordViewAsync(fixture.Scope, slug, live, "short"));
        Assert.AreEqual(new ArticleFeedbackCounts(2, 1, 1), Value(await fixture.Service.GetFeedbackAsync(editor, live)));
    }
}
