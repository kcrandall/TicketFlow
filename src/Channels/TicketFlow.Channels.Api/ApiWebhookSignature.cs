using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TicketFlow.Channels.Api;

/// <summary>Webhook header names used by the API channel.</summary>
public static class ApiWebhookHeaders
{
    /// <summary>The stable delivery identifier (the submission's idempotency key; identical across retries of one message).</summary>
    public const string Id = "TicketFlow-Webhook-Id";

    /// <summary>Unix time in seconds when the request was signed.</summary>
    public const string Timestamp = "TicketFlow-Webhook-Timestamp";

    /// <summary><c>v1=</c> followed by the lowercase hex HMAC-SHA256.</summary>
    public const string Signature = "TicketFlow-Webhook-Signature";

    /// <summary>The application's key identifier, for key rotation (not part of the signature).</summary>
    public const string KeyId = "TicketFlow-Webhook-Key-Id";
}

/// <summary>The signature and timestamp are valid; deduplicate <see cref="DeliveryId"/> before processing.</summary>
public sealed record ApiWebhookVerified(string DeliveryId, DateTimeOffset SignedAt);

public enum ApiWebhookRejection
{
    /// <summary>Missing or malformed headers, key or body bounds.</summary>
    Malformed = 1,

    /// <summary>The timestamp is older than the replay window.</summary>
    Expired = 2,

    /// <summary>The timestamp is further in the future than the allowed clock skew.</summary>
    FromFuture = 3,

    /// <summary>The signature does not match (wrong key, tampered body, id or timestamp).</summary>
    SignatureMismatch = 4,

    /// <summary>This exact signed request was already accepted (a replay).</summary>
    Replayed = 5,
}

public sealed record ApiWebhookRejected(ApiWebhookRejection Reason);

/// <summary>Outcome of verifying a signed webhook. Switch over every case without a discard arm.</summary>
public union ApiWebhookVerification(ApiWebhookVerified, ApiWebhookRejected);

/// <summary>
/// Version-one webhook signatures: HMAC-SHA256 with a 32-64 byte key over <c>"v1\n" + timestamp + "\n" + deliveryId + "\n"</c>
/// followed by the exact body bytes. Verification compares in constant time and enforces a replay window (default 5 minutes)
/// and future skew (default 1 minute). It authenticates bytes only: receivers must also reject replays inside the window
/// (<see cref="ApiWebhookReplayGuard"/>) and deduplicate the delivery id durably.
/// </summary>
public static class ApiWebhookSignature
{
    public const int MinimumKeyBytes = 32;
    public const int MaximumKeyBytes = 64;
    public const int MaximumBodyBytes = 1024 * 1024;
    public const int MaximumDeliveryIdLength = 200;

    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultFutureSkew = TimeSpan.FromMinutes(1);

    public static string Create(ReadOnlySpan<byte> key, string deliveryId, long timestamp, ReadOnlySpan<byte> body)
    {
        if (key.Length is < MinimumKeyBytes or > MaximumKeyBytes)
            throw new ArgumentException($"Signing keys are {MinimumKeyBytes}-{MaximumKeyBytes} bytes.", nameof(key));
        RequireDeliveryId(deliveryId);
        if (body.Length > MaximumBodyBytes)
            throw new ArgumentException("The body exceeds the signing bound.", nameof(body));
        _ = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        Span<byte> hash = stackalloc byte[32];
        ComputeHash(key, deliveryId, timestamp, body, hash);
        Span<char> output = stackalloc char[67];
        output[0] = 'v';
        output[1] = '1';
        output[2] = '=';
        Convert.TryToHexStringLower(hash, output[3..], out _);
        return new string(output);
    }

