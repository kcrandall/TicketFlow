using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Reporting;

/// <summary>
/// The reporting dimensions of a fact at the instant it happened: the conversation's (immutable) inbox, its team, the attributed
/// agent, the channel kind token, the priority and the labels (≤ 64, ordinal order). Identifiers only — never bodies or addresses.
/// </summary>
public sealed record ReportDimensions
{
    public const int MaximumChannelLength = 64;
    public const int MaximumLabels = Conversation.MaximumLabels;

    public ReportDimensions(InboxId inbox, TeamId? team, UserId? agent, string? channel, ConversationPriority priority, IEnumerable<LabelId>? labels = null)
    {
        if (inbox == default || team == default(TeamId) || agent == default(UserId))
            throw new ArgumentException("Inbox, team and agent identifiers must be initialized when supplied.", inbox == default ? nameof(inbox) : team == default(TeamId) ? nameof(team) : nameof(agent));
        if (!Enum.IsDefined(priority))
            throw new ArgumentOutOfRangeException(nameof(priority));
        Inbox = inbox;
        Team = team;
        Agent = agent;
        Channel = channel is null ? null : ReportText.RequireToken(channel, MaximumChannelLength, nameof(channel));
        Priority = priority;
        Labels = Snapshot(labels);
    }

    internal ReportDimensions(InboxId inbox, TeamId? team, UserId? agent, string? channel, ConversationPriority priority, IReadOnlyList<LabelId> validatedLabels)
    {
        Inbox = inbox;
        Team = team;
        Agent = agent;
        Channel = channel;
        Priority = priority;
        Labels = validatedLabels;
    }

    public InboxId Inbox { get; }
    public TeamId? Team { get; }

    /// <summary>The agent the fact is attributed to (the replying user for responses, otherwise the assignee at the time).</summary>
    public UserId? Agent { get; }

    public string? Channel { get; }
    public ConversationPriority Priority { get; }

    /// <summary>Distinct labels in ordinal order.</summary>
    public IReadOnlyList<LabelId> Labels { get; }

    public ReportDimensions WithAgent(UserId? agent)
    {
        if (agent == default(UserId))
            throw new ArgumentException("Inbox, team and agent identifiers must be initialized when supplied.", nameof(agent));
        return new(Inbox, Team, agent, Channel, Priority, Labels);
    }

    public bool Equals(ReportDimensions? other) =>
        other is not null && Inbox == other.Inbox && Team == other.Team && Agent == other.Agent && string.Equals(Channel, other.Channel, StringComparison.Ordinal) && Priority == other.Priority
        && Labels.SequenceEqual(other.Labels);

    public override int GetHashCode() => HashCode.Combine(Inbox, Team, Agent, Channel, Priority, Labels.Count);

    internal static IReadOnlyList<LabelId> Snapshot(IEnumerable<LabelId>? labels)
    {
        if (labels is null)
            return [];
        if (labels is IReadOnlyList<LabelId> list)
        {
            var count = list.Count;
            if (count == 0)
                return [];
            if (count == 1)
            {
                var only = list[0];
                if (only == default)
                    throw new ArgumentException($"At most {MaximumLabels} initialized labels are allowed.", nameof(labels));
                return [only];
            }
            if (count <= MaximumLabels)
            {
                var ordered = true;
                var prev = list[0];
                if (prev == default)
                    throw new ArgumentException($"At most {MaximumLabels} initialized labels are allowed.", nameof(labels));
                for (var i = 1; i < count; i++)
                {
                    var cur = list[i];
                    if (cur == default)
                        throw new ArgumentException($"At most {MaximumLabels} initialized labels are allowed.", nameof(labels));
                    if (string.CompareOrdinal(prev.Value, cur.Value) >= 0)
                    {
                        ordered = false;
                        break;
                    }
                    prev = cur;
                }
                if (ordered)
                {
                    var copy = new LabelId[count];
                    for (var i = 0; i < count; i++)
                        copy[i] = list[i];
                    return copy;
                }
            }
        }
        IReadOnlyList<LabelId> snapshot = [.. labels.Distinct().OrderBy(label => label.Value, StringComparer.Ordinal)];
        if (snapshot.Count > MaximumLabels)
            throw new ArgumentException($"At most {MaximumLabels} initialized labels are allowed.", nameof(labels));
        for (var i = 0; i < snapshot.Count; i++)
            if (snapshot[i] == default)
                throw new ArgumentException($"At most {MaximumLabels} initialized labels are allowed.", nameof(labels));
        return snapshot;
    }
}

