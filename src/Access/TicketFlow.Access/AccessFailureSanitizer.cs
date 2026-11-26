using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Sanitizes post-lookup authorization failures on single-ID admin operations into opaque <see cref="TicketFailureKind.NotFound"/>
/// responses so callers cannot distinguish nonexistent identifiers from out-of-scope or higher-tier records.
/// </summary>
internal static class AccessFailureSanitizer
{
    public static TicketFailure ToOpaqueNotFound(TicketFailure failure) =>
        failure.Kind == TicketFailureKind.Forbidden || string.Equals(failure.Code, "out-of-scope", StringComparison.Ordinal)
            ? TicketFailure.NotFound
            : failure;
}
