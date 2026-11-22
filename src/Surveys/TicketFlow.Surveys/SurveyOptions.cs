using System.Security.Cryptography;

namespace TicketFlow.Surveys;

/// <summary>Dispatch settings.</summary>
public sealed class SurveyOptions
{
    /// <summary>Delivery attempts (claims) before an invitation is marked failed (1-20, default 5).</summary>
    public int MaximumSendAttempts { get; set; } = 5;

    /// <summary>Delay before the first retry; doubles per attempt up to <see cref="MaximumRetryDelay"/> (default 5 minutes).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for the retry delay (default 6 hours).</summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a claimed invitation belongs to one dispatcher (default 5 minutes, at most 1 hour). A send must finish within the lease;
    /// when a dispatcher stops mid-send the invitation becomes due again after it.
    /// </summary>
    public TimeSpan SendLease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Secret (at least 32 bytes) from which invitation tokens are derived with HMAC-SHA256 (<see cref="SurveyToken.Derive"/>), so a
    /// retry re-sends the same link. The default is a random per-process key: configure a stable secret shared by every dispatcher
    /// host in production, otherwise a retry after a restart (or on another host) must issue a new token and links from earlier
    /// attempts stop working. Keep it outside the database; rotating it has the same effect for invitations still being retried.
    /// </summary>
    public byte[] TokenKey { get; set; } = RandomNumberGenerator.GetBytes(32);

    internal void Validate()
    {
        if (MaximumSendAttempts is < 1 or > SurveyInvitation.MaximumAttempts)
            throw new InvalidOperationException($"MaximumSendAttempts must be between 1 and {SurveyInvitation.MaximumAttempts}.");
        if (RetryDelay <= TimeSpan.Zero || MaximumRetryDelay < RetryDelay || MaximumRetryDelay > TimeSpan.FromDays(7))
            throw new InvalidOperationException("Retry delays must be positive, ordered and at most seven days.");
        if (SendLease < TimeSpan.FromSeconds(1) || SendLease > TimeSpan.FromHours(1))
            throw new InvalidOperationException("SendLease must be between one second and one hour.");
        if (TokenKey is null || TokenKey.Length < SurveyToken.MinimumKeyLength)
            throw new InvalidOperationException($"TokenKey must contain at least {SurveyToken.MinimumKeyLength} bytes.");
    }
}
