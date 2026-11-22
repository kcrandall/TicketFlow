using Microsoft.VisualStudio.TestTools.UnitTesting;
using TicketFlow.Testing;

namespace TicketFlow.Hosting.Worker.StoreContracts;

/// <summary>The <see cref="IWorkerCheckpointStore"/> contract: scope-confined compare-and-set positions.</summary>
public static class WorkerCheckpointStoreContract
{
    public static async Task VerifyAsync(IWorkerCheckpointStore primary, IWorkerCheckpointStore competing)
    {
        var scope = TestScopes.Create(workspaceId: "checkpoint-" + Guid.NewGuid().ToString("N"));
        Assert.IsNull(await primary.GetAsync(scope, "contact-merges"));
        Assert.IsFalse(await primary.SetAsync(scope, "contact-merges", 5, expected: 1), "An absent checkpoint does not match an expected value.");
        Assert.IsTrue(await primary.SetAsync(scope, "contact-merges", 5, expected: null));
        Assert.IsFalse(await competing.SetAsync(scope, "contact-merges", 7, expected: null), "Creating twice loses the race.");
        Assert.AreEqual(5, await competing.GetAsync(scope, "contact-merges"));
        Assert.IsTrue(await competing.SetAsync(scope, "contact-merges", 9, expected: 5));
        Assert.IsFalse(await primary.SetAsync(scope, "contact-merges", 11, expected: 5), "A stale expected value loses.");
        Assert.AreEqual(9, await primary.GetAsync(scope, "contact-merges"));

        var outcomes = await Task.WhenAll(primary.SetAsync(scope, "contact-merges", 20, 9), competing.SetAsync(scope, "contact-merges", 21, 9));
        Assert.AreEqual(1, outcomes.Count(outcome => outcome), "Exactly one concurrent compare-and-set wins.");

        foreach (var other in TestScopes.LevelVariants(scope).Skip(1))
            Assert.IsNull(await primary.GetAsync(other, "contact-merges"), "Checkpoints never cross a scope level.");
        Assert.IsNull(await primary.GetAsync(scope, "other"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => primary.GetAsync(scope, "Not Valid"));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => primary.SetAsync(scope, "x", -1, null));
    }
}