    /// <summary>Verifies header values against the body. Never throws for attacker-controlled input.</summary>
    public static ApiWebhookVerification Verify(ReadOnlySpan<byte> key, string? deliveryId, string? timestamp, string? signature, ReadOnlySpan<byte> body,
        DateTimeOffset now, TimeSpan? maximumAge = null, TimeSpan? futureSkew = null)
    {
        var age = maximumAge ?? DefaultMaximumAge;
        var future = futureSkew ?? DefaultFutureSkew;
        if (age <= TimeSpan.Zero || age > TimeSpan.FromHours(1) || future < TimeSpan.Zero || future > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Replay windows are at most 1 hour and skew at most 5 minutes.");
        if (key.Length is < MinimumKeyBytes or > MaximumKeyBytes || deliveryId is null || !IsDeliveryId(deliveryId) || timestamp is null
            || timestamp.Length is 0 or > 12 || !IsAllAsciiDigits(timestamp) || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || signature is null || signature.Length != 67 || !signature.StartsWith("v1=", StringComparison.Ordinal) || body.Length > MaximumBodyBytes)
            return new ApiWebhookRejected(ApiWebhookRejection.Malformed);
        DateTimeOffset signedAt;
        Span<byte> supplied = stackalloc byte[32];
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (Convert.FromHexString(signature.AsSpan(3), supplied, out _, out var written) != OperationStatus.Done || written != 32)
                return new ApiWebhookRejected(ApiWebhookRejection.Malformed);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new ApiWebhookRejected(ApiWebhookRejection.Malformed);
        }
        Span<byte> expected = stackalloc byte[32];
        ComputeHash(key, deliveryId, seconds, body, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            return new ApiWebhookRejected(ApiWebhookRejection.SignatureMismatch);
        if (signedAt < now - age)
            return new ApiWebhookRejected(ApiWebhookRejection.Expired);
        if (signedAt > now + future)
            return new ApiWebhookRejected(ApiWebhookRejection.FromFuture);
        return new ApiWebhookVerified(deliveryId, signedAt);
    }

    private static void ComputeHash(ReadOnlySpan<byte> key, string deliveryId, long timestamp, ReadOnlySpan<byte> body, Span<byte> destination)
    {
        // "v1\n" (3) + timestamp (<=20) + "\n" (1) + deliveryId (<=200 ASCII) + "\n" (1) <= 225 bytes.
        Span<byte> prefix = stackalloc byte[256];
        prefix[0] = (byte)'v';
        prefix[1] = (byte)'1';
        prefix[2] = (byte)'\n';
        Utf8Formatter.TryFormat(timestamp, prefix[3..], out var tsLen);
        var pos = 3 + tsLen;
        prefix[pos++] = (byte)'\n';
        pos += Encoding.ASCII.GetBytes(deliveryId.AsSpan(), prefix[pos..]);
        prefix[pos++] = (byte)'\n';
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(prefix[..pos]);
        hmac.AppendData(body);
        hmac.GetHashAndReset(destination);
    }

    private static bool IsAllAsciiDigits(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (!char.IsAsciiDigit(value[i]))
                return false;
        return true;
    }

    internal static bool IsDeliveryId(string value)
    {
        if (value.Length is 0 or > MaximumDeliveryIdLength)
            return false;
        for (var i = 0; i < value.Length; i++)
            if (value[i] is < '!' or > '~')
                return false;
        return true;
    }

    private static void RequireDeliveryId(string deliveryId)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        if (!IsDeliveryId(deliveryId))
            throw new ArgumentException($"Delivery identifiers are 1-{MaximumDeliveryIdLength} printable ASCII characters.", nameof(deliveryId));
    }
}

/// <summary>
/// A bounded, process-local replay guard for webhook receivers: remembers each accepted (delivery id, timestamp) pair until it
/// leaves the replay window, so a captured request cannot be replayed while its timestamp is still fresh. Legitimate retries are
/// newly signed (new timestamp) and pass; receivers deduplicate their delivery ids durably. When full, the oldest entries are
/// evicted only if already expired; otherwise new requests are refused (fail closed). Thread-safe.
/// </summary>
public sealed class ApiWebhookReplayGuard
{
    private readonly Lock gate = new();
    private readonly HashSet<(string Id, DateTimeOffset At)> seen = [];
    private readonly PriorityQueue<(string Id, DateTimeOffset At), DateTimeOffset> expiry = new();
    private readonly TimeSpan maximumAge;
    private readonly TimeSpan futureSkew;
    private readonly TimeSpan retention;
    private readonly int capacity;

    public ApiWebhookReplayGuard(TimeSpan? maximumAge = null, TimeSpan? futureSkew = null, int capacity = 100_000)
    {
        var age = maximumAge ?? ApiWebhookSignature.DefaultMaximumAge;
        var future = futureSkew ?? ApiWebhookSignature.DefaultFutureSkew;
        if (age <= TimeSpan.Zero || age > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Replay windows must be positive and at most 1 hour.");
        if (future < TimeSpan.Zero || future > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(futureSkew), "Future skew must be non-negative and at most 5 minutes.");
        this.maximumAge = age;
        this.futureSkew = future;
        retention = age + future;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.capacity = capacity;
    }

    /// <summary>Verifies the signature and records the request; a repeat of an accepted request is <see cref="ApiWebhookRejection.Replayed"/>.</summary>
    public ApiWebhookVerification Verify(ReadOnlySpan<byte> key, string? deliveryId, string? timestamp, string? signature, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        var result = ApiWebhookSignature.Verify(key, deliveryId, timestamp, signature, body, now, maximumAge, futureSkew);
        if (result.Value is not ApiWebhookVerified verified)
            return result;
        lock (gate)
        {
            while (expiry.TryPeek(out var oldest, out var until) && until < now)
            {
                expiry.Dequeue();
                seen.Remove(oldest);
            }
            var entry = (verified.DeliveryId, verified.SignedAt);
            if (seen.Count >= capacity || !seen.Add(entry))
                return new ApiWebhookRejected(ApiWebhookRejection.Replayed);
            var expires = verified.SignedAt + retention;
            expiry.Enqueue(entry, expires);
        }
        return verified;
    }
}
