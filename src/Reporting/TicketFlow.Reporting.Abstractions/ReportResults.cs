namespace TicketFlow.Reporting;

/// <summary>The projection (or state delete) was applied; <see cref="InsertedFacts"/> counts facts that were new (rollups were updated for exactly these).</summary>
public sealed record ReportWriteApplied(StoredReportConversation Conversation, int InsertedFacts);

/// <summary>The expected revision did not match (<see cref="CurrentRevision"/> is <see langword="null"/> when no state exists). Nothing was written.</summary>
public sealed record ReportWriteConflict(long? CurrentRevision);

/// <summary>The store refused the write for a contract reason. Nothing was written.</summary>
public sealed record ReportWriteRejected(ReportWriteRejection Reason);

public enum ReportWriteRejection
{
    /// <summary>A bounded (development) store is full.</summary>
    CapacityExceeded = 1,
}

/// <summary>Outcome of a reporting store write. Switch over every case without a discard arm.</summary>
public union ReportWriteResult(ReportWriteApplied, ReportWriteConflict, ReportWriteRejected);

/// <summary>The authorized report succeeded.</summary>
public sealed record ReportSucceeded<T>(T Value);

/// <summary>The request was refused. <see cref="Errors"/> are non-sensitive messages.</summary>
public sealed record ReportRefused(ReportRefusal Reason, IReadOnlyList<string> Errors);

public enum ReportRefusal
{
    Forbidden = 1,
    Invalid = 2,
}

/// <summary>Outcome of a report service call. Switch over every case without a discard arm.</summary>
public union ReportResult<T>(ReportSucceeded<T>, ReportRefused);
