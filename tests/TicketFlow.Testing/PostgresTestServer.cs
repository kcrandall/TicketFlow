using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace TicketFlow.Testing;

/// <summary>
/// Resolves the configured PostgreSQL test server and waits, once per process, until it answers a query.
/// A started container is not a ready server: published ports accept connections before PostgreSQL listens.
/// </summary>
public static class PostgresTestServer
{
    private static readonly TimeSpan ReadinessDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, OnceTask> Readiness = new(StringComparer.Ordinal);

    /// <summary>Returns the configured connection string once the server answers, or marks the test inconclusive.</summary>
    public static async Task<string> RequireAsync()
    {
        var connection = Environment.GetEnvironmentVariable("TICKETFLOW_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
        {
            // CI and scripts/test-postgres.sh set TICKETFLOW_REQUIRE_POSTGRES=1 so a missing server can never pass as skipped.
            if (IsRequired)
                Assert.Fail("TICKETFLOW_REQUIRE_POSTGRES=1 but TICKETFLOW_TEST_POSTGRES is not set.");
            Assert.Inconclusive("Set TICKETFLOW_TEST_POSTGRES to run actual PostgreSQL contracts.");
        }
        await Readiness.GetOrAdd(connection!, static value => new OnceTask(() => WaitUntilReadyAsync(value))).RunAsync();
        return connection!;
    }

    private sealed class OnceTask(Func<Task> factory)
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;

        public async Task RunAsync()
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
            {
                await completion.Task;
                return;
            }
            try
            {
                await factory();
                completion.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                completion.TrySetCanceled(ex.CancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                throw;
            }
        }
    }

    /// <summary>True when <c>TICKETFLOW_REQUIRE_POSTGRES=1</c>: PostgreSQL tests fail instead of being inconclusive.</summary>
    public static bool IsRequired => string.Equals(Environment.GetEnvironmentVariable("TICKETFLOW_REQUIRE_POSTGRES"), "1", StringComparison.Ordinal);

    /// <summary>Synchronous form for fixtures that construct data sources outside async code.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "Intentional synchronous entrypoint for non-async fixture constructors.")]
    public static string Require() => RequireAsync().GetAwaiter().GetResult();

    /// <summary>Retries transient connection failures until <paramref name="connection"/> answers a query, within a bounded deadline.</summary>
    public static async Task WaitUntilReadyAsync(string connection)
    {
        var probe = new NpgsqlConnectionStringBuilder(connection) { Pooling = false, Timeout = 5, CommandTimeout = 5 }.ConnectionString;
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await using var session = new NpgsqlConnection(probe);
                await session.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", session);
                await command.ExecuteScalarAsync();
                return;
            }
            // Refused, reset, timed-out and "starting up" (57P03) connections are retried. Server-reported failures such as
            // bad credentials or a missing database fail at once; an unreachable host waits for the deadline, once per process.
            catch (NpgsqlException error) when (error.IsTransient && elapsed.Elapsed < ReadinessDeadline)
            {
                await Task.Delay(RetryDelay);
            }
        }
    }
}
