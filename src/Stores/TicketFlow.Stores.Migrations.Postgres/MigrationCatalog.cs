using System.Security.Cryptography;
using System.Text;

namespace TicketFlow.Stores.Migrations.Postgres;

/// <summary>Trusted feature-owned SQL, executed in one PostgreSQL transaction. SQL is never logged by this package.</summary>
public sealed record PostgresMigration
{
    public PostgresMigration(int version, string id, string sql)
    {
        if (version is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(version));
        MigrationIdentifiers.Validate(id, nameof(id));
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 1_000_000)
            throw new ArgumentException("A migration requires bounded SQL.", nameof(sql));
        Version = version; Id = id; Sql = sql;
        Checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{version}\n{id}\n{sql}")));
    }
    public int Version { get; }
    public string Id { get; }
    public string Sql { get; }
    public string Checksum { get; }
    public override string ToString() => $"PostgresMigration {{ Version = {Version}, Id = {Id}, Checksum = {Checksum} }}";
}

/// <summary>The feature supplies a trusted read-only query returning one Boolean, verifying an already initialized schema at this exact version.</summary>
public sealed record PostgresSchemaBaseline
{
    public PostgresSchemaBaseline(int version, string validationSql)
    {
        if (version is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(version));
        if (string.IsNullOrWhiteSpace(validationSql) || validationSql.Length > 1_000_000)
            throw new ArgumentException("A baseline requires bounded validation SQL.", nameof(validationSql));
        Version = version; ValidationSql = validationSql;
    }
    public int Version { get; }
    public string ValidationSql { get; }
    public override string ToString() => $"PostgresSchemaBaseline {{ Version = {Version} }}";
}

/// <summary>Feature-owned immutable contiguous history. Version one is the fresh schema; baseline adoption never executes its migration SQL.</summary>
public sealed class PostgresMigrationCatalog
{
    public PostgresMigrationCatalog(string component, IEnumerable<PostgresMigration> migrations, IEnumerable<PostgresSchemaBaseline>? baselines = null)
    {
        MigrationIdentifiers.Validate(component, nameof(component));
        ArgumentNullException.ThrowIfNull(migrations);
        var steps = new List<PostgresMigration>();
        foreach (var step in migrations)
        {
            if (step is null || steps.Count == 1000 || step.Version != steps.Count + 1)
                throw new ArgumentException("Migration versions must be contiguous from one, ordered and bounded.", nameof(migrations));
            steps.Add(step);
        }
        if (steps.Count == 0 || steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != steps.Count)
            throw new ArgumentException("Migrations require unique identities and a nonempty history.", nameof(migrations));
        var adoption = new List<PostgresSchemaBaseline>();
        foreach (var baseline in baselines ?? [])
        {
            if (baseline is null || adoption.Count == 1000 || baseline.Version > steps.Count || adoption.Any(item => item.Version == baseline.Version))
                throw new ArgumentException("Baselines must reference unique supported versions.", nameof(baselines));
            adoption.Add(baseline);
        }
        Component = component;
        Migrations = Array.AsReadOnly(steps.ToArray());
        Baselines = Array.AsReadOnly(adoption.ToArray());
    }
    public string Component { get; }
    public IReadOnlyList<PostgresMigration> Migrations { get; }
    public IReadOnlyList<PostgresSchemaBaseline> Baselines { get; }
}

public sealed record AppliedPostgresMigration(int Version, string Id, string Checksum, bool AdoptedBaseline, DateTimeOffset AppliedAtUtc);
public sealed record PostgresMigrationInspection(string Component, IReadOnlyList<AppliedPostgresMigration> Applied)
{
    public int CurrentVersion => Applied.Count == 0 ? 0 : Applied[^1].Version;
}
public sealed record PostgresMigrationPlan(string Component, int CurrentVersion, int TargetVersion, IReadOnlyList<PostgresMigration> Pending);
public enum MigrationProtocolFailure { InvalidHistory, UnsupportedVersion, ChecksumMismatch, Downgrade, BaselineUnavailable, BaselineMismatch, AlreadyVersioned }
public sealed class MigrationProtocolException(MigrationProtocolFailure reason) : InvalidOperationException($"PostgreSQL migration protocol rejected the operation: {reason}.")
{
    public MigrationProtocolFailure Reason { get; } = reason;
}

public sealed record PostgresMigrationOptions
{
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    internal void Validate()
    {
        if (OperationTimeout <= TimeSpan.Zero || OperationTimeout > TimeSpan.FromMinutes(5) || LockTimeout <= TimeSpan.Zero || LockTimeout > OperationTimeout)
            throw new ArgumentException("Migration timeouts must be positive, with lock timeout no longer than an operation timeout of at most five minutes.");
    }
}

internal static class MigrationIdentifiers
{
    public static void Validate(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_')))
            throw new ArgumentException("Migration identifiers use 1–128 lowercase ASCII letters, digits, dot, underscore or hyphen.", name);
    }
}
