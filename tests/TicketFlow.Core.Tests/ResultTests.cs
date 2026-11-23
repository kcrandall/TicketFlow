using TicketFlow.Core;

namespace TicketFlow.Core.Tests;

[TestClass]
public sealed class ResultTests
{
    private enum SampleRejection
    {
        CapacityExceeded = 1,
        Duplicate = 2,
    }

    private sealed record DomainFailure(string Code);

    [TestMethod]
    public void TicketResultMatchesExhaustivelyWithoutAllocatingOnSuccess()
    {
        TicketResult<int> ok = new TicketValue<int>(42);
        TicketResult<int> failed = TicketFailure.NotFound;

        Assert.IsTrue(ok.HasValue);
        Assert.IsTrue(ok.IsSuccess);
        Assert.IsTrue(failed.HasValue);
        Assert.IsFalse(failed.IsSuccess);
        Assert.IsFalse(default(TicketResult<int>).HasValue);

        Assert.AreEqual("ok:42", Describe(ok));
        Assert.AreEqual("fail:NotFound", Describe(failed));
        Assert.AreEqual(TicketResult.Ok(42), ok);
        Assert.AreNotEqual(ok, failed);
        Assert.AreEqual(TicketResult.Ok(42).GetHashCode(), ok.GetHashCode());

        // Warm up JIT then confirm pattern matching via TryGetValue allocates 0 bytes on success.
        _ = Describe(ok);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 1000; i++)
        {
            TicketResult<int> r = new TicketValue<int>(i);
            sum += r switch
            {
                TicketValue<int> v => v.Value,
                TicketFailure => -1,
            };
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(499_500, sum);
        Assert.AreEqual(0L, allocated);

        static string Describe(TicketResult<int> result) => result switch
        {
            TicketValue<int> v => $"ok:{v.Value}",
            TicketFailure f => $"fail:{f.Kind}",
        };
    }

    [TestMethod]
    public void CustomFailureTicketResultMatchesExhaustivelyWithoutAllocatingOnSuccess()
    {
        TicketResult<string, DomainFailure> ok = TicketResult.Ok<string, DomainFailure>("hello");
        TicketResult<string, DomainFailure> fail = new DomainFailure("busy");

        Assert.IsTrue(ok.IsSuccess);
        Assert.IsFalse(fail.IsSuccess);
        Assert.AreEqual("hello", ok switch { TicketValue<string> v => v.Value, DomainFailure f => f.Code });
        Assert.AreEqual("busy", fail switch { TicketValue<string> v => v.Value, DomainFailure f => f.Code });
        Assert.AreEqual(ok, new TicketResult<string, DomainFailure>(new TicketValue<string>("hello")));
        Assert.AreEqual(fail, new TicketResult<string, DomainFailure>(new DomainFailure("busy")));
    }

