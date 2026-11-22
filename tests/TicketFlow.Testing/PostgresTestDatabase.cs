using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace TicketFlow.Testing;

/// <summary>Owns one disposable schema and independent, bounded client pools.</summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly NpgsqlDataSource administration;
    private readonly List<NpgsqlDataSource> clients = [];
    private bool disposed;
    public string Schema { get; }
    public string ConnectionString { get; }
    public NpgsqlDataSource First { get; }
    public NpgsqlDataSource Second { get; }

    private PostgresTestDatabase(string connection, string prefix, int maximumPoolSize)
    {
        if (prefix.Length is < 1 or > 31 || prefix.Any(character => character is not (>= 'a' and <= 'z') and not '_'))
            throw new ArgumentException("Use a short lowercase schema prefix.", nameof(prefix));
        if (maximumPoolSize is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(maximumPoolSize));
        Schema = prefix + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(connection) { MinPoolSize = 0, MaxPoolSize = maximumPoolSize, Timeout = 10, CommandTimeout = 15 };
        administration = NpgsqlDataSource.Create(settings.ConnectionString);
        settings.SearchPath = Schema;
        settings.ApplicationName = Schema;
        ConnectionString = settings.ConnectionString;
        First = OpenClient(); Second = OpenClient();
    }

    public static async Task<PostgresTestDatabase> CreateAsync(string prefix = "tf_test_", int maximumPoolSize = 8)
    {
        var database = new PostgresTestDatabase(await PostgresTestServer.RequireAsync(), prefix, maximumPoolSize);
        try
        {
            await using var create = database.administration.CreateCommand("CREATE SCHEMA " + database.Schema);
            await create.ExecuteNonQueryAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs feature-owned initialization and cleans up if any initializer fails.</summary>
    public static async Task<T> CreateAsync<T>(string prefix, Func<PostgresTestDatabase, Task<T>> initialize, int maximumPoolSize = 8)
    {
        var database = await CreateAsync(prefix, maximumPoolSize);
        try { return await initialize(database); }
        catch { await database.DisposeAsync(); throw; }
    }

    public NpgsqlDataSource OpenClient()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var source = NpgsqlDataSource.Create(ConnectionString);
        clients.Add(source);
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            foreach (var source in clients)
                await source.DisposeAsync();
            await using var drop = administration.CreateCommand("DROP SCHEMA IF EXISTS " + Schema + " CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
        finally { await administration.DisposeAsync(); }
    }
}
