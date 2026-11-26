using System.Text;

namespace TicketFlow.Channels.Api.Tests;

[TestClass]
public sealed class ApiWebhookSignatureTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"v":1,"body":"hello"}""");
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static (string Timestamp, string Signature) Sign(DateTimeOffset at, string id = "tf1-abc", byte[]? body = null)
    {
        var timestamp = at.ToUnixTimeSeconds();
        return (timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), ApiWebhookSignature.Create(Key, id, timestamp, body ?? Body));
    }

    [TestMethod]
    public void ValidSignaturesVerify()
    {
        var (timestamp, signature) = Sign(Now);
        Assert.IsTrue(signature.StartsWith("v1=", StringComparison.Ordinal));
        Assert.AreEqual(67, signature.Length);
        var verified = Is<ApiWebhookVerified>(ApiWebhookSignature.Verify(Key, "tf1-abc", timestamp, signature, Body, Now.AddSeconds(30)).Value);
        Assert.AreEqual("tf1-abc", verified.DeliveryId);
        Assert.AreEqual(Now, verified.SignedAt);
    }

    [TestMethod]
    public void TamperingIsDetected()
    {
        var (timestamp, signature) = Sign(Now);
        var tampered = (byte[])Body.Clone();
        tampered[^3] ^= 1;
        Assert.AreEqual(ApiWebhookRejection.SignatureMismatch, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", timestamp, signature, tampered, Now)));
        Assert.AreEqual(ApiWebhookRejection.SignatureMismatch, Reason(ApiWebhookSignature.Verify(Key, "tf1-abd", timestamp, signature, Body, Now)));
        Assert.AreEqual(ApiWebhookRejection.SignatureMismatch, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", (Now.ToUnixTimeSeconds() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), signature, Body, Now)));
        var otherKey = Enumerable.Range(2, 32).Select(value => (byte)value).ToArray();
        Assert.AreEqual(ApiWebhookRejection.SignatureMismatch, Reason(ApiWebhookSignature.Verify(otherKey, "tf1-abc", timestamp, signature, Body, Now)));
        var flipped = signature[..^1] + (signature[^1] == '0' ? '1' : '0');
        Assert.AreEqual(ApiWebhookRejection.SignatureMismatch, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", timestamp, flipped, Body, Now)));
    }

    [TestMethod]
    public void TimestampsOutsideTheWindowAreRejected()
    {
        var (old, oldSignature) = Sign(Now.AddMinutes(-6));
        Assert.AreEqual(ApiWebhookRejection.Expired, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", old, oldSignature, Body, Now)));
        var (future, futureSignature) = Sign(Now.AddMinutes(2));
        Assert.AreEqual(ApiWebhookRejection.FromFuture, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", future, futureSignature, Body, Now)));
        var (edge, edgeSignature) = Sign(Now.AddMinutes(-5));
        Assert.IsInstanceOfType<ApiWebhookVerified>(ApiWebhookSignature.Verify(Key, "tf1-abc", edge, edgeSignature, Body, Now).Value);
        Assert.AreEqual(ApiWebhookRejection.Expired, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", edge, edgeSignature, Body, Now, maximumAge: TimeSpan.FromMinutes(1))));
    }

    [TestMethod]
    public void MalformedInputIsRejectedWithoutThrowing()
    {
        var (timestamp, signature) = Sign(Now);
        Assert.AreEqual(ApiWebhookRejection.Malformed, Reason(ApiWebhookSignature.Verify(Key, null, timestamp, signature, Body, Now)));
        Assert.AreEqual(ApiWebhookRejection.Malformed, Reason(ApiWebhookSignature.Verify(Key, "tf1 abc", timestamp, signature, Body, Now)));
        Assert.AreEqual(ApiWebhookRejection.Malformed, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", "-5", signature, Body, Now)));
        Assert.AreEqual(ApiWebhookRejection.Malformed, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", "999999999999", signature, Body, Now)));
        Assert.AreEqual(ApiWebhookRejection.Malformed, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", timestamp, "v2=" + signature[3..], Body, Now)));
        Assert.AreEqual(ApiWebhookRejection.Malformed, Reason(ApiWebhookSignature.Verify(Key, "tf1-abc", timestamp, "v1=" + new string('z', 64), Body, Now)));
        Assert.AreEqual(ApiWebhookRejection.Malformed, Reason(ApiWebhookSignature.Verify(Key.AsSpan()[..16], "tf1-abc", timestamp, signature, Body, Now)));
        Assert.ThrowsExactly<ArgumentException>(() => ApiWebhookSignature.Create(Key.AsSpan()[..16], "id", 1, Body));
    }

    [TestMethod]
    public void ReplayGuardRejectsReplaysButAcceptsResignedRetries()
    {
        var guard = new ApiWebhookReplayGuard();
        var (timestamp, signature) = Sign(Now);
        Assert.IsInstanceOfType<ApiWebhookVerified>(guard.Verify(Key, "tf1-abc", timestamp, signature, Body, Now).Value);
        Assert.AreEqual(ApiWebhookRejection.Replayed, Reason(guard.Verify(Key, "tf1-abc", timestamp, signature, Body, Now.AddSeconds(10))));
        var (retryTimestamp, retrySignature) = Sign(Now.AddSeconds(20));
        Assert.IsInstanceOfType<ApiWebhookVerified>(guard.Verify(Key, "tf1-abc", retryTimestamp, retrySignature, Body, Now.AddSeconds(20)).Value);
        Assert.AreEqual(ApiWebhookRejection.Expired, Reason(guard.Verify(Key, "tf1-abc", timestamp, signature, Body, Now.AddMinutes(10))));
        var tampered = (byte[])Body.Clone();
        tampered[0] ^= 1;
        Assert.AreEqual(ApiWebhookRejection.SignatureMismatch, Reason(guard.Verify(Key, "tf1-new", timestamp, signature, tampered, Now)));

        var small = new ApiWebhookReplayGuard(capacity: 1);
        Assert.IsInstanceOfType<ApiWebhookVerified>(small.Verify(Key, "tf1-abc", timestamp, signature, Body, Now).Value);
        var (other, otherSignature) = Sign(Now, "tf1-other");
        Assert.AreEqual(ApiWebhookRejection.Replayed, Reason(small.Verify(Key, "tf1-other", other, otherSignature, Body, Now)), "A full guard fails closed.");
    }

    private static ApiWebhookRejection Reason(ApiWebhookVerification verification) => verification switch
    {
        ApiWebhookVerified => throw new AssertFailedException("Expected a rejection."),
        ApiWebhookRejected rejected => rejected.Reason,
    };

    private static T Is<T>(object? value) where T : class
    {
        Assert.IsInstanceOfType<T>(value);
        return (T)value!;
    }
}
