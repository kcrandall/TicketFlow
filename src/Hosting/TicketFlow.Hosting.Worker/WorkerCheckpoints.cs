using TicketFlow.Core;

namespace TicketFlow.Hosting.Worker;

/// <summary>
/// Durable named per-scope positions for change-log followers (for example the contact-merge catch-up). Contract: scope-confined;
/// <see cref="SetAsync"/> is compare-and-set (<c>expected</c> <see langword="null"/> means absent) and returns false on a lost race; names are
/// 1-64 characters of a-z, 0-9, '-', '_' or '.'.
/// </summary>
public interface IWorkerCheckpointStore
{
    Task<long?> GetAsync(TicketScope scope, string name, CancellationToken cancellationToken = default);

    Task<bool> SetAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default);
}

public static class WorkerCheckpointNames
{
    public const int MaximumLength = 64;

    public static string Require(string name, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);
        if (name.Length is 0 or > MaximumLength || !name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
            throw new ArgumentException("Checkpoint names are 1-64 characters of a-z, 0-9, '-', '_' or '.'.", parameterName);
        return name;
    }
}

/// <summary>Development-only checkpoint store: process-local, bounded, lost on restart (catch-up then replays idempotently from zero).</summary>
public sealed class InMemoryWorkerCheckpointStore(int maximumEntries = 100_000) : IWorkerCheckpointStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(TicketScope Scope, string Name), long> values = [];

    public Task<long?> GetAsync(TicketScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        WorkerCheckpointNames.Require(name, nameof(name));
        lock (gate)
            return Task.FromResult(values.TryGetValue((scope, name), out var value) ? value : (long?)null);
    }

    public Task<bool> SetAsync(TicketScope scope, string name, long value, long? expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        WorkerCheckpointNames.Require(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        lock (gate)
        {
            var exists = values.TryGetValue((scope, name), out var current);
            if (exists ? expected != current : expected is not null)
                return Task.FromResult(false);
            if (!exists && values.Count >= maximumEntries)
                throw new InvalidOperationException("The in-memory checkpoint store is full.");
            values[(scope, name)] = value;
            return Task.FromResult(true);
        }
    }
}
