namespace TicketFlow.Surveys;

public enum SurveyWriteRejection
{
    /// <summary>A capacity in <see cref="SurveyLimits"/> would be exceeded.</summary>
    CapacityExceeded = 1,

    /// <summary>The survey still has invitations, or a fixed invitation field (survey, conversation, contact, kind) changed.</summary>
    InUse = 2,
}

/// <summary>A new invitation was stored.</summary>
public sealed record InvitationCreated(StoredInvitation Invitation);

/// <summary>An invitation already exists for this survey and conversation; nothing was written.</summary>
public sealed record InvitationDuplicate(StoredInvitation Existing);

/// <summary>The contact was invited to this survey within the cooldown window; nothing was written.</summary>
public sealed record InvitationCoolingDown(DateTimeOffset LastInvitedAt);

/// <summary>The store is at capacity; nothing was written.</summary>
public sealed record InvitationRejected(SurveyWriteRejection Reason);

/// <summary>Outcome of an atomic, idempotent invitation create. Switch over every case without a discard arm.</summary>
public union InvitationCreateResult(InvitationCreated, InvitationDuplicate, InvitationCoolingDown, InvitationRejected);

/// <summary>The response was recorded and the invitation is now responded.</summary>
public sealed record ResponseRecorded(StoredResponse Response);

/// <summary>The invitation already has a response (returned); nothing was written.</summary>
public sealed record ResponseAlreadyRecorded(StoredResponse Existing);

/// <summary>The invitation does not exist in the scope, has no token yet, or was cancelled.</summary>
public sealed record ResponseInvitationNotFound
{
    private ResponseInvitationNotFound()
    {
    }

    public static ResponseInvitationNotFound Instance { get; } = new();
}

/// <summary>The invitation's response window closed before the response time.</summary>
public sealed record ResponseInvitationExpired(DateTimeOffset ExpiredAt);

/// <summary>Outcome of an atomic single-response write. Switch over every case without a discard arm.</summary>
public union ResponseRecordResult(ResponseRecorded, ResponseAlreadyRecorded, ResponseInvitationNotFound, ResponseInvitationExpired);

/// <summary>Capacity limits survey stores enforce atomically with writes.</summary>
public static class SurveyLimits
{
    public const int MaximumSurveysPerWorkspace = 64;
    public const int MaximumDuePerRead = 500;
    public const int MaximumPageSize = 200;

    /// <summary>UTC with whole-microsecond precision, the resolution durable stores (PostgreSQL) keep.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
