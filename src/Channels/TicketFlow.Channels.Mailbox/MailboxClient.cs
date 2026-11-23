using System.Buffers;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace TicketFlow.Channels.Mailbox;

/// <summary>Folder identity returned when opening an IMAP folder.</summary>
public sealed record MailboxFolderState(uint UidValidity, uint UidNext);

/// <summary>One fetched raw MIME email (or a size-exceeded marker when the message is larger than configured bounds).</summary>
public sealed record MailboxFetchedMessage(uint Uid, long SizeBytes, byte[]? RawMimeBytes, bool TooLarge = false);

/// <summary>Lightweight IMAP message summary (UID, RFC822 size, and optional envelope Message-Id) fetched in batch before downloading MIME bodies.</summary>
public sealed record MailboxMessageSummary(uint Uid, long SizeBytes, string? MessageId);

/// <summary>Testable IMAP client session for one polled folder.</summary>
public interface IMailboxClient : IAsyncDisposable
{
    Task<MailboxFolderState> OpenFolderAsync(string folder, CancellationToken cancellationToken = default);

    /// <summary>Returns UIDs strictly greater than <paramref name="afterUid"/> in ascending UID order, bounded to <paramref name="maxCount"/>.</summary>
    Task<IReadOnlyList<uint>> ListUidsAfterAsync(uint afterUid, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Fetches raw MIME bytes for <paramref name="uid"/> if its size does not exceed <paramref name="maxBytes"/>.</summary>
    Task<MailboxFetchedMessage?> FetchMessageAsync(uint uid, int maxBytes, CancellationToken cancellationToken = default);

    /// <summary>Optionally fetches batch metadata summaries (size and envelope Message-Id) for <paramref name="uids"/> in one round-trip.</summary>
    Task<IReadOnlyList<MailboxMessageSummary>?> FetchSummariesAsync(IReadOnlyList<uint> uids, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MailboxMessageSummary>?>(null);
}

/// <summary>Creates connected, authenticated <see cref="IMailboxClient"/> sessions for a mailbox endpoint.</summary>
public interface IMailboxClientFactory
{
    Task<IMailboxClient> ConnectAsync(MailboxEndpointConfig config, MailboxCredentials credentials, CancellationToken cancellationToken = default);
}

/// <summary>Production <see cref="IMailboxClientFactory"/> backed by MailKit <see cref="ImapClient"/>.</summary>
public sealed class MailKitMailboxClientFactory : IMailboxClientFactory
{
    public async Task<IMailboxClient> ConnectAsync(MailboxEndpointConfig config, MailboxCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(credentials);
        var client = new ImapClient { Timeout = (int)config.ConnectTimeout.TotalMilliseconds };
        try
        {
            var socketOptions = config.RequireTls
                ? (config.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls)
                : SecureSocketOptions.Auto;
            await client.ConnectAsync(config.Host, config.Port, socketOptions, cancellationToken).ConfigureAwait(false);
            await client.AuthenticateAsync(credentials.Username, credentials.Password, cancellationToken).ConfigureAwait(false);
            return new MailKitMailboxClient(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed class MailKitMailboxClient(ImapClient client) : IMailboxClient
{
    private IMailFolder? folder;
    private Dictionary<uint, long>? cachedSizes;

    public async Task<MailboxFolderState> OpenFolderAsync(string folderName, CancellationToken cancellationToken = default)
    {
        folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderName, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        cachedSizes = null;
        return new MailboxFolderState(folder.UidValidity, folder.UidNext?.Id ?? 0);
    }

    public async Task<IReadOnlyList<uint>> ListUidsAfterAsync(uint afterUid, int maxCount, CancellationToken cancellationToken = default)
    {
        var open = folder ?? throw new InvalidOperationException("Call OpenFolderAsync before listing UIDs.");
        if (afterUid == uint.MaxValue)
            return [];
        var start = new UniqueId(afterUid + 1);
        var range = new UniqueIdRange(start, UniqueId.MaxValue);
        var uids = await open.SearchAsync(SearchQuery.Uids(range), cancellationToken).ConfigureAwait(false);
        return [.. uids.Select(u => u.Id).Where(id => id > afterUid).Order().Take(maxCount)];
    }

    public async Task<IReadOnlyList<MailboxMessageSummary>?> FetchSummariesAsync(IReadOnlyList<uint> uids, CancellationToken cancellationToken = default)
    {
        var open = folder ?? throw new InvalidOperationException("Call OpenFolderAsync before fetching summaries.");
        if (uids.Count == 0)
            return [];
        var uniqueIds = new UniqueId[uids.Count];
        for (var i = 0; i < uids.Count; i++)
            uniqueIds[i] = new UniqueId(uids[i]);
        var summaries = await open.FetchAsync(uniqueIds, MessageSummaryItems.Size | MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, cancellationToken).ConfigureAwait(false);
        cachedSizes = new Dictionary<uint, long>(summaries.Count);
        var results = new List<MailboxMessageSummary>(summaries.Count);
        for (var i = 0; i < summaries.Count; i++)
        {
            var s = summaries[i];
            var size = s.Size ?? 0;
            cachedSizes[s.UniqueId.Id] = size;
            results.Add(new MailboxMessageSummary(s.UniqueId.Id, size, s.Envelope?.MessageId));
        }
        return results;
    }

    public async Task<MailboxFetchedMessage?> FetchMessageAsync(uint uid, int maxBytes, CancellationToken cancellationToken = default)
    {
        var open = folder ?? throw new InvalidOperationException("Call OpenFolderAsync before fetching messages.");
        var uniqueId = new UniqueId(uid);
        long size;
        if (cachedSizes is not null && cachedSizes.TryGetValue(uid, out var knownSize))
            size = knownSize;
        else
        {
            var summaries = await open.FetchAsync([uniqueId], MessageSummaryItems.Size | MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);
            var summary = summaries.FirstOrDefault();
            if (summary is null)
                return null;
            size = summary.Size ?? 0;
        }
        if (size > maxBytes)
            return new MailboxFetchedMessage(uid, size, null, TooLarge: true);
        await using var rawStream = await open.GetStreamAsync(uniqueId, cancellationToken).ConfigureAwait(false);
        using var ms = size is > 0 and <= int.MaxValue ? new MemoryStream((int)size) : new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long total = 0;
            int read;
            while ((read = await rawStream.ReadAsync(buffer.AsMemory(0, 8192), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    return new MailboxFetchedMessage(uid, total, null, TooLarge: true);
                ms.Write(buffer, 0, read);
            }
            return new MailboxFetchedMessage(uid, total, ms.ToArray(), TooLarge: false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(true).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            client.Dispose();
        }
    }
}
