using TicketFlow.Surveys.StoreContracts;

namespace TicketFlow.Surveys.Tests;

[TestClass]
public sealed class InMemorySurveyStoreTests
{
    [TestMethod]
    public async Task SurveysSatisfyTheContract()
    {
        var store = new InMemorySurveyStore();
        await SurveyStoreContract.VerifySurveysAsync(store, store);
        await SurveyStoreContract.VerifySurveyCapacityAsync(store);
    }

    [TestMethod]
    public async Task InvitationsSatisfyTheContract()
    {
        var store = new InMemorySurveyStore();
        await SurveyStoreContract.VerifyInvitationsAsync(store, store);
        await SurveyStoreContract.VerifyClaimsAsync(store, store);
    }

    [TestMethod]
    public async Task ResponsesSatisfyTheContract()
    {
        var store = new InMemorySurveyStore();
        await SurveyStoreContract.VerifyResponsesAsync(store, store);
    }

    [TestMethod]
    public async Task ListsAndAggregatesSatisfyTheContract()
    {
        var store = new InMemorySurveyStore();
        await SurveyStoreContract.VerifyListsAndAggregatesAsync(store);
        await SurveyStoreContract.VerifyAggregateTruncationAsync(store);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(42)]
    [DataRow(2026)]
    public Task ResponseListsEqualPointChecks(int seed) => AccessFilteredResponseParity.VerifyAsync(new InMemorySurveyStore(), seed);

    [TestMethod]
    public async Task StoreIsBoundedGlobally()
    {
        var store = new InMemorySurveyStore(maximumRecords: 2);
        var scope = SurveyStoreContract.NewScope();
        Assert.AreEqual("applied:1", SurveyStoreContract.S(await store.CreateSurveyAsync(scope, SurveyStoreContract.SampleSurvey())));
        Assert.AreEqual("created", SurveyStoreContract.C(await store.CreateInvitationAsync(scope, SurveyStoreContract.SampleInvitation(), TimeSpan.Zero)));
        Assert.AreEqual("rejected:CapacityExceeded", SurveyStoreContract.C(await store.CreateInvitationAsync(scope, SurveyStoreContract.SampleInvitation(conversation: "c2"), TimeSpan.Zero)));
        Assert.AreEqual("rejected:CapacityExceeded", SurveyStoreContract.S(await store.CreateSurveyAsync(scope, SurveyStoreContract.SampleSurvey("other"))));
    }
}
