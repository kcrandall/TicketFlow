using TicketFlow.Core;
using TicketFlow.Testing;

namespace TicketFlow.Stores.InMemory.Tests;

[TestClass]
public sealed class ScopedRevisionStoreTests
{
    private static string Describe(RevisionWriteResult<string, string> result) => result switch
    {
        RevisionApplied<string, string> applied => $"applied:{applied.Entry.Revision}:{applied.Entry.Value}",
        RevisionNotFound => "not-found",
        RevisionConflict conflict => $"conflict:{conflict.CurrentRevision}",
    };

    [TestMethod]
    public void WritesAreRevisionFenced()
    {
        var store = new ScopedRevisionStore<string, string>();
        var scope = TestScopes.Create();
        Assert.AreEqual("applied:1:a", Describe(store.Add(scope, "k", "a")));
        Assert.AreEqual("conflict:1", Describe(store.Add(scope, "k", "again")));
        Assert.AreEqual("applied:2:b", Describe(store.Replace(scope, "k", 1, "b")));
        Assert.AreEqual("conflict:2", Describe(store.Replace(scope, "k", 1, "stale")));
        Assert.AreEqual("b", store.Find(scope, "k")!.Value);
        Assert.AreEqual("conflict:2", Describe(store.Remove(scope, "k", 1)));
        Assert.AreEqual("applied:2:b", Describe(store.Remove(scope, "k", 2)));
        Assert.IsNull(store.Find(scope, "k"));
        Assert.AreEqual("not-found", Describe(store.Replace(scope, "k", 2, "c")));
        Assert.AreEqual("not-found", Describe(store.Remove(scope, "k", 2)));
        Assert.AreEqual("applied:1:d", Describe(store.Add(scope, "k", "d")));
    }

    [TestMethod]
    public void EntriesNeverCrossAnyScopeLevel()
    {
        var store = new ScopedRevisionStore<string, string>();
        var variants = TestScopes.LevelVariants(TestScopes.Create());
        Assert.AreEqual("applied:1:base", Describe(store.Add(variants[0], "k", "base")));
        foreach (var other in variants.Skip(1))
        {
            Assert.IsNull(store.Find(other, "k"));
            Assert.AreEqual("not-found", Describe(store.Replace(other, "k", 1, "x")));
            Assert.AreEqual("not-found", Describe(store.Remove(other, "k", 1)));
            Assert.AreEqual("applied:1:other", Describe(store.Add(other, "k", "other")));
        }
        Assert.AreEqual("base", store.Find(variants[0], "k")!.Value);
        Assert.AreEqual(variants.Count, store.Count);
        Assert.AreEqual(variants[3], store.Find(variants[3], "k")!.Scope);
    }

    [TestMethod]
    public void CapacityIsBoundedAndFreedByRemoval()
    {
        var store = new ScopedRevisionStore<string, string>(maximumEntries: 1);
        var scope = TestScopes.Create();
        store.Add(scope, "a", "a");
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Add(scope, "b", "b"));
        Assert.AreEqual("conflict:1", Describe(store.Add(scope, "a", "dup")));
        store.Remove(scope, "a", 1);
        Assert.AreEqual("applied:1:b", Describe(store.Add(scope, "b", "b")));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ScopedRevisionStore<string, string>(0));
    }

    [TestMethod]
    public void KeyComparerAndArgumentsAreValidated()
    {
        var store = new ScopedRevisionStore<string, string>(keyComparer: StringComparer.OrdinalIgnoreCase);
        var scope = TestScopes.Create();
        store.Add(scope, "Key", "v");
        Assert.AreEqual("v", store.Find(scope, "KEY")!.Value);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.Replace(scope, "Key", 0, "x"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.Remove(scope, "Key", 0));
        Assert.ThrowsExactly<ArgumentNullException>(() => store.Find(null!, "Key"));
        Assert.ThrowsExactly<ArgumentNullException>(() => store.Add(scope, null!, "x"));
    }

    [TestMethod]
    public async Task ConcurrentReplacementsWithTheSameRevisionHaveOneWinner()
    {
        var store = new ScopedRevisionStore<ConversationId, int>();
        var scope = TestScopes.Create();
        var key = new ConversationId("c");
        store.Add(scope, key, 0);
        var results = await Task.WhenAll(Enumerable.Range(1, 32).Select(value => Task.Run(() => store.Replace(scope, key, 1, value))));
        Assert.AreEqual(1, results.Count(result => result.Value is RevisionApplied<ConversationId, int>));
        Assert.AreEqual(31, results.Count(result => result.Value is RevisionConflict { CurrentRevision: 2 }));
        Assert.AreEqual(2, store.Find(scope, key)!.Revision);
    }
}
