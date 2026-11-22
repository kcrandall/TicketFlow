using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Channels.Mailbox;

/// <summary>Supplies IMAP credentials for a configured mailbox endpoint without embedding secrets in channel configuration.</summary>
public sealed record MailboxCredentials(string Username, string Password)
{
    public override string ToString() => "MailboxCredentials { <redacted> }";
}

/// <summary>Application-supplied credential provider for IMAP endpoints.</summary>
public interface IMailboxCredentialProvider
{
    Task<MailboxCredentials> GetCredentialsAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default);
}

/// <summary>Static in-memory credential provider for development and tests.</summary>
public sealed class StaticMailboxCredentialProvider(MailboxCredentials credentials) : IMailboxCredentialProvider
{
    private readonly MailboxCredentials credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));

    public Task<MailboxCredentials> GetCredentialsAsync(TicketScope scope, string configurationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(credentials);
}

/// <summary>Configuration of one polled IMAP mailbox bound to a TicketFlow channel configuration.</summary>
public sealed record MailboxEndpointConfig
{
    public const int DefaultPort = 993;
    public const int DefaultMaximumMessagesPerPoll = 50;
    public const int MaximumMessagesPerPollLimit = 500;
    public const int DefaultMaximumMessageBytes = 10 * 1024 * 1024;
    public const int DefaultMaximumAttachmentBytes = 10 * 1024 * 1024;

    public MailboxEndpointConfig(
        TicketScope scope,
        string configurationId,
        string host,
        int port = DefaultPort,
        bool requireTls = true,
        string folder = "INBOX",
        ChannelKind? channel = null,
        int maximumMessagesPerPoll = DefaultMaximumMessagesPerPoll,
        int maximumMessageBytes = DefaultMaximumMessageBytes,
        int maximumAttachmentBytes = DefaultMaximumAttachmentBytes,
        int maximumAttachments = ConversationMessageLimits.MaximumAttachments,
        TimeSpan? connectTimeout = null)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        ConfigurationId = TicketScope.RequireIdentifier(configurationId, nameof(configurationId));
        if (string.IsNullOrWhiteSpace(host) || host.Length > 255 || host.Any(char.IsWhiteSpace))
            throw new ArgumentException("A valid IMAP host is required.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (string.IsNullOrWhiteSpace(folder) || folder.Length > 256)
            throw new ArgumentException("A valid IMAP folder name is required.", nameof(folder));
        if (maximumMessagesPerPoll is < 1 or > MaximumMessagesPerPollLimit)
            throw new ArgumentOutOfRangeException(nameof(maximumMessagesPerPoll));
        if (maximumMessageBytes is < 1024 or > 50 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
        if (maximumAttachmentBytes is < 1 or > 50 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumAttachmentBytes));
        if (maximumAttachments is < 0 or > ConversationMessageLimits.MaximumAttachments)
            throw new ArgumentOutOfRangeException(nameof(maximumAttachments));
        var timeout = connectTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));

        Host = host;
        Port = port;
        RequireTls = requireTls;
        Folder = folder;
        Channel = channel is { IsInitialized: true } kind ? kind : new ChannelKind("email");
        MaximumMessagesPerPoll = maximumMessagesPerPoll;
        MaximumMessageBytes = maximumMessageBytes;
        MaximumAttachmentBytes = maximumAttachmentBytes;
        MaximumAttachments = maximumAttachments;
        ConnectTimeout = timeout;
    }

    public TicketScope Scope { get; }
    public string ConfigurationId { get; }
    public string Host { get; }
    public int Port { get; }
    public bool RequireTls { get; }
    public string Folder { get; }
    public ChannelKind Channel { get; }
    public int MaximumMessagesPerPoll { get; }
    public int MaximumMessageBytes { get; }
    public int MaximumAttachmentBytes { get; }
    public int MaximumAttachments { get; }
    public TimeSpan ConnectTimeout { get; }

    public override string ToString() => $"MailboxEndpointConfig {{ ConfigurationId = {ConfigurationId}, Host = {Host}, Port = {Port}, Folder = {Folder} }}";
}

/// <summary>An attachment extracted from an inbound email ready to be persisted by the application's storage seam.</summary>
public sealed record MailboxAttachmentUpload(string FileName, string MediaType, long SizeBytes, Stream Content);