/// <summary>
/// One immutable report fact. Its identity is (<see cref="SourceId"/>, <see cref="Metric"/>) within the scope: the source is the
/// timeline event that produced it (conversation-level facts such as <see cref="ReportMetric.ConversationCreated"/> use an
/// identifier derived from the conversation, <see cref="ReportFactIds.Created"/>), so replaying a source never adds a second fact.
/// <see cref="Value"/> is 1 for count metrics and microseconds (0 to <see cref="MaximumDuration"/>) for duration metrics.
/// </summary>
public sealed record ReportFact
{
    /// <summary>Longest recorded duration (about ten years, the business-calendar horizon).</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(3660);

    public ReportFact(Guid sourceId, ReportMetric metric, ConversationId conversation, DateTimeOffset occurredAt, ReportDimensions dimensions, long value = 1)
    {
        if (sourceId == Guid.Empty || conversation == default)
            throw new ArgumentException("Source and conversation identifiers are required.", sourceId == Guid.Empty ? nameof(sourceId) : nameof(conversation));
        ReportMetrics.Require(metric, nameof(metric));
        ArgumentNullException.ThrowIfNull(dimensions);
        if (metric.IsDuration ? value < 0 || value > MaximumDuration.Ticks / 10 : value != 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Count facts carry 1; duration facts carry 0 to 3660 days in microseconds.");
        SourceId = sourceId;
        Metric = metric;
        Conversation = conversation;
        OccurredAt = ReportText.Instant(occurredAt);
        Dimensions = dimensions;
        Value = value;
    }

    public Guid SourceId { get; }
    public ReportMetric Metric { get; }
    public ConversationId Conversation { get; }
    public DateTimeOffset OccurredAt { get; }
    public ReportDimensions Dimensions { get; }
    public long Value { get; }

    /// <summary>The 15-minute UTC rollup cell the fact belongs to.</summary>
    public DateTimeOffset CellStart => ReportBuckets.CellStart(OccurredAt);

    /// <summary>The histogram bin for duration metrics, 0 for count metrics.</summary>
    public int Bin => Metric.IsDuration ? ReportHistogram.Bin(Value) : 0;

    /// <summary>Duration facts as a <see cref="TimeSpan"/>; <see langword="null"/> for count facts.</summary>
    public TimeSpan? Duration => Metric.IsDuration ? TimeSpan.FromTicks(Value * 10) : null;

    public static long Microseconds(TimeSpan duration) => duration.Ticks / 10;

    public override string ToString() => $"ReportFact {{ Metric = {Metric}, SourceId = {SourceId} }}";
}

/// <summary>Deterministic source identifiers for facts that have no timeline event of their own.</summary>
public static class ReportFactIds
{
    private static ReadOnlySpan<byte> CreatedPrefix => "tf-report-created-v1\u001F"u8;

