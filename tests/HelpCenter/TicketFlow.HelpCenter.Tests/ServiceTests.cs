using TicketFlow.Access;
using TicketFlow.Core;
using static TicketFlow.HelpCenter.StoreContracts.HelpCenterStoreContract;
using static TicketFlow.HelpCenter.Tests.HelpCenterFixture;

namespace TicketFlow.HelpCenter.Tests;

[TestClass]
public sealed class ServiceTests
{
    [TestMethod]
    public async Task EveryOperationRequiresItsPermission()
    {
        using var fixture = Create();
        var editor = await fixture.EditorAsync();
        var reader = await fixture.UserWithAsync("reader", HelpCenterPermissions.Read);
        var writer = await fixture.UserWithAsync("writer", HelpCenterPermissions.Read, HelpCenterPermissions.Write);
        var stranger = fixture.User("stranger");
        var admin = fixture.User("admin");
        await fixture.Grants.CreateAsync(new AccessGrant(new GrantId("admin-grant"), AccessPartition.From(fixture.Scope), admin.UserId, BuiltInRoles.Admin.Id, new WorkspaceGrantScope(fixture.Scope.WorkspaceId)));

        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.CreatePortalAsync(writer, SamplePortal())));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.CreatePortalAsync(admin, SamplePortal())), "Admin holds built-in permissions only.");
        Value(await fixture.Service.CreatePortalAsync(editor, SamplePortal()));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.GetPortalAsync(stranger, new PortalId("portal"))));
        Assert.IsEmpty(Value(await fixture.Service.ListPortalsAsync(stranger)));
        Assert.HasCount(1, Value(await fixture.Service.ListPortalsAsync(reader)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.CreateCollectionAsync(reader, SampleCollection("c1"))));
        Value(await fixture.Service.CreateCollectionAsync(writer, SampleCollection("c1")));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.CreateArticleAsync(reader, SampleArticle("a1"))));
        var created = Value(await fixture.Service.CreateArticleAsync(writer, SampleArticle("a1")));
        Assert.AreEqual(new AccessActor(AccessActorKind.User, "writer"), created.Record.CreatedBy);
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.PublishArticleAsync(writer, created.Id, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.DeleteArticleAsync(writer, created.Id, 1)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.GetArticleAsync(stranger, created.Id)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.PreviewArticleAsync(stranger, created.Id, English)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Service.GetFeedbackAsync(stranger, created.Id)));
        Assert.AreEqual("refused:Forbidden", Describe(await fixture.Search.SearchAsync(stranger, new HelpCenterSearchQuery(new PortalId("portal"), English, "x"))));
        Assert.IsEmpty(Value(await fixture.Service.ListArticlesAsync(stranger, new ArticleListQuery(new PortalId("portal")))).Items);
        Assert.HasCount(1, Value(await fixture.Service.ListArticlesAsync(reader, new ArticleListQuery(new PortalId("portal")))).Items);
        Assert.AreEqual("ok", Describe(await fixture.Service.PublishArticleAsync(editor, created.Id, 1)));
    }

    [TestMethod]
    public async Task ArticleLifecycleKeepsImmutableRevisions()
    {
        using var fixture = Create();
        var editor = await fixture.EditorAsync();
        Value(await fixture.Service.CreatePortalAsync(editor, SamplePortal()));
        var draft = SampleArticle("a1", title: "Reset your password", body: "Open **settings**.", slug: "reset");
        var stored = Value(await fixture.Service.CreateArticleAsync(editor, draft));
        Assert.AreEqual(ArticleState.Draft, stored.Record.State);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), stored.Record.CreatedAt);

        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var published = Value(await fixture.Service.PublishArticleAsync(editor, draft.Id, 1));
        Assert.AreEqual(ArticleState.Published, published.Record.State);
        Assert.AreEqual(1, published.Record.PublishedRevision);
        var revision = Value(await fixture.Service.GetRevisionAsync(editor, draft.Id, 1));
        Assert.AreEqual("Open settings.", revision.Translation(English)!.PlainText);
        Assert.AreEqual(fixture.Clock.GetUtcNow(), revision.PublishedAt);

        var edited = new Article(draft.Id, draft.Portal, [Translation(English, "Reset passwords", "reset", "New **body**.")]);
        var updated = Value(await fixture.Service.UpdateArticleAsync(editor, edited, 2));
        Assert.AreEqual(ArticleState.Published, updated.Record.State);
        Assert.AreEqual("Reset your password", (await fixture.Store.GetPublishedByIdAsync(fixture.Scope, draft.Id, English))!.Content.Title);
        Assert.AreEqual("refused:Conflict:3", Describe(await fixture.Service.PublishArticleAsync(editor, draft.Id, 2)));
        Value(await fixture.Service.PublishArticleAsync(editor, draft.Id, 3));
        Assert.AreEqual("Reset passwords", (await fixture.Store.GetPublishedByIdAsync(fixture.Scope, draft.Id, English))!.Content.Title);
        Assert.IsNull(await fixture.Store.GetPublishedByIdAsync(fixture.Scope, draft.Id, German), "Revision 2 has no German translation.");

        var rolledBack = Value(await fixture.Service.RollbackArticleAsync(editor, draft.Id, 1, 4));
        Assert.AreEqual(3, rolledBack.Record.PublishedRevision);
        Assert.AreEqual(draft, rolledBack.Record.Article);
        Assert.AreEqual("Reset your password", (await fixture.Store.GetPublishedByIdAsync(fixture.Scope, draft.Id, English))!.Content.Title);
        Assert.AreSequenceEqual(Value(await fixture.Service.GetRevisionAsync(editor, draft.Id, 1)).Translations, Value(await fixture.Service.GetRevisionAsync(editor, draft.Id, 3)).Translations);
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, Value(await fixture.Service.ListRevisionsAsync(editor, draft.Id)).Items.Select(item => item.Number).ToArray());
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Service.RollbackArticleAsync(editor, draft.Id, 9, 5)));

        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.RestoreArticleAsync(editor, draft.Id, 5)));
        var unpublished = Value(await fixture.Service.UnpublishArticleAsync(editor, draft.Id, 5));
        Assert.AreEqual(ArticleState.Draft, unpublished.Record.State);
        Assert.AreEqual(3, unpublished.Record.PublishedRevision);
        Assert.IsNull(await fixture.Store.GetPublishedByIdAsync(fixture.Scope, draft.Id, English));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.UnpublishArticleAsync(editor, draft.Id, 6)));
        Value(await fixture.Service.ArchiveArticleAsync(editor, draft.Id, 6));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.PublishArticleAsync(editor, draft.Id, 7)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.RollbackArticleAsync(editor, draft.Id, 1, 7)));
        Assert.AreEqual(ArticleState.Draft, Value(await fixture.Service.RestoreArticleAsync(editor, draft.Id, 7)).Record.State);
        Value(await fixture.Service.DeleteArticleAsync(editor, draft.Id, 8));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Service.GetArticleAsync(editor, draft.Id)));
    }

    [TestMethod]
    public async Task ValidatesLocalesPortalsAndDefaultTranslation()
    {
        using var fixture = Create();
        var editor = await fixture.EditorAsync();
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.CreateArticleAsync(editor, SampleArticle("a1"))), "Missing portal.");
        Value(await fixture.Service.CreatePortalAsync(editor, new Portal(new PortalId("portal"), new HelpCenterSlug("help"), "Help", English)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.CreateArticleAsync(editor, SampleArticle("a1"))), "German is unsupported.");
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.CreateCollectionAsync(editor,
            new HelpCollection(new CollectionId("c"), new PortalId("portal"), [new CollectionTranslation(German, "Start", new HelpCenterSlug("start"))]))));
        var portal = Value(await fixture.Service.UpdatePortalAsync(editor, new Portal(new PortalId("portal"), new HelpCenterSlug("help"), "Help", English, [German]), 1));
        var germanOnly = new Article(new ArticleId("a1"), new PortalId("portal"), [Translation(German, "Nur Deutsch", "nur", "Inhalt")]);
        Value(await fixture.Service.CreateArticleAsync(editor, germanOnly));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.PublishArticleAsync(editor, germanOnly.Id, 1)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.UpdateArticleAsync(editor, SampleArticle("a1", portal: "elsewhere"), 1)));
        Assert.AreEqual("refused:Conflict:1", Describe(await fixture.Service.UpdateArticleAsync(editor, SampleArticle("a1"), 9)));
        Assert.AreEqual("refused:Invalid", Describe(await fixture.Service.UpdateArticleAsync(editor, SampleArticle("a1"), 0)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Service.UpdateArticleAsync(editor, SampleArticle("missing"), 1)));
        Assert.AreEqual("refused:Rejected", Describe(await fixture.Service.CreatePortalAsync(editor, SamplePortal("second", "help"))));
        Assert.AreEqual("refused:Rejected", Describe(await fixture.Service.DeletePortalAsync(editor, portal.Portal.Id, portal.Revision)));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Service.PreviewArticleAsync(editor, germanOnly.Id, English)));
        var preview = Value(await fixture.Service.PreviewArticleAsync(editor, germanOnly.Id, German));
        Assert.AreEqual("<p>Inhalt</p>\n", preview.Html);
    }

    [TestMethod]
    public async Task AgentSearchCoversPrivatePortals()
    {
        using var fixture = Create();
        var editor = await fixture.EditorAsync();
        Value(await fixture.Service.CreatePortalAsync(editor, SamplePortal(visibility: PortalVisibility.Private)));
        Value(await fixture.Service.CreateArticleAsync(editor, SampleArticle("a1", title: "Reset your password")));
        Value(await fixture.Service.PublishArticleAsync(editor, new ArticleId("a1"), 1));
        var hits = Value(await fixture.Search.SearchAsync(editor, new HelpCenterSearchQuery(new PortalId("portal"), English, "password")));
        Assert.AreEqual("a1", hits.Single().Article.Value);
        Assert.IsEmpty(await fixture.Search.SearchPublicAsync(fixture.Scope, new HelpCenterSearchQuery(new PortalId("portal"), English, "password")));
        Assert.AreEqual("refused:NotFound", Describe(await fixture.Search.SearchAsync(editor, new HelpCenterSearchQuery(new PortalId("missing"), English, "password"))));
    }
}
