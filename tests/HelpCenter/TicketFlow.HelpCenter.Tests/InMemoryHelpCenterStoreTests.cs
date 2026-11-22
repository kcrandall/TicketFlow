using TicketFlow.HelpCenter.StoreContracts;

namespace TicketFlow.HelpCenter.Tests;

[TestClass]
public sealed class InMemoryHelpCenterStoreTests
{
    [TestMethod]
    public async Task PortalsSatisfyTheContract()
    {
        var store = new InMemoryHelpCenterStore();
        await HelpCenterStoreContract.VerifyPortalsAsync(store, store);
        await HelpCenterStoreContract.VerifyPortalCapacityAsync(store);
    }

    [TestMethod]
    public async Task CollectionsSatisfyTheContract()
    {
        var store = new InMemoryHelpCenterStore();
        await HelpCenterStoreContract.VerifyCollectionsAsync(store, store);
    }

    [TestMethod]
    public async Task ArticlesSatisfyTheContract()
    {
        var store = new InMemoryHelpCenterStore();
        await HelpCenterStoreContract.VerifyArticlesAsync(store, store);
        await HelpCenterStoreContract.VerifyPublishedListingAsync(store);
    }

    [TestMethod]
    public async Task SearchSatisfiesTheContract() => await HelpCenterStoreContract.VerifySearchAsync(new InMemoryHelpCenterStore());

    [TestMethod]
    public async Task FeedbackSatisfiesTheContract()
    {
        var store = new InMemoryHelpCenterStore();
        await HelpCenterStoreContract.VerifyFeedbackAsync(store, store, store);
    }

    [TestMethod]
    public async Task StoreIsBoundedGlobally()
    {
        var store = new InMemoryHelpCenterStore(maximumRecords: 2, maximumFeedbackMarkers: 1);
        var scope = HelpCenterStoreContract.NewScope();
        Assert.AreEqual("applied:1", HelpCenterStoreContract.P(await store.CreatePortalAsync(scope, HelpCenterStoreContract.SamplePortal())));
        var article = HelpCenterStoreContract.SampleArticle("a1");
        Assert.AreEqual("applied:1", HelpCenterStoreContract.A(await store.CreateArticleAsync(scope, HelpCenterStoreContract.Draft(article))));
        Assert.AreEqual("rejected:CapacityExceeded", HelpCenterStoreContract.A(await store.CreateArticleAsync(scope, HelpCenterStoreContract.Draft(HelpCenterStoreContract.SampleArticle("a2")))));
        Assert.AreEqual("rejected:CapacityExceeded", HelpCenterStoreContract.A(await store.UpdateArticleAsync(scope, HelpCenterStoreContract.Published(article, 1), 1, HelpCenterStoreContract.RevisionOf(article, 1))));
        var day = new DateOnly(2026, 1, 1);
        Assert.IsInstanceOfType<FeedbackCounted>((await store.RecordViewAsync(scope, article.Id, VisitorHash.Create(scope, "visitor-1"), day)).Value);
        Assert.IsInstanceOfType<FeedbackUnchanged>((await store.RecordViewAsync(scope, article.Id, VisitorHash.Create(scope, "visitor-2"), day)).Value);
        Assert.AreEqual("applied:1", HelpCenterStoreContract.A(await store.DeleteArticleAsync(scope, article.Id, 1)));
        Assert.AreEqual("applied:1", HelpCenterStoreContract.A(await store.CreateArticleAsync(scope, HelpCenterStoreContract.Draft(HelpCenterStoreContract.SampleArticle("a2")))));
    }
}