/// <summary>
/// Application-owned attachment storage seam. TicketFlow never persists raw attachment bytes in its own tables; implementations
/// write the stream to blob/object storage and return a bounded <see cref="AttachmentRef"/>.
/// </summary>
public interface IMailboxAttachmentStore
{
    Task<AttachmentRef> StoreAsync(TicketScope scope, string configurationId, MailboxAttachmentUpload upload, CancellationToken cancellationToken = default);
}

/// <summary>In-memory development and test attachment store.</summary>
public sealed class InMemoryMailboxAttachmentStore : IMailboxAttachmentStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

    public async Task<AttachmentRef> StoreAsync(TicketScope scope, string configurationId, MailboxAttachmentUpload upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(upload);
        using var ms = new MemoryStream();
        await upload.Content.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var bytes = ms.ToArray();
        var key = $"mem://mailbox/{configurationId}/{Guid.NewGuid():N}";
        lock (gate)
            blobs[key] = bytes;
        return new AttachmentRef(key, upload.FileName, upload.MediaType, bytes.Length);
    }

    public byte[]? GetBytes(string storageRef)
    {
        lock (gate)
            return blobs.TryGetValue(storageRef, out var bytes) ? bytes : null;
    }
}

/// <summary>Durable checkpoint of an IMAP folder's <see cref="UidValidity"/> and highest processed <see cref="LastUid"/>.</summary>
public sealed record MailboxCheckpoint(uint UidValidity, uint LastUid, DateTimeOffset UpdatedAt);

/// <summary>
/// Durable checkpoint and <c>Message-Id</c> deduplication store for IMAP polling. <see cref="TryRecordMessageIdAsync"/> is atomic:
/// returning <see langword="false"/> when the message id was already recorded for this mailbox folder.
/// </summary>
public interface IMailboxCheckpointStore
{
    Task<MailboxCheckpoint?> GetCheckpointAsync(TicketScope scope, string configurationId, string folder, CancellationToken cancellationToken = default);

    Task SaveCheckpointAsync(TicketScope scope, string configurationId, string folder, MailboxCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="messageId"/> as seen; returns <see langword="true"/> if newly recorded, <see langword="false"/> if already seen.</summary>
    Task<bool> TryRecordMessageIdAsync(TicketScope scope, string configurationId, string folder, string messageId, DateTimeOffset seenAt, CancellationToken cancellationToken = default);

    /// <summary>Checks whether <paramref name="messageId"/> has already been recorded for this mailbox folder.</summary>
    Task<bool> HasSeenMessageIdAsync(TicketScope scope, string configurationId, string folder, string messageId, CancellationToken cancellationToken = default);

    /// <summary>Prunes seen <c>Message-Id</c> records older than <paramref name="olderThan"/> (at most <paramref name="limit"/>).</summary>
    Task<int> PruneSeenMessageIdsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default);
}

/// <summary>In-memory development and test implementation of <see cref="IMailboxCheckpointStore"/>.</summary>
public sealed class InMemoryMailboxCheckpointStore : IMailboxCheckpointStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, string Config, string Folder), MailboxCheckpoint> checkpoints = [];
    private readonly Dictionary<(TicketScope Scope, string Config, string Folder, string MessageId), DateTimeOffset> seen = [];

    public Task<MailboxCheckpoint?> GetCheckpointAsync(TicketScope scope, string configurationId, string folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(checkpoints.TryGetValue((scope, configurationId, folder), out var cp) ? cp : null);
    }

    public Task SaveCheckpointAsync(TicketScope scope, string configurationId, string folder, MailboxCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(checkpoint);
        lock (gate)
            checkpoints[(scope, configurationId, folder)] = checkpoint;
        return Task.CompletedTask;
    }

    public Task<bool> TryRecordMessageIdAsync(TicketScope scope, string configurationId, string folder, string messageId, DateTimeOffset seenAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(seen.TryAdd((scope, configurationId, folder, messageId), seenAt.ToUniversalTime()));
    }

    public Task<bool> HasSeenMessageIdAsync(TicketScope scope, string configurationId, string folder, string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult(seen.ContainsKey((scope, configurationId, folder, messageId)));
    }

    public Task<int> PruneSeenMessageIdsAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (gate)
        {
            var expired = seen.Where(kv => kv.Value < olderThan).Take(limit).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
                seen.Remove(key);
            return Task.FromResult(expired.Count);
        }
    }
}