    /// <summary>The source of a conversation's <see cref="ReportMetric.ConversationCreated"/> fact: a name-based UUID over the scope and conversation.</summary>
    public static Guid Created(TicketScope scope, ConversationId conversation)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        var workspace = scope.WorkspaceId.Value;
        var conv = conversation.Value;
        var maxBytes = CreatedPrefix.Length
            + Encoding.UTF8.GetMaxByteCount(scope.TenantId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(scope.ProjectId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(scope.EnvironmentId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(scope.AppId.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(workspace.Length) + 1
            + Encoding.UTF8.GetMaxByteCount(conv.Length);
        byte[]? rented = null;
        var buffer = maxBytes <= 512 ? stackalloc byte[maxBytes] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes)).AsSpan(0, maxBytes);
        try
        {
            var pos = 0;
            CreatedPrefix.CopyTo(buffer);
            pos += CreatedPrefix.Length;
            pos += Encoding.UTF8.GetBytes(scope.TenantId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(scope.ProjectId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(scope.EnvironmentId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(scope.AppId, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(workspace, buffer[pos..]);
            buffer[pos++] = 0x1F;
            pos += Encoding.UTF8.GetBytes(conv, buffer[pos..]);
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(buffer[..pos], hash);
            hash[6] = (byte)((hash[6] & 0x0F) | 0x80);   // RFC 9562 version 8 (custom)
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // RFC 9562 variant
            return new Guid(hash[..16], bigEndian: true);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>
/// The per-conversation projection state the reporting projector keeps between changes: the dimensions as of
/// <see cref="LastSequence"/>, the unanswered-wait marker, whether a first response and how many resolutions happened. Stores
/// fence it on revision; open states (not resolved or closed) form the open backlog.
/// </summary>
public sealed record ReportConversationState
{
    public ReportConversationState(ConversationId conversation, InboxId inbox, string? channel, DateTimeOffset createdAt, ConversationStatusKind status,
        UserId? assignee, TeamId? team, ConversationPriority priority, IEnumerable<LabelId>? labels, DateTimeOffset? waitingSince, DateTimeOffset? firstResponseAt,
        int resolutions, long lastSequence)
    {
        if (conversation == default || inbox == default || assignee == default(UserId) || team == default(TeamId))
            throw new ArgumentException("Conversation, inbox, assignee and team identifiers must be initialized.", conversation == default ? nameof(conversation) : inbox == default ? nameof(inbox) : assignee == default(UserId) ? nameof(assignee) : nameof(team));
        if (!Enum.IsDefined(status) || !Enum.IsDefined(priority))
            throw new ArgumentOutOfRangeException(nameof(status));
        ArgumentOutOfRangeException.ThrowIfNegative(resolutions);
        ArgumentOutOfRangeException.ThrowIfNegative(lastSequence);
        Conversation = conversation;
        Inbox = inbox;
        Channel = channel is null ? null : ReportText.RequireToken(channel, ReportDimensions.MaximumChannelLength, nameof(channel));
        CreatedAt = ReportText.Instant(createdAt);
        Status = status;
        Assignee = assignee;
        Team = team;
        Priority = priority;
        Labels = ReportDimensions.Snapshot(labels);
        WaitingSince = waitingSince is { } waiting ? ReportText.Instant(waiting) : null;
        FirstResponseAt = firstResponseAt is { } first ? ReportText.Instant(first) : null;
        Resolutions = resolutions;
        LastSequence = lastSequence;
    }

    public ConversationId Conversation { get; }
    public InboxId Inbox { get; }
    public string? Channel { get; }
    public DateTimeOffset CreatedAt { get; }
    public ConversationStatusKind Status { get; }
    public UserId? Assignee { get; }
    public TeamId? Team { get; }
    public ConversationPriority Priority { get; }

    /// <summary>Distinct labels in ordinal order.</summary>
    public IReadOnlyList<LabelId> Labels { get; }

    /// <summary>The oldest incoming message not yet answered by a user reply, resolve or close.</summary>
    public DateTimeOffset? WaitingSince { get; }

    /// <summary>The first user reply that answered a waiting contact.</summary>
    public DateTimeOffset? FirstResponseAt { get; }

    public int Resolutions { get; }
    public long LastSequence { get; }

    /// <summary>Not resolved or closed (counts toward the open backlog).</summary>
    public bool IsOpen => Status is not (ConversationStatusKind.Resolved or ConversationStatusKind.Closed);

    /// <summary>The dimensions as of this state, attributed to the assignee.</summary>
    public ReportDimensions Dimensions() => new(Inbox, Team, Assignee, Channel, Priority, Labels);

    public bool Equals(ReportConversationState? other) =>
        other is not null && Conversation == other.Conversation && Inbox == other.Inbox && string.Equals(Channel, other.Channel, StringComparison.Ordinal) && CreatedAt == other.CreatedAt
        && Status == other.Status && Assignee == other.Assignee && Team == other.Team && Priority == other.Priority && Labels.SequenceEqual(other.Labels)
        && WaitingSince == other.WaitingSince && FirstResponseAt == other.FirstResponseAt && Resolutions == other.Resolutions && LastSequence == other.LastSequence;

    public override int GetHashCode() => HashCode.Combine(Conversation, Status, LastSequence);
}

/// <summary>A reporting state with its scope and revision (first revision is 1).</summary>
public sealed record StoredReportConversation(TicketScope Scope, ReportConversationState State, long Revision);

/// <summary>
/// One atomic projection write: the conversation's new state and the facts derived since the previous state (≤
/// <see cref="MaximumFacts"/>, all of that conversation).
/// </summary>
public sealed record ReportProjection
{
    public const int MaximumFacts = 4096;

    public ReportProjection(ReportConversationState state, IEnumerable<ReportFact> facts)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(facts);
        ReportFact[] copy = [.. facts];
        if (copy.Length > MaximumFacts)
            throw new ArgumentException($"A projection carries at most {MaximumFacts} facts of its conversation.", nameof(facts));
        for (var i = 0; i < copy.Length; i++)
            if (copy[i] is null || copy[i].Conversation != state.Conversation)
                throw new ArgumentException($"A projection carries at most {MaximumFacts} facts of its conversation.", nameof(facts));
        Facts = copy;
    }

    public ReportConversationState State { get; }
    public IReadOnlyList<ReportFact> Facts { get; }
}
