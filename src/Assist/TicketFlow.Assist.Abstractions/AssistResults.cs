namespace TicketFlow.Assist;

/// <summary>Whether the assistant action was recorded on the conversation timeline.</summary>
public enum AssistRecording
{
    /// <summary>The feature is not recorded (sentiment, translation, tone) or recording is disabled.</summary>
    NotApplicable = 1,

    /// <summary>An <c>assist.*</c> extension event was appended.</summary>
    Recorded = 2,

    /// <summary>The recording principal may not append extension events (<c>conversation:manage</c>).</summary>
    Forbidden = 3,

    /// <summary>The append failed (conflict, closed conversation, store rejection).</summary>
    Failed = 4,
}

/// <summary>The suggestion was produced. <see cref="Model"/> is the TicketFlow model identifier; <see cref="UsageId"/> the durable usage record.</summary>
public sealed record AssistSucceeded<T>(T Value, AssistUsage Usage, string Model, Guid UsageId, AssistRecording Recording);

public enum AssistRefusal
{
    /// <summary>Access denied the read or <c>assist:use</c>/<c>assist:manage</c>.</summary>
    Forbidden = 1,

    /// <summary>The conversation (or message) does not exist in the actor's scope.</summary>
    NotFound = 2,

    /// <summary>The input is invalid in context (for example an unregistered model).</summary>
    Invalid = 3,

    /// <summary>The assistant is disabled for the workspace.</summary>
    Disabled = 4,

    /// <summary>The workspace's daily token quota is exhausted; see <see cref="AssistRefused.RetryAfter"/>.</summary>
    QuotaExceeded = 5,

    /// <summary>The workspace's per-minute request limit is reached; see <see cref="AssistRefused.RetryAfter"/>.</summary>
    RateLimited = 6,

    /// <summary>The model's safety system declined the request.</summary>
    ModelRefused = 7,

    /// <summary>The settings changed concurrently; see <see cref="AssistRefused.CurrentRevision"/>.</summary>
    Conflict = 8,
}

/// <summary>The request was refused. <see cref="Errors"/> are non-sensitive messages.</summary>
public sealed record AssistRefused(AssistRefusal Reason, IReadOnlyList<string> Errors, TimeSpan? RetryAfter = null, long? CurrentRevision = null);

/// <summary>The model provider failed. <see cref="Retryable"/> tells whether repeating the request may succeed.</summary>
public sealed record AssistProviderFailed(AssistProviderFailure Failure, bool Retryable, TimeSpan? RetryAfter = null);

/// <summary>Outcome of an assistant feature. Switch over every case without a discard arm.</summary>
public union AssistResult<T>(AssistSucceeded<T>, AssistRefused, AssistProviderFailed);
