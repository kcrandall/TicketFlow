using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace TicketFlow.Channels.Mailbox;

/// <summary>Folder identity returned when opening an IMAP folder.</summary>
public sealed record MailboxFolderState(uint UidValidity, uint UidNext);

/// <summary>One fetched raw MIME email (or a size-exceeded marker when the message is larger than configured bounds).</summary>
public sealed record MailboxFetchedMessage(uint Uid, long SizeBytes, byte[]? RawMimeBytes, bool TooLarge = false);

/// <summary>Testable IMAP client session for one polled folder.</summary>
public interface IMailboxClient : IAsyncDisposable
{
    Task<MailboxFolderState> OpenFolderAsync(string folder, CancellationToken cancellationToken = default);

    /// <summary>Returns UIDs strictly greater than <paramref name="afterUid"/> in ascending UID order, bounded to <paramref name="maxCount"/>.</summary>
    Task<IReadOnlyList<uint>> ListUidsAfterAsync(uint afterUid, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Fetches raw MIME bytes for <paramref name="uid"/> if its size does not exceed <paramref name="maxBytes"/>.</summary>
    Task<MailboxFetchedMessage?> FetchMessageAsync(uint uid, int maxBytes, CancellationToken cancellationToken = default);
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

    public async Task<MailboxFolderState> OpenFolderAsync(string folderName, CancellationToken cancellationToken = default)
    {
        folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderName, cancellationToken).ConfigureAwait(false);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
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

    public async Task<MailboxFetchedMessage?> FetchMessageAsync(uint uid, int maxBytes, CancellationToken cancellationToken = default)
    {
        var open = folder ?? throw new InvalidOperationException("Call OpenFolderAsync before fetching messages.");
        var uniqueId = new UniqueId(uid);
        var summaries = await open.FetchAsync([uniqueId], MessageSummaryItems.Size | MessageSummaryItems.UniqueId, cancellationToken).ConfigureAwait(false);
        var summary = summaries.FirstOrDefault();
        if (summary is null)
            return null;
        var size = summary.Size ?? 0;
        if (size > maxBytes)
            return new MailboxFetchedMessage(uid, size, null, TooLarge: true);
        await using var rawStream = await open.GetStreamAsync(uniqueId, cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await rawStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return new MailboxFetchedMessage(uid, total, null, TooLarge: true);
            ms.Write(buffer, 0, read);
        }
        return new MailboxFetchedMessage(uid, total, ms.ToArray(), TooLarge: false);
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
