using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.HelpCenter.StoreContracts;

/// <summary>
/// Behavioral contract every help center store must satisfy. <c>primary</c> and <c>competing</c> must be independent clients of the
/// same storage, so fencing is verified across clients. Each verification uses fresh identifiers within a fresh scope.
/// </summary>
public static class HelpCenterStoreContract
{
    public static readonly HelpCenterLocale English = new("en");
    public static readonly HelpCenterLocale German = new("de");
    public static readonly AccessActor Author = new(AccessActorKind.User, "author");
    public static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public static string Describe<T>(HelpCenterWriteResult<T> result, Func<T, long> revision) => result switch
    {
        HelpCenterWriteApplied<T> applied => $"applied:{revision(applied.Value)}",
        HelpCenterWriteNotFound => "not-found",
        HelpCenterWriteConflict conflict => $"conflict:{conflict.CurrentRevision}",
        HelpCenterWriteRejected rejected => $"rejected:{rejected.Reason}",
    };

    public static string P(HelpCenterWriteResult<StoredPortal> result) => Describe(result, stored => stored.Revision);
    public static string C(HelpCenterWriteResult<StoredCollection> result) => Describe(result, stored => stored.Revision);
    public static string A(HelpCenterWriteResult<StoredArticle> result) => Describe(result, stored => stored.Revision);

    public static TicketScope NewScope() => TestScopes.Create(workspaceId: "hc-" + Guid.NewGuid().ToString("N"));

    public static Portal SamplePortal(string id = "portal", string slug = "help", PortalVisibility visibility = PortalVisibility.Public) =>
        new(new PortalId(id), new HelpCenterSlug(slug), "Acme Help", English, [German], visibility, "help.example.com");

    public static HelpCollection SampleCollection(string id, string portal = "portal", string? parent = null, int position = 0) =>
        new(new CollectionId(id), new PortalId(portal), [new CollectionTranslation(English, "Getting started " + id, new HelpCenterSlug("c-" + id), "Basics.")],
            parent is null ? null : new CollectionId(parent), position);

    public static ArticleTranslation Translation(HelpCenterLocale locale, string title, string slug, string body, params string[] tags) =>
        new(locale, title, new HelpCenterSlug(slug), body, "Excerpt of " + title, tags);

    public static Article SampleArticle(string id, string portal = "portal", string? collection = null, int position = 0, string? title = null, string? body = null, string? slug = null) =>
        new(new ArticleId(id), new PortalId(portal),
            [Translation(English, title ?? "Article " + id, slug ?? "a-" + id, body ?? "Body of **" + id + "**.", "faq"),
             Translation(German, "Artikel " + id, "de-" + (slug ?? "a-" + id), "Inhalt " + id)],
            collection is null ? null : new CollectionId(collection), position);

    public static ArticleRecord Draft(Article article) => new(article, ArticleState.Draft, null, Author, Now, Author, Now);

    public static ArticleRevision RevisionOf(Article article, int number) =>
        new(article.Id, number, article.Translations.Select(translation => new PublishedTranslation(translation, translation.Body.Replace("*", "", StringComparison.Ordinal))), Author, Now.AddMinutes(number));

    public static ArticleRecord Published(Article article, int revision) => new(article, ArticleState.Published, revision, Author, Now, Author, Now.AddMinutes(revision));

    public static async Task<StoredArticle> PublishAsync(IHelpCenterStore store, TicketScope scope, Article article, long expectedRevision, int number)
    {
        var result = await store.UpdateArticleAsync(scope, Published(article, number), expectedRevision, RevisionOf(article, number));
        Assert.IsInstanceOfType<HelpCenterWriteApplied<StoredArticle>>(result.Value, A(result));
        return ((HelpCenterWriteApplied<StoredArticle>)result.Value!).Value;
    }

