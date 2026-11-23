using System.Collections.Frozen;
using System.Collections.ObjectModel;
using TicketFlow.Core;

namespace TicketFlow.Access;

/// <summary>
/// The attribute snapshot of the resource being authorized. <see cref="TeamId"/>, <see cref="InboxId"/> and
/// <see cref="AssigneeId"/> are authoritative (<see langword="null"/> means none). <see cref="Labels"/> is <see langword="null"/>
/// when the caller did not load labels, and an attribute key absent from <see cref="Attributes"/> is unknown; conditions over
/// unknown data fail closed. Attribute values may be personal data and are never logged or audited.
/// </summary>
public sealed record AccessResource
{
    public const int MaximumLabels = 256;
    public const int MaximumAttributes = 128;
    public const int MaximumAttributeKeyLength = 64;
    public const int MaximumAttributeValueLength = 4096;

    public AccessResource(
        TicketScope scope,
        string? resourceId = null,
        TeamId? team = null,
        InboxId? inbox = null,
        UserId? assignee = null,
        IEnumerable<LabelId>? labels = null,
        IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (team == default(TeamId) || inbox == default(InboxId) || assignee == default(UserId))
            throw new ArgumentException("Resource team, inbox and assignee identifiers must be initialized when supplied.");
        Scope = scope;
        ResourceId = resourceId is null ? null : TicketScope.RequireIdentifier(resourceId, nameof(resourceId));
        TeamId = team;
        InboxId = inbox;
        AssigneeId = assignee;
        Labels = labels is null ? null : SnapshotLabels(labels);
        Attributes = SnapshotAttributes(attributes);
    }

    public TicketScope Scope { get; }

    /// <summary>Optional opaque identifier recorded in audit events (for example a conversation ID).</summary>
    public string? ResourceId { get; }

    public TeamId? TeamId { get; }
    public InboxId? InboxId { get; }
    public UserId? AssigneeId { get; }

    /// <summary>Labels, or <see langword="null"/> when not supplied (unknown).</summary>
    public IReadOnlySet<LabelId>? Labels { get; }

    /// <summary>Supplied attributes; missing keys are unknown.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }

    public bool Equals(AccessResource? other)
    {
        if (other is null || Scope != other.Scope || ResourceId != other.ResourceId || TeamId != other.TeamId || InboxId != other.InboxId || AssigneeId != other.AssigneeId)
            return false;
        if (Labels is null ? other.Labels is not null : other.Labels is null || !Labels.SetEquals(other.Labels))
            return false;
        if (Attributes.Count != other.Attributes.Count)
            return false;
        foreach (var pair in Attributes)
            if (!other.Attributes.TryGetValue(pair.Key, out var value) || value != pair.Value)
                return false;
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Scope, ResourceId, TeamId, InboxId, AssigneeId, Labels?.Count, Attributes.Count);

    public override string ToString() => "AccessResource { <redacted> }";

    private static IReadOnlySet<LabelId> SnapshotLabels(IEnumerable<LabelId> labels)
    {
        var capacity = labels.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumLabels) : 0;
        if (count == 0 && labels is ICollection<LabelId> or IReadOnlyCollection<LabelId>)
            return FrozenSet<LabelId>.Empty;
        var snapshot = capacity > 0 ? new HashSet<LabelId>(capacity) : new HashSet<LabelId>();
        foreach (var label in labels)
        {
            if (label == default)
                throw new ArgumentException("Labels cannot contain uninitialized identifiers.", nameof(labels));
            if (snapshot.Add(label) && snapshot.Count > MaximumLabels)
                throw new ArgumentException($"A resource can carry at most {MaximumLabels} labels.", nameof(labels));
        }
        return snapshot.Count == 0 ? FrozenSet<LabelId>.Empty : new ReadOnlySet<LabelId>(snapshot);
    }

    private static IReadOnlyDictionary<string, string> SnapshotAttributes(IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        if (attributes is null)
            return FrozenDictionary<string, string>.Empty;
        var capacity = attributes.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumAttributes) : 0;
        if (count == 0 && attributes is ICollection<KeyValuePair<string, string>> or IReadOnlyCollection<KeyValuePair<string, string>>)
            return FrozenDictionary<string, string>.Empty;
        var snapshot = capacity > 0 ? new Dictionary<string, string>(capacity, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            AccessText.RequireToken(key, MaximumAttributeKeyLength, nameof(attributes), "Attribute keys");
            ArgumentNullException.ThrowIfNull(value, nameof(attributes));
            if (value.Length > MaximumAttributeValueLength)
                throw new ArgumentException($"Attribute values can contain at most {MaximumAttributeValueLength} characters.", nameof(attributes));
            if (!snapshot.TryAdd(key, value))
                throw new ArgumentException("Attribute keys must be unique.", nameof(attributes));
            if (snapshot.Count > MaximumAttributes)
                throw new ArgumentException($"A resource can carry at most {MaximumAttributes} attributes.", nameof(attributes));
        }
        return snapshot.Count == 0 ? FrozenDictionary<string, string>.Empty : snapshot.AsReadOnly();
    }
}

/// <summary>A point authorization request.</summary>
public sealed record AccessRequest
{
    public AccessRequest(PrincipalRef principal, Permission permission, AccessResource resource)
    {
        if (principal.Value is null)
            throw new ArgumentException("A principal is required.", nameof(principal));
        ArgumentNullException.ThrowIfNull(resource);
        Principal = principal;
        Permission = permission;
        Resource = resource;
    }

    public PrincipalRef Principal { get; }

    /// <summary>The requested permission. Uninitialized or uncataloged permissions are denied.</summary>
    public Permission Permission { get; }

    public AccessResource Resource { get; }
}

/// <summary>A list/search authorization request within one workspace scope.</summary>
public sealed record AccessQuery
{
    public AccessQuery(PrincipalRef principal, Permission permission, TicketScope scope)
    {
        if (principal.Value is null)
            throw new ArgumentException("A principal is required.", nameof(principal));
        ArgumentNullException.ThrowIfNull(scope);
        Principal = principal;
        Permission = permission;
        Scope = scope;
    }

    public PrincipalRef Principal { get; }
    public Permission Permission { get; }
    public TicketScope Scope { get; }
}

/// <summary>
/// A maskable field path such as <c>contact.email</c> or <c>attribute.ssn</c>: 2-4 dot-separated segments of a-z, 0-9, '-' or
/// '_', each starting with a letter, at most 160 characters.
/// </summary>
public readonly record struct FieldPath : IComparable<FieldPath>
{
    public const int MaximumLength = 160;

    private readonly string? value;

    public FieldPath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsValid(value))
            throw new ArgumentException("Field paths must be 2-4 dot-separated lowercase segments of at most 160 characters.", nameof(value));
        this.value = value;
    }

    /// <exception cref="InvalidOperationException">The path is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The FieldPath is uninitialized.");

    public bool IsInitialized => value is not null;

    public int CompareTo(FieldPath other) => string.CompareOrdinal(value, other.value);

    public override string ToString() => value ?? string.Empty;

    private static bool IsValid(string value)
    {
        if (value.Length > MaximumLength)
            return false;
        var span = value.AsSpan();
        var segments = 0;
        foreach (var range in span.Split('.'))
        {
            segments++;
            if (segments > 4 || !AccessText.IsFieldSegment(span[range], 64))
                return false;
        }
        return segments >= 2;
    }
}
