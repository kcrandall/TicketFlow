namespace TicketFlow.Surveys;

/// <summary>Dispatch retry settings.</summary>
public sealed class SurveyOptions
{
    /// <summary>Delivery attempts before an invitation is marked failed (1-20, default 5).</summary>
    public int MaximumSendAttempts { get; set; } = 5;

    /// <summary>Delay before the first retry; doubles per attempt up to <see cref="MaximumRetryDelay"/> (default 5 minutes).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for the retry delay (default 6 hours).</summary>
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(6);

    internal void Validate()
    {
        if (MaximumSendAttempts is < 1 or > SurveyInvitation.MaximumAttempts)
            throw new InvalidOperationException($"MaximumSendAttempts must be between 1 and {SurveyInvitation.MaximumAttempts}.");
        if (RetryDelay <= TimeSpan.Zero || MaximumRetryDelay < RetryDelay || MaximumRetryDelay > TimeSpan.FromDays(7))
            throw new InvalidOperationException("Retry delays must be positive, ordered and at most seven days.");
    }
}
