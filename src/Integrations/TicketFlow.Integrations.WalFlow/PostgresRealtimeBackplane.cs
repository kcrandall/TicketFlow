using System.Collections.Immutable;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace TicketFlow.Integrations.WalFlow;

/// <summary>
/// PostgreSQL <c>LISTEN</c>/<c>NOTIFY</c> implementation of <see cref="IRealtimeBackplane"/>. Uses <c>pg_notify</c> for publishing
/// and a dedicated <c>LISTEN</c> loop with automatic reconnect for receiving cross-node realtime envelopes. Call
/// <see cref="StartAsync"/> (or run via a hosted service) before expecting remote notifications.
/// </summary>
public sealed partial class PostgresRealtimeBackplane : IRealtimeBackplane, IAsyncDisposable
{
    /// <summary>PostgreSQL <c>NOTIFY</c> hard payload limit is 8,000 bytes.</summary>
    public const int PostgresNotifyLimitBytes = 7_900;

    private readonly NpgsqlDataSource source;
    private readonly WalFlowRealtimeOptions options;
    private readonly ILogger<PostgresRealtimeBackplane>? logger;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Lock startGate = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ImmutableArray<Func<string, CancellationToken, Task>> handlers = [];
    private Task? listenerLoop;
    private int disposed;

    public PostgresRealtimeBackplane(NpgsqlDataSource source, IOptions<WalFlowRealtimeOptions> options, ILogger<PostgresRealtimeBackplane>? logger = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.logger = logger;
    }

    /// <summary>Starts the background <c>LISTEN</c> connection and returns once <c>LISTEN</c> is registered on PostgreSQL.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task readyTask;
        lock (startGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            listenerLoop ??= ListenLoopAsync(ready, lifetime.Token);
            readyTask = ready.Task;
        }
        await readyTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(string envelopeJson, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        if (Encoding.UTF8.GetByteCount(envelopeJson) > PostgresNotifyLimitBytes)
        {
            if (logger is not null)
                LogSkippedNotifyBroadcast(logger, PostgresNotifyLimitBytes);
            return;
        }
        await using var command = source.CreateCommand("SELECT pg_notify(@channel, @payload)");
        command.Parameters.Add(new NpgsqlParameter<string>("channel", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = options.PostgresNotifyChannel });
        command.Parameters.Add(new NpgsqlParameter<string>("payload", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = envelopeJson });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishBatchAsync(IReadOnlyList<string> envelopes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        if (envelopes.Count == 0)
            return;
        if (envelopes.Count == 1)
        {
            await PublishAsync(envelopes[0], cancellationToken).ConfigureAwait(false);
            return;
        }
        var valid = new List<string>(envelopes.Count);
        for (var i = 0; i < envelopes.Count; i++)
        {
            var json = envelopes[i] ?? throw new ArgumentNullException(nameof(envelopes));
            if (Encoding.UTF8.GetByteCount(json) > PostgresNotifyLimitBytes)
            {
                if (logger is not null)
                    LogSkippedNotifyBroadcast(logger, PostgresNotifyLimitBytes);
            }
            else
                valid.Add(json);
        }
        if (valid.Count == 0)
            return;
        await using var command = source.CreateCommand("SELECT pg_notify(@channel, p) FROM unnest(@payloads) AS p");
        command.Parameters.Add(new NpgsqlParameter<string>("channel", NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = options.PostgresNotifyChannel });
        command.Parameters.Add(new NpgsqlParameter<string[]>("payloads", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { TypedValue = [.. valid] });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public IDisposable Subscribe(Func<string, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ImmutableInterlocked.Update(ref handlers, list => list.Add(handler));
        return new Subscription(this, handler);
    }

    private async Task ListenLoopAsync(TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                var queue = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(1024)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                });
                connection.Notification += (_, args) => queue.Writer.TryWrite(args.Payload);
                // Channel name is validated as a safe lowercase PG identifier in WalFlowRealtimeOptions.Validate().
                await using (var listen = new NpgsqlCommand($"LISTEN {options.PostgresNotifyChannel}", connection))
                    await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                ready.TrySetResult();

                var dispatch = DispatchQueueAsync(queue.Reader, cancellationToken);
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                        await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    queue.Writer.TryComplete();
                    await dispatch.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ready.TrySetCanceled(cancellationToken);
                return;
            }
            catch (Exception error)
            {
                if (logger is not null)
                    LogListenFailed(logger, error.GetType().Name);
                try
                {
                    await Task.Delay(options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ready.TrySetCanceled(cancellationToken);
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped PostgreSQL NOTIFY broadcast exceeding {Limit} bytes.")]
    private static partial void LogSkippedNotifyBroadcast(ILogger logger, int limit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PostgreSQL realtime LISTEN connection failed with {Error}; reconnecting.")]
    private static partial void LogListenFailed(ILogger logger, string error);

    private async Task DispatchQueueAsync(System.Threading.Channels.ChannelReader<string> reader, CancellationToken cancellationToken)
    {
        await foreach (var payload in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    await handler(payload, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (startGate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            loop = listenerLoop;
        }

        try
        {
            try
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            ready.TrySetCanceled();

            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private sealed class Subscription(PostgresRealtimeBackplane backplane, Func<string, CancellationToken, Task> handler) : IDisposable
    {
        public void Dispose() => ImmutableInterlocked.Update(ref backplane.handlers, list => list.Remove(handler));
    }
}
