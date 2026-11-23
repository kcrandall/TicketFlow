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

/// <summary>The range's cells were recomputed from <see cref="Facts"/> facts.</summary>
public sealed record ReportRebuilt(long Facts);

/// <summary>The range starts before <see cref="FactHorizon"/>, where facts may have been pruned; nothing was changed.</summary>
public sealed record ReportRebuildRefused(DateTimeOffset FactHorizon);

/// <summary>Outcome of a rollup rebuild. Switch over every case without a discard arm.</summary>
public union ReportRebuildResult(ReportRebuilt, ReportRebuildRefused);
