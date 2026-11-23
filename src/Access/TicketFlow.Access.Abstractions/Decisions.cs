using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>The action is allowed without redaction.</summary>
public sealed record AccessAllowed(Permission Permission, bool UsedBreakGlass = false);

/// <summary>The action is denied.</summary>
public sealed record AccessDenied(Permission Permission, AccessDenialReason Reason);

/// <summary>The action is allowed, but <see cref="Fields"/> must be redacted server-side. Fields are ordered by path (ordinal).</summary>
public sealed record AccessMasked
{
    public AccessMasked(Permission permission, IEnumerable<MaskedField> fields, bool usedBreakGlass = false)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Permission = permission;
        var list = fields is ICollection<MaskedField> col ? new List<MaskedField>(col.Count) : new List<MaskedField>();
        var sorted = true;
        MaskedField? previous = null;
        foreach (var field in fields)
        {
            if (previous is not null && previous.Field.CompareTo(field.Field) > 0)
                sorted = false;
            list.Add(field);
            previous = field;
        }
        if (list.Count == 0)
            throw new ArgumentException("A masked decision lists at least one field.", nameof(fields));
        if (!sorted)
            list.Sort(static (left, right) => left.Field.CompareTo(right.Field));
        Fields = list.AsReadOnly();
        UsedBreakGlass = usedBreakGlass;
    }

    public Permission Permission { get; }
    public IReadOnlyList<MaskedField> Fields { get; }
    public bool UsedBreakGlass { get; }

    public bool Equals(AccessMasked? other) =>
        other is not null && Permission == other.Permission && UsedBreakGlass == other.UsedBreakGlass && Fields.SequenceEqual(other.Fields);

    public override int GetHashCode() => HashCode.Combine(Permission, Fields.Count, UsedBreakGlass);
}

/// <summary>A field to redact. <see cref="Revealable"/> means the principal may call <see cref="IAccessEvaluator.RevealAsync"/>.</summary>
public sealed record MaskedField(FieldPath Field, bool Revealable);

/// <summary>
/// The decision for a point check. Callers switch over every case without a discard arm; <see cref="AccessMasked"/> permits the
/// action but requires redaction.
/// </summary>
public union AccessDecision(AccessAllowed, AccessDenied, AccessMasked)
{
    /// <summary>True for <see cref="AccessAllowed"/> and <see cref="AccessMasked"/>.</summary>
    public bool IsPermitted => this switch
    {
        AccessAllowed => true,
        AccessDenied => false,
        AccessMasked => true,
    };
}

public enum AccessDenialReason
{
    /// <summary>The permission is uninitialized or not in the catalog.</summary>
    UnknownPermission = 1,

    /// <summary>The principal's scope differs from the resource or query scope.</summary>
    ScopeMismatch = 2,

    /// <summary>The principal kind cannot hold grants (automation and system principals) or is uninitialized.</summary>
    UnsupportedPrincipal = 3,

    /// <summary>No valid allow grant applies with a true condition.</summary>
    NoMatchingGrant = 4,

    /// <summary>An applicable deny grant's condition was true or unknown.</summary>
    ExplicitDeny = 5,

    /// <summary>A query plan would exceed <see cref="AccessFilter.MaximumNodes"/>; the planner returned deny-all.</summary>
    FilterLimitExceeded = 6,
}

/// <summary>Request to reveal one masked field. The value itself never passes through the access engine.</summary>
public sealed record RevealRequest
{
    public RevealRequest(PrincipalRef principal, AccessResource resource, Permission readPermission, FieldPath field, string? justification = null)
    {
        if (principal.Value is null)
            throw new ArgumentException("A principal is required.", nameof(principal));
        ArgumentNullException.ThrowIfNull(resource);
        if (!field.IsInitialized)
            throw new ArgumentException("A field path is required.", nameof(field));
        Principal = principal;
        Resource = resource;
        ReadPermission = readPermission;
        Field = field;
        Justification = justification is null ? null : AccessText.RequireText(justification, AccessGrant.MaximumJustificationLength, nameof(justification));
    }

    public PrincipalRef Principal { get; }
    public AccessResource Resource { get; }

    /// <summary>The read permission whose masking the reveal lifts, for example <c>contact:read</c>.</summary>
    public Permission ReadPermission { get; }

    public FieldPath Field { get; }
    public string? Justification { get; }
}

/// <summary>The caller may show the field value. <see cref="AuditEventId"/> identifies the recorded reveal.</summary>
public sealed record RevealGranted(FieldPath Field, Guid AuditEventId);

/// <summary>The reveal was refused; the refusal is also audited.</summary>
public sealed record RevealRefused(FieldPath Field, RevealRefusal Reason, Guid AuditEventId);

public enum RevealRefusal
{
    /// <summary>The read permission itself is denied.</summary>
    ReadDenied = 1,

    /// <summary>No field policy for the field applies to the read permission.</summary>
    NoPolicy = 2,

    /// <summary>The field policy has no reveal permission.</summary>
    NotRevealable = 3,

    /// <summary>The policy's reveal permission is denied.</summary>
    RevealDenied = 4,
}

/// <summary>Outcome of a reveal request. Switch over every case without a discard arm.</summary>
public union RevealResult(RevealGranted, RevealRefused);
