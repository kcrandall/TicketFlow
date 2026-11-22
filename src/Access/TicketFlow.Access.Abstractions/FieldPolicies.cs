using System.Collections.Frozen;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// Masks <see cref="Field"/> in results authorized by any of <see cref="AppliesTo"/>. The field is unmasked only when an applicable
/// allow grant for the requested permission uses one of <see cref="UnmaskedRoles"/>. A masked field can be revealed one at a time
/// when <see cref="RevealPermission"/> is set and allowed; every reveal is audited.
/// </summary>
public sealed record AccessFieldPolicy
{
    public const int MaximumPermissions = 32;
    public const int MaximumRoles = 64;

    public AccessFieldPolicy(FieldPath field, IEnumerable<Permission> appliesTo, IEnumerable<RoleId>? unmaskedRoles = null, Permission? revealPermission = null)
    {
        if (!field.IsInitialized)
            throw new ArgumentException("A field path is required.", nameof(field));
        ArgumentNullException.ThrowIfNull(appliesTo);
        if (revealPermission is { IsInitialized: false })
            throw new ArgumentException("The reveal permission must be initialized when supplied.", nameof(revealPermission));
        Field = field;
        AppliesTo = Snapshot(appliesTo, MaximumPermissions, permission => permission.IsInitialized, nameof(appliesTo));
        if (AppliesTo.Count == 0)
            throw new ArgumentException("A field policy applies to at least one permission.", nameof(appliesTo));
        UnmaskedRoles = Snapshot(unmaskedRoles ?? [], MaximumRoles, role => role.IsInitialized, nameof(unmaskedRoles));
        RevealPermission = revealPermission;
    }

    public FieldPath Field { get; }
    public IReadOnlySet<Permission> AppliesTo { get; }
    public IReadOnlySet<RoleId> UnmaskedRoles { get; }
    public Permission? RevealPermission { get; }

    public bool Equals(AccessFieldPolicy? other) =>
        other is not null && Field == other.Field && RevealPermission == other.RevealPermission
        && AppliesTo.SetEquals(other.AppliesTo) && UnmaskedRoles.SetEquals(other.UnmaskedRoles);

    public override int GetHashCode() => HashCode.Combine(Field, AppliesTo.Count, UnmaskedRoles.Count, RevealPermission);

    private static FrozenSet<T> Snapshot<T>(IEnumerable<T> values, int maximum, Func<T, bool> valid, string parameterName)
    {
        var snapshot = new HashSet<T>();
        foreach (var value in values)
        {
            if (!valid(value))
                throw new ArgumentException("Values must be initialized.", parameterName);
            if (snapshot.Add(value) && snapshot.Count > maximum)
                throw new ArgumentException($"At most {maximum} values are allowed.", parameterName);
        }
        return snapshot.ToFrozenSet();
    }
}

/// <summary>A field policy with its workspace scope and revision (first revision is 1).</summary>
public sealed record StoredAccessFieldPolicy(TicketScope Scope, AccessFieldPolicy Policy, long Revision);