    [TestMethod]
    public void TicketResultMapBindAndMatchPropagateAndAvoidAllocations()
    {
        var ok = TicketResult.Ok(10)
            .Map(static x => x * 2)
            .Bind(static x => x > 15 ? TicketResult.Ok(x + 1) : TicketFailure.Invalid("too small"));
        Assert.AreEqual(21, ok.Match(static x => x, static _ => -1));

        var failedEarly = ((TicketResult<int>)TicketFailure.Forbidden)
            .Map<int>(_ => throw new AssertFailedException("Map must not run on failure."))
            .Bind<int>(_ => throw new AssertFailedException("Bind must not run on failure."));
        Assert.AreEqual(TicketFailureKind.Forbidden, failedEarly.Match(static _ => TicketFailureKind.Invalid, static f => f.Kind));

        var failedBind = TicketResult.Ok(5)
            .Map(static x => x * 2)
            .Bind(static x => x > 15 ? TicketResult.Ok(x + 1) : TicketFailure.Invalid("too small"));
        Assert.AreEqual("too small", failedBind.Match(static _ => string.Empty, static f => f.Errors[0]));

        Assert.ThrowsExactly<InvalidOperationException>(() => default(TicketResult<int>).Map(static x => x));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(TicketResult<int>).Bind(static x => TicketResult.Ok(x)));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(TicketResult<int>).Match(static x => x, static _ => 0));

        // Custom failure variant
        var customOk = TicketResult.Ok<int, DomainFailure>(3)
            .Map(static x => x + 4)
            .Bind(static x => TicketResult.Ok<int, DomainFailure>(x * 10));
        Assert.AreEqual(70, customOk.Match(static x => x, static _ => -1));
        var customFail = ((TicketResult<int, DomainFailure>)new DomainFailure("busy"))
            .Map(static x => x + 1)
            .Bind(static x => TicketResult.Ok<int, DomainFailure>(x));
        Assert.AreEqual("busy", customFail.Match(static _ => string.Empty, static f => f.Code));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(TicketResult<int, DomainFailure>).Match(static x => x, static _ => 0));

        // Warm up JIT then verify 0 bytes allocated across Map -> Bind -> Match with static lambdas.
        _ = RunPipeline(1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 1000; i++)
            sum += RunPipeline(i);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(1_000_000, sum);
        Assert.AreEqual(0L, allocated);

        static int RunPipeline(int value) => TicketResult.Ok(value)
            .Map(static x => x * 2)
            .Bind(static x => TicketResult.Ok(x + 1))
            .Match(static x => x, static _ => -1);
    }

    [TestMethod]
    public void StoreWriteResultMatchesAllFourCasesWithZeroAllocations()
    {
        StoreWriteResult<int, SampleRejection> applied = StoreWriteResult.Applied(7);
        StoreWriteResult<int, SampleRejection> notFound = StoreWriteResult.NotFound;
        StoreWriteResult<int, SampleRejection> conflict = StoreWriteResult.Conflict(3);
        StoreWriteResult<int, SampleRejection> conflictNull = StoreWriteResult.Conflict(null);
        StoreWriteResult<int, SampleRejection> rejected = StoreWriteResult.Rejected(SampleRejection.Duplicate);

        Assert.AreEqual("applied:7", Describe(applied));
        Assert.AreEqual("not-found", Describe(notFound));
        Assert.AreEqual("conflict:3", Describe(conflict));
        Assert.AreEqual("conflict:", Describe(conflictNull));
        Assert.AreEqual("rejected:Duplicate", Describe(rejected));

        Assert.AreEqual(TicketResult.Ok(7), applied.ToTicketResult(r => r.ToString()));
        Assert.AreEqual(TicketFailure.NotFound, ((TicketFailure)notFound.ToTicketResult(r => r.ToString()).Value!));
        Assert.AreEqual(3L, ((TicketFailure)conflict.ToTicketResult(r => r.ToString()).Value!).CurrentRevision);
        Assert.IsNull(((TicketFailure)conflictNull.ToTicketResult(r => r.ToString()).Value!).CurrentRevision);
        Assert.AreEqual(TicketFailureKind.Rejected, ((TicketFailure)rejected.ToTicketResult(r => r.ToString()).Value!).Kind);

        // Warm up JIT then confirm zero heap allocation across all 4 cases.
        _ = Count(applied) + Count(notFound) + Count(conflict) + Count(rejected);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0L;
        for (var i = 0; i < 1000; i++)
        {
            total += Count(new StoreApplied<int>(i));
            total += Count(StoreNotFound.Instance);
            total += Count(new StoreConflict(i));
            total += Count(new StoreRejected<SampleRejection>(SampleRejection.CapacityExceeded));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsGreaterThan(0L, total);
        Assert.AreEqual(0L, allocated);

        static string Describe(StoreWriteResult<int, SampleRejection> result) => result switch
        {
            StoreApplied<int> a => $"applied:{a.Value}",
            StoreNotFound => "not-found",
            StoreConflict c => $"conflict:{c.CurrentRevision}",
            StoreRejected<SampleRejection> r => $"rejected:{r.Reason}",
        };

        static long Count(StoreWriteResult<int, SampleRejection> result) => result switch
        {
            StoreApplied<int> a => a.Value,
            StoreNotFound => 1,
            StoreConflict c => c.CurrentRevision ?? 0,
            StoreRejected<SampleRejection> r => (int)r.Reason,
        };
    }
}
