using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow.Access;

/// <summary>The closed set of permissions the access engine recognizes. Unknown permissions are always denied.</summary>
public interface IPermissionCatalog
{
    /// <summary>Definitions ordered by permission (ordinal).</summary>
    IReadOnlyList<PermissionDefinition> Definitions { get; }

    bool Contains(Permission permission);

    PermissionDefinition? Find(Permission permission);
}

/// <summary>
/// Immutable permission catalog. Registering an identical definition more than once is idempotent; registering the same permission
/// with a different sensitivity or description throws, naming the first conflicting permission in ordinal order.
/// </summary>
public sealed class PermissionCatalog : IPermissionCatalog
{
    /// <summary>Maximum number of distinct permissions.</summary>
    public const int MaximumPermissions = 4096;

    private readonly FrozenDictionary<Permission, PermissionDefinition> definitions;

    public PermissionCatalog(IEnumerable<PermissionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var merged = new Dictionary<Permission, PermissionDefinition>();
        var conflicts = new SortedSet<Permission>();
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition, nameof(definitions));
            if (merged.TryGetValue(definition.Permission, out var existing))
            {
                if (existing != definition)
                    conflicts.Add(definition.Permission);
                continue;
            }
            merged.Add(definition.Permission, definition);
            if (merged.Count > MaximumPermissions)
                throw new ArgumentException($"A catalog can contain at most {MaximumPermissions} permissions.", nameof(definitions));
        }
        if (conflicts.Count > 0)
            throw new ArgumentException($"Permission '{conflicts.Min}' was registered with conflicting definitions.", nameof(definitions));

        this.definitions = merged.ToFrozenDictionary();
        Definitions = [.. merged.Values.OrderBy(definition => definition.Permission)];
    }

    /// <summary>A catalog containing the built-in <see cref="AccessPermissions"/> plus <paramref name="additional"/>.</summary>
    public static PermissionCatalog Create(IEnumerable<PermissionDefinition>? additional = null) =>
        new(additional is null ? AccessPermissions.Definitions : AccessPermissions.Definitions.Concat(additional));

    public IReadOnlyList<PermissionDefinition> Definitions { get; }

    public bool Contains(Permission permission) => permission.IsInitialized && definitions.ContainsKey(permission);

    public PermissionDefinition? Find(Permission permission) =>
        permission.IsInitialized ? definitions.GetValueOrDefault(permission) : null;
}

/// <summary>A feature package's permission registration, collected into the catalog when it is first resolved.</summary>
public sealed record AccessPermissionRegistration(IReadOnlyList<PermissionDefinition> Definitions);

public static class AccessPermissionServiceCollectionExtensions
{
    /// <summary>Registers feature-owned permissions. Safe to call from several packages; identical definitions are merged.</summary>
    public static IServiceCollection AddAccessPermissions(this IServiceCollection services, params IEnumerable<PermissionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(definitions);
        IReadOnlyList<PermissionDefinition> snapshot = [.. definitions];
        foreach (var definition in snapshot)
            ArgumentNullException.ThrowIfNull(definition, nameof(definitions));
        services.AddSingleton(new AccessPermissionRegistration(snapshot));
        return services;
    }
}