    public static async Task VerifyPortalsAsync(IHelpCenterStore primary, IHelpCenterStore competing)
    {
        var scope = NewScope();
        var portal = SamplePortal();
        Assert.IsNull(await primary.GetPortalAsync(scope, portal.Id));
        Assert.AreEqual("not-found", P(await primary.UpdatePortalAsync(scope, portal, 1)));
        Assert.AreEqual("applied:1", P(await primary.CreatePortalAsync(scope, portal)));
        Assert.AreEqual("conflict:1", P(await competing.CreatePortalAsync(scope, portal)));
        Assert.AreEqual("rejected:SlugConflict", P(await competing.CreatePortalAsync(scope, SamplePortal("other", "help"))));
        var stored = (await competing.GetPortalAsync(scope, portal.Id))!;
        Assert.AreEqual(portal, stored.Portal);
        Assert.AreEqual(scope, stored.Scope);
        Assert.AreEqual(portal, (await competing.FindPortalBySlugAsync(scope, portal.Slug))!.Portal);
        CollectionAssert.AreEqual(new[] { German, English }.OrderBy(locale => locale).ToArray(), stored.Portal.SupportedLocales.ToArray());

        var renamed = new Portal(portal.Id, new HelpCenterSlug("support"), "Support", German, [English], PortalVisibility.Private);
        Assert.AreEqual("applied:2", P(await competing.UpdatePortalAsync(scope, renamed, 1)));
        Assert.AreEqual("conflict:2", P(await primary.UpdatePortalAsync(scope, portal, 1)));
        Assert.IsNull(await primary.FindPortalBySlugAsync(scope, portal.Slug));
        Assert.AreEqual(renamed, (await primary.FindPortalBySlugAsync(scope, renamed.Slug))!.Portal);
        Assert.AreEqual("applied:1", P(await primary.CreatePortalAsync(scope, SamplePortal("second", "help"))));
        Assert.AreEqual("rejected:SlugConflict", P(await primary.UpdatePortalAsync(scope, SamplePortal("second", "support"), 1)));
        CollectionAssert.AreEqual(new[] { "portal", "second" }, (await competing.ListPortalsAsync(scope)).Select(item => item.Portal.Id.Value).ToArray());

        // Both writers keep the slug unique so the race can only end in a revision conflict.
        var retitled = new Portal(portal.Id, new HelpCenterSlug("support"), "Support desk", German, [English], PortalVisibility.Private);
        var outcomes = await Task.WhenAll(primary.UpdatePortalAsync(scope, renamed, 2), competing.UpdatePortalAsync(scope, retitled, 2));
        CollectionAssert.AreEquivalent(new[] { "applied:3", "conflict:3" }, outcomes.Select(P).ToArray());

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetPortalAsync(other, portal.Id));
            Assert.IsEmpty(await primary.ListPortalsAsync(other));
            Assert.AreEqual("not-found", P(await primary.DeletePortalAsync(other, portal.Id, 3)));
        }

        Assert.AreEqual("applied:1", C(await primary.CreateCollectionAsync(scope, SampleCollection("c1"))));
        Assert.AreEqual("rejected:NotEmpty", P(await primary.DeletePortalAsync(scope, portal.Id, 3)));
        Assert.AreEqual("applied:1", C(await primary.DeleteCollectionAsync(scope, new CollectionId("c1"), 1)));
        Assert.AreEqual("conflict:3", P(await primary.DeletePortalAsync(scope, portal.Id, 2)));
        Assert.AreEqual("applied:3", P(await competing.DeletePortalAsync(scope, portal.Id, 3)));
        Assert.AreEqual("not-found", P(await primary.DeletePortalAsync(scope, portal.Id, 3)));
    }

    public static async Task VerifyPortalCapacityAsync(IHelpCenterStore store)
    {
        var scope = NewScope();
        for (var index = 0; index < HelpCenterLimits.MaximumPortalsPerWorkspace; index++)
            Assert.AreEqual("applied:1", P(await store.CreatePortalAsync(scope, SamplePortal($"p{index:D3}", $"p{index:D3}"))));
        Assert.AreEqual("rejected:CapacityExceeded", P(await store.CreatePortalAsync(scope, SamplePortal("overflow", "overflow"))));
    }

    public static async Task VerifyCollectionsAsync(IHelpCenterStore primary, IHelpCenterStore competing)
    {
        var scope = NewScope();
        Assert.AreEqual("rejected:InvalidReference", C(await primary.CreateCollectionAsync(scope, SampleCollection("root"))));
        Assert.AreEqual("applied:1", P(await primary.CreatePortalAsync(scope, SamplePortal())));
        Assert.AreEqual("applied:1", P(await primary.CreatePortalAsync(scope, SamplePortal("other", "other"))));
        Assert.AreEqual("applied:1", C(await primary.CreateCollectionAsync(scope, SampleCollection("root", position: 2))));
        Assert.AreEqual("conflict:1", C(await competing.CreateCollectionAsync(scope, SampleCollection("root"))));
        Assert.AreEqual("applied:1", C(await primary.CreateCollectionAsync(scope, SampleCollection("first", position: 1))));
        Assert.AreEqual("applied:1", C(await primary.CreateCollectionAsync(scope, SampleCollection("child", parent: "root"))));
        Assert.AreEqual("applied:1", C(await primary.CreateCollectionAsync(scope, SampleCollection("grandchild", parent: "child"))));
        Assert.AreEqual("rejected:DepthExceeded", C(await primary.CreateCollectionAsync(scope, SampleCollection("too-deep", parent: "grandchild"))));
        Assert.AreEqual("rejected:InvalidReference", C(await primary.CreateCollectionAsync(scope, SampleCollection("orphan", parent: "missing"))));
        Assert.AreEqual("rejected:InvalidReference", C(await primary.CreateCollectionAsync(scope, SampleCollection("cross", portal: "other", parent: "root"))));

        var grandchild = (await competing.GetCollectionAsync(scope, new CollectionId("grandchild")))!;
        Assert.AreEqual(3, grandchild.Depth);
        Assert.AreEqual(SampleCollection("grandchild", parent: "child"), grandchild.Collection);
        CollectionAssert.AreEqual(new[] { "first", "root", "child", "grandchild" },
            (await competing.ListCollectionsAsync(scope, new PortalId("portal"))).Select(item => item.Collection.Id.Value).ToArray());
        Assert.IsEmpty(await competing.ListCollectionsAsync(scope, new PortalId("other")));

        Assert.AreEqual("applied:2", C(await competing.UpdateCollectionAsync(scope, SampleCollection("root", position: 0), 1)));
        Assert.AreEqual("conflict:2", C(await primary.UpdateCollectionAsync(scope, SampleCollection("root", position: 5), 1)));
        Assert.AreEqual("rejected:InvalidReference", C(await primary.UpdateCollectionAsync(scope, SampleCollection("child", parent: "first"), 1)));
        Assert.AreEqual("rejected:InvalidReference", C(await primary.UpdateCollectionAsync(scope, SampleCollection("child"), 1)));
        Assert.AreEqual("not-found", C(await primary.UpdateCollectionAsync(scope, SampleCollection("missing"), 1)));
        Assert.AreEqual(2, (await primary.GetCollectionAsync(scope, new CollectionId("child")))!.Depth);

        Assert.AreEqual("rejected:NotEmpty", C(await primary.DeleteCollectionAsync(scope, new CollectionId("child"), 1)));
        Assert.AreEqual("applied:1", A(await primary.CreateArticleAsync(scope, Draft(SampleArticle("in-first", collection: "first")))));
        Assert.AreEqual("rejected:NotEmpty", C(await primary.DeleteCollectionAsync(scope, new CollectionId("first"), 1)));
        Assert.AreEqual("applied:1", C(await primary.DeleteCollectionAsync(scope, new CollectionId("grandchild"), 1)));
        Assert.AreEqual("applied:1", C(await competing.DeleteCollectionAsync(scope, new CollectionId("child"), 1)));
        Assert.IsNull(await primary.GetCollectionAsync(scope, new CollectionId("child")));
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await primary.GetCollectionAsync(other, new CollectionId("root")));
    }

    public static async Task VerifyArticlesAsync(IHelpCenterStore primary, IHelpCenterStore competing)
    {
        var scope = NewScope();
        Assert.AreEqual("rejected:InvalidReference", A(await primary.CreateArticleAsync(scope, Draft(SampleArticle("a1")))));
        Assert.AreEqual("applied:1", P(await primary.CreatePortalAsync(scope, SamplePortal())));
        Assert.AreEqual("applied:1", P(await primary.CreatePortalAsync(scope, SamplePortal("other", "other"))));
        Assert.AreEqual("applied:1", C(await primary.CreateCollectionAsync(scope, SampleCollection("guides"))));
        Assert.AreEqual("applied:1", C(await primary.CreateCollectionAsync(scope, SampleCollection("foreign", portal: "other"))));
        Assert.AreEqual("rejected:InvalidReference", A(await primary.CreateArticleAsync(scope, Draft(SampleArticle("a1", collection: "foreign")))));
        Assert.AreEqual("rejected:InvalidRevision", A(await primary.CreateArticleAsync(scope, Published(SampleArticle("a1"), 1))));

        var article = SampleArticle("a1", collection: "guides", title: "Reset your password", slug: "reset-password");
        Assert.AreEqual("applied:1", A(await primary.CreateArticleAsync(scope, Draft(article))));
        Assert.AreEqual("conflict:1", A(await competing.CreateArticleAsync(scope, Draft(article))));
        var stored = (await competing.GetArticleAsync(scope, article.Id))!;
        Assert.AreEqual(article, stored.Record.Article);
        Assert.AreEqual(ArticleState.Draft, stored.Record.State);
        Assert.IsNull(stored.Record.PublishedRevision);
        Assert.AreEqual(Author, stored.Record.CreatedBy);
        Assert.AreEqual(Now, stored.Record.CreatedAt);
        Assert.IsNull(await primary.GetPublishedByIdAsync(scope, article.Id, English));

        // Fixed portal, next-number revisions, published reference must exist.
        Assert.AreEqual("rejected:InvalidReference", A(await primary.UpdateArticleAsync(scope, Draft(SampleArticle("a1", portal: "other")), 1)));
        Assert.AreEqual("rejected:InvalidRevision", A(await primary.UpdateArticleAsync(scope, Published(article, 1), 1)));
        Assert.AreEqual("rejected:InvalidRevision", A(await primary.UpdateArticleAsync(scope, Published(article, 2), 1, RevisionOf(article, 2))));
        Assert.AreEqual("rejected:InvalidRevision", A(await primary.UpdateArticleAsync(scope, Published(article, 1), 1, RevisionOf(SampleArticle("a2"), 1))));

        await PublishAsync(primary, scope, article, 1, 1);
        Assert.AreEqual("conflict:2", A(await competing.UpdateArticleAsync(scope, Draft(article), 1)));
        var live = (await competing.GetPublishedAsync(scope, article.Portal, English, new HelpCenterSlug("reset-password")))!;
        Assert.AreEqual(article.Id, live.Article);
        Assert.AreEqual(1, live.RevisionNumber);
        Assert.AreEqual(new CollectionId("guides"), live.Collection);
        Assert.AreEqual(article.Translation(English), live.Content);
        Assert.AreEqual(Now.AddMinutes(1), live.PublishedAt);
        Assert.AreEqual(article.Translation(German), (await competing.GetPublishedByIdAsync(scope, article.Id, German))!.Content);

        // Editing the draft of a published article keeps serving the live revision, with the current collection and position.
        var edited = new Article(article.Id, article.Portal, [Translation(English, "Changed title", "changed", "Changed body")], null, 7);
        Assert.AreEqual("applied:3", A(await competing.UpdateArticleAsync(scope, Published(edited, 1), 2)));
        live = (await primary.GetPublishedAsync(scope, article.Portal, English, new HelpCenterSlug("reset-password")))!;
        Assert.AreEqual("Reset your password", live.Content.Title);
        Assert.IsNull(live.Collection);
        Assert.AreEqual(7, live.Position);
        Assert.IsNull(await primary.GetPublishedAsync(scope, article.Portal, English, new HelpCenterSlug("changed")));

        await PublishAsync(primary, scope, edited, 3, 2);
        Assert.IsNull(await primary.GetPublishedAsync(scope, article.Portal, English, new HelpCenterSlug("reset-password")));
        Assert.IsNull(await primary.GetPublishedByIdAsync(scope, article.Id, German));
        Assert.AreEqual(2, (await primary.GetPublishedAsync(scope, article.Portal, English, new HelpCenterSlug("changed")))!.RevisionNumber);

        // Revisions are immutable and listed newest first.
        Assert.AreEqual(RevisionOf(article, 1), await competing.GetRevisionAsync(scope, article.Id, 1));
        Assert.AreEqual(RevisionOf(edited, 2), await competing.GetRevisionAsync(scope, article.Id, 2));
        Assert.IsNull(await competing.GetRevisionAsync(scope, article.Id, 3));
        var revisions = await competing.ListRevisionsAsync(scope, article.Id, 1);
        Assert.IsTrue(revisions.HasMore);
        Assert.AreEqual(2, revisions.Items.Single().Number);
        revisions = await competing.ListRevisionsAsync(scope, article.Id, 10, before: 2);
        Assert.IsFalse(revisions.HasMore);
        Assert.AreEqual(1, revisions.Items.Single().Number);

        // Pointing back at revision 1 (without appending) serves it again.
        Assert.AreEqual("applied:5", A(await primary.UpdateArticleAsync(scope, Published(edited, 1), 4)));
        Assert.AreEqual(1, (await primary.GetPublishedByIdAsync(scope, article.Id, English))!.RevisionNumber);

        // Published slugs are unique per (portal, locale) across articles; drafts may collide.
        var rival = SampleArticle("a2", slug: "reset-password");
        Assert.AreEqual("applied:1", A(await primary.CreateArticleAsync(scope, Draft(rival))));
        Assert.AreEqual("rejected:SlugConflict", A(await primary.UpdateArticleAsync(scope, Published(rival, 1), 1, RevisionOf(rival, 1))));
        Assert.IsNull(await primary.GetRevisionAsync(scope, rival.Id, 1));
        Assert.AreEqual(1, (await primary.GetArticleAsync(scope, rival.Id))!.Revision);
        var otherPortal = SampleArticle("a3", portal: "other", slug: "reset-password");
        Assert.AreEqual("applied:1", A(await primary.CreateArticleAsync(scope, Draft(otherPortal))));
        await PublishAsync(primary, scope, otherPortal, 1, 1);

        // Unpublish and archive remove read-model rows; the revision pointer remains.
        var unpublished = new ArticleRecord(edited, ArticleState.Draft, 1, Author, Now, Author, Now);
        Assert.AreEqual("applied:6", A(await primary.UpdateArticleAsync(scope, unpublished, 5)));
        Assert.IsNull(await primary.GetPublishedByIdAsync(scope, article.Id, English));
        Assert.AreEqual(1, (await primary.GetArticleAsync(scope, article.Id))!.Record.PublishedRevision);
        await PublishAsync(primary, scope, rival, 1, 1);
        Assert.AreEqual("rejected:SlugConflict", A(await primary.UpdateArticleAsync(scope, Published(edited, 1), 6)));

        // Listing by portal, collection and state with keyset paging.
        Assert.AreEqual("applied:1", A(await primary.CreateArticleAsync(scope, Draft(SampleArticle("a0", collection: "guides")))));
        var page = await competing.ListArticlesAsync(scope, new ArticleListQuery(new PortalId("portal"), limit: 2));
        CollectionAssert.AreEqual(new[] { "a0", "a1" }, page.Items.Select(item => item.Id.Value).ToArray());
        Assert.IsTrue(page.HasMore);
        page = await competing.ListArticlesAsync(scope, new ArticleListQuery(new PortalId("portal"), limit: 2, after: new ArticleId("a1")));
        CollectionAssert.AreEqual(new[] { "a2" }, page.Items.Select(item => item.Id.Value).ToArray());
        Assert.IsFalse(page.HasMore);
        page = await competing.ListArticlesAsync(scope, new ArticleListQuery(new PortalId("portal"), new CollectionId("guides")));
        CollectionAssert.AreEqual(new[] { "a0" }, page.Items.Select(item => item.Id.Value).ToArray());
        page = await competing.ListArticlesAsync(scope, new ArticleListQuery(new PortalId("portal"), state: ArticleState.Published));
        CollectionAssert.AreEqual(new[] { "a2" }, page.Items.Select(item => item.Id.Value).ToArray());

        // Concurrent lifecycle writes: exactly one wins.
        var outcomes = await Task.WhenAll(
            primary.UpdateArticleAsync(scope, Published(edited, 3), 6, RevisionOf(edited, 3)),
            competing.UpdateArticleAsync(scope, Published(SampleArticle("a1", slug: "racing"), 3), 6, RevisionOf(SampleArticle("a1", slug: "racing"), 3)));
        CollectionAssert.AreEquivalent(new[] { "applied:7", "conflict:7" }, outcomes.Select(A).ToArray());
        Assert.AreEqual(3, (await primary.ListRevisionsAsync(scope, article.Id, 100)).Items.Count);

        // Delete cascades revisions and read-model rows; scopes are isolated.
        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
        {
            Assert.IsNull(await primary.GetArticleAsync(other, article.Id));
            Assert.IsNull(await primary.GetRevisionAsync(other, article.Id, 1));
            Assert.IsNull(await primary.GetPublishedByIdAsync(other, article.Id, English));
            Assert.AreEqual("not-found", A(await primary.DeleteArticleAsync(other, article.Id, 7)));
        }
        Assert.AreEqual("conflict:7", A(await primary.DeleteArticleAsync(scope, article.Id, 6)));
        Assert.AreEqual("applied:7", A(await competing.DeleteArticleAsync(scope, article.Id, 7)));
        Assert.IsNull(await primary.GetArticleAsync(scope, article.Id));
        Assert.IsNull(await primary.GetRevisionAsync(scope, article.Id, 1));
        Assert.IsNull(await primary.GetPublishedByIdAsync(scope, article.Id, English));
        Assert.IsEmpty((await primary.ListRevisionsAsync(scope, article.Id, 10)).Items);
        Assert.AreEqual("applied:1", A(await primary.CreateArticleAsync(scope, Draft(article))));
        Assert.IsNull(await primary.GetRevisionAsync(scope, article.Id, 1));
    }

    public static async Task VerifyPublishedListingAsync(IHelpCenterStore store)
    {
        var scope = NewScope();
        Assert.AreEqual("applied:1", P(await store.CreatePortalAsync(scope, SamplePortal())));
        Assert.AreEqual("applied:1", C(await store.CreateCollectionAsync(scope, SampleCollection("guides"))));
        var positions = new[] { ("b", 1, "guides"), ("a", 1, (string?)null), ("c", 0, "guides"), ("d", 2, null) };
        foreach (var (id, position, collection) in positions)
        {
            var article = SampleArticle(id, collection: collection, position: position);
            Assert.AreEqual("applied:1", A(await store.CreateArticleAsync(scope, Draft(article))));
            await PublishAsync(store, scope, article, 1, 1);
        }
        Assert.AreEqual("applied:1", A(await store.CreateArticleAsync(scope, Draft(SampleArticle("draft")))));

        var first = await store.ListPublishedAsync(scope, new PublishedArticleQuery(new PortalId("portal"), English, limit: 2));
        CollectionAssert.AreEqual(new[] { "c", "a" }, first.Items.Select(item => item.Article.Value).ToArray());
        Assert.IsNotNull(first.Next);
        var second = await store.ListPublishedAsync(scope, new PublishedArticleQuery(new PortalId("portal"), English, limit: 2, after: first.Next));
        CollectionAssert.AreEqual(new[] { "b", "d" }, second.Items.Select(item => item.Article.Value).ToArray());
        Assert.IsNull(second.Next);
        var guides = await store.ListPublishedAsync(scope, new PublishedArticleQuery(new PortalId("portal"), German, new CollectionId("guides")));
        CollectionAssert.AreEqual(new[] { "c", "b" }, guides.Items.Select(item => item.Article.Value).ToArray());
        Assert.IsTrue(guides.Items.All(item => item.Content.Locale == German));
        Assert.IsEmpty((await store.ListPublishedAsync(scope, new PublishedArticleQuery(new PortalId("portal"), new HelpCenterLocale("fr")))).Items);
        Assert.IsEmpty((await store.ListPublishedAsync(TestScopes.LevelVariants(scope)[5], new PublishedArticleQuery(new PortalId("portal"), English))).Items);
    }

    public static async Task VerifySearchAsync(IHelpCenterStore store)
    {
        var scope = NewScope();
        Assert.AreEqual("applied:1", P(await store.CreatePortalAsync(scope, SamplePortal())));
        Assert.AreEqual("applied:1", P(await store.CreatePortalAsync(scope, SamplePortal("other", "other"))));
        var articles = new[]
        {
            SampleArticle("billing", title: "Billing and invoices", body: "Download invoices. If you forgot your password, reset it first."),
            SampleArticle("reset", title: "Reset your password", body: "Open **settings** and choose reset."),
            SampleArticle("shipping", title: "Shipping times", body: "Orders ship within two days."),
            SampleArticle("other-reset", portal: "other", title: "Reset your password", body: "Other portal."),
        };
        foreach (var article in articles)
        {
            Assert.AreEqual("applied:1", A(await store.CreateArticleAsync(scope, Draft(article))));
            await PublishAsync(store, scope, article, 1, 1);
        }
        var hidden = SampleArticle("hidden", title: "Password vault", body: "Never published.");
        Assert.AreEqual("applied:1", A(await store.CreateArticleAsync(scope, Draft(hidden))));

        var hits = await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "password"));
        CollectionAssert.AreEqual(new[] { "reset", "billing" }, hits.Select(hit => hit.Article.Value).ToArray());
        Assert.IsTrue(hits[0].Rank > hits[1].Rank);
        Assert.AreEqual("Reset your password", hits[0].Title);
        Assert.AreEqual(new HelpCenterSlug("a-reset"), hits[0].Slug);
        Assert.AreEqual(1, hits[0].RevisionNumber);
        Assert.IsTrue(hits.All(hit => hit.Snippet.Length <= ArticleSnippet.MaximumSnippetLength && !hit.Snippet.Contains('<') && !hit.Snippet.Contains("**", StringComparison.Ordinal)));
        Assert.Contains("password", hits[1].Snippet);

        Assert.HasCount(1, await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "password", limit: 1)));
        CollectionAssert.AreEqual(new[] { "billing" }, (await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "password", offset: 1))).Select(hit => hit.Article.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "shipping" }, (await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "shipping"))).Select(hit => hit.Article.Value).ToArray());
        CollectionAssert.AreEqual(new[] { "reset" }, (await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), German, "Inhalt reset"))).Select(hit => hit.Article.Value).ToArray());
        Assert.IsEmpty(await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "vault")));
        Assert.IsEmpty(await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "zebra")));
        Assert.IsEmpty(await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "' OR 1=1 --")));
        Assert.IsEmpty(await store.SearchAsync(TestScopes.LevelVariants(scope)[1], new HelpCenterSearchQuery(new PortalId("portal"), English, "password")));
        CollectionAssert.AreEqual(new[] { "other-reset" }, (await store.SearchAsync(scope, new HelpCenterSearchQuery(new PortalId("other"), English, "password"))).Select(hit => hit.Article.Value).ToArray());
    }

    public static async Task VerifyFeedbackAsync(IHelpCenterStore store, IArticleFeedbackStore primary, IArticleFeedbackStore competing)
    {
        var scope = NewScope();
        var article = new ArticleId("a1");
        var visitor = VisitorHash.Create(scope, "visitor-1");
        var other = VisitorHash.Create(scope, "visitor-2");
        var day = new DateOnly(2026, 3, 1);
        Assert.IsNull(await primary.GetCountsAsync(scope, article));
        Assert.IsInstanceOfType<FeedbackArticleNotFound>((await primary.RecordViewAsync(scope, article, visitor, day)).Value);
        Assert.IsInstanceOfType<FeedbackArticleNotFound>((await primary.RecordVoteAsync(scope, article, visitor, true)).Value);
        Assert.AreEqual("applied:1", P(await store.CreatePortalAsync(scope, SamplePortal())));
        Assert.AreEqual("applied:1", A(await store.CreateArticleAsync(scope, Draft(SampleArticle("a1")))));
        Assert.AreEqual(ArticleFeedbackCounts.Empty, await primary.GetCountsAsync(scope, article));

        Assert.AreEqual("counted:1/0/0", F(await primary.RecordViewAsync(scope, article, visitor, day)));
        Assert.AreEqual("unchanged:1/0/0", F(await competing.RecordViewAsync(scope, article, visitor, day)));
        Assert.AreEqual("counted:2/0/0", F(await competing.RecordViewAsync(scope, article, visitor, day.AddDays(1))));
        Assert.AreEqual("counted:3/0/0", F(await competing.RecordViewAsync(scope, article, other, day)));
        Assert.AreEqual("counted:3/1/0", F(await primary.RecordVoteAsync(scope, article, visitor, true)));
        Assert.AreEqual("unchanged:3/1/0", F(await competing.RecordVoteAsync(scope, article, visitor, true)));
        Assert.AreEqual("counted:3/0/1", F(await competing.RecordVoteAsync(scope, article, visitor, false)));
        Assert.AreEqual("counted:3/0/2", F(await primary.RecordVoteAsync(scope, article, other, false)));

        var concurrent = VisitorHash.Create(scope, "visitor-3");
        var outcomes = await Task.WhenAll(primary.RecordVoteAsync(scope, article, concurrent, true), competing.RecordVoteAsync(scope, article, concurrent, true));
        CollectionAssert.AreEquivalent(new[] { "counted:3/1/2", "unchanged:3/1/2" }, outcomes.Select(F).ToArray());
        Assert.AreEqual(new ArticleFeedbackCounts(3, 1, 2), await competing.GetCountsAsync(scope, article));
        Assert.IsNull(await competing.GetCountsAsync(TestScopes.LevelVariants(scope)[1], article));

        Assert.AreEqual(2, await primary.PurgeViewMarkersAsync(scope, day.AddDays(1), 100));
        Assert.AreEqual(0, await primary.PurgeViewMarkersAsync(scope, day.AddDays(1), 100));
        Assert.AreEqual("unchanged:3/1/2", F(await primary.RecordViewAsync(scope, article, visitor, day.AddDays(1))));
        Assert.AreEqual(new ArticleFeedbackCounts(3, 1, 2), await competing.GetCountsAsync(scope, article));

        Assert.AreEqual("applied:1", A(await store.DeleteArticleAsync(scope, article, 1)));
        Assert.IsNull(await primary.GetCountsAsync(scope, article));
        Assert.AreEqual("applied:1", A(await store.CreateArticleAsync(scope, Draft(SampleArticle("a1")))));
        Assert.AreEqual(ArticleFeedbackCounts.Empty, await primary.GetCountsAsync(scope, article));
        Assert.AreEqual("counted:0/1/0", F(await primary.RecordVoteAsync(scope, article, visitor, true)));
    }

    private static string F(ArticleFeedbackResult result) => result switch
    {
        FeedbackCounted counted => $"counted:{counted.Counts.Views}/{counted.Counts.Helpful}/{counted.Counts.NotHelpful}",
        FeedbackUnchanged unchanged => $"unchanged:{unchanged.Counts.Views}/{unchanged.Counts.Helpful}/{unchanged.Counts.NotHelpful}",
        FeedbackArticleNotFound => "not-found",
    };
}
