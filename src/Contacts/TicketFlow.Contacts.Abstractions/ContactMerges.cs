using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>Profile fields a merge may fill on the target. Values are persisted; never renumber.</summary>
public enum ContactProfileField
{
    DisplayName = 1,
    AvatarRef = 2,
    Locale = 3,
    TimeZone = 4,
    Company = 5,
}

/// <summary>Which side wins when both contacts hold a value for the same attribute key.</summary>
public enum ContactAttributeConflict
{
    PreferTarget = 1,
    PreferSource = 2,
}

/// <summary>An attribute the merge set on the target. <see cref="Previous"/> is <see langword="null"/> when the target had none.</summary>
public sealed record ContactMergeAttributeChange
{
    public ContactMergeAttributeChange(string key, ContactAttributeValue? previous, ContactAttributeValue merged)
    {
        Key = ContactAttributeDefinition.RequireKey(key, nameof(key));
        if (previous is { Value: null } || merged.Value is null)
            throw new ArgumentException("Attribute values must be initialized.");
        Previous = previous;
        Merged = merged;
    }

    public string Key { get; }
    public ContactAttributeValue? Previous { get; }
    public ContactAttributeValue Merged { get; }

    public override string ToString() => "ContactMergeAttributeChange { <redacted> }";
}

/// <summary>
/// Everything needed to undo one merge: the source as it was before the merge plus exactly what the merge contributed to the target.
/// Unmerge restores the source and removes from the target only contributions that are still unchanged. Contains personal data.
/// </summary>
public sealed record ContactMergeRecord
{
    public ContactMergeRecord(
        Guid id,
        ContactId target,
        ContactId source,
        DateTimeOffset mergedAt,
        AccessActor actor,
        Contact sourceSnapshot,
        IEnumerable<ContactIdentityAddress> movedIdentities,
        IEnumerable<ContactIdentity> replacedIdentities,
        IEnumerable<ContactMergeAttributeChange> attributeChanges,
        IEnumerable<LabelId> addedLabels,
        IEnumerable<ContactProfileField> filledProfileFields,
        bool blockedChanged)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("A merge identifier is required.", nameof(id));
        if (target == default || source == default || target == source)
            throw new ArgumentException("Merge target and source must be initialized and distinct.");
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(sourceSnapshot);
        if (sourceSnapshot.Id != source || sourceSnapshot.MergedInto is not null)
            throw new ArgumentException("The snapshot must be the live source contact.", nameof(sourceSnapshot));
        Id = id;
        Target = target;
        Source = source;
        MergedAt = ContactText.Instant(mergedAt);
        Actor = actor;
        SourceSnapshot = sourceSnapshot;
        MovedIdentities = Bounded(movedIdentities, Contact.MaximumIdentities, nameof(movedIdentities));
        ReplacedIdentities = Bounded(replacedIdentities, Contact.MaximumIdentities, nameof(replacedIdentities));
        AttributeChanges = Bounded(attributeChanges, Contact.MaximumAttributes, nameof(attributeChanges));
        AddedLabels = Bounded(addedLabels, Contact.MaximumLabels, nameof(addedLabels));
        FilledProfileFields = Bounded(filledProfileFields, 5, nameof(filledProfileFields));
        if (FilledProfileFields.Any(field => !Enum.IsDefined(field)) || AddedLabels.Any(label => label == default))
            throw new ArgumentException("Merge contributions must be valid.");
        BlockedChanged = blockedChanged;
    }

    public Guid Id { get; }
    public ContactId Target { get; }
    public ContactId Source { get; }
    public DateTimeOffset MergedAt { get; }
    public AccessActor Actor { get; }
    public Contact SourceSnapshot { get; }

    /// <summary>Source addresses added to the target.</summary>
    public IReadOnlyList<ContactIdentityAddress> MovedIdentities { get; }

    /// <summary>Target identities (claimed) that the merge replaced with the source's verified identity for the same address.</summary>
    public IReadOnlyList<ContactIdentity> ReplacedIdentities { get; }

    public IReadOnlyList<ContactMergeAttributeChange> AttributeChanges { get; }
    public IReadOnlyList<LabelId> AddedLabels { get; }

    /// <summary>Target profile fields that were empty and were filled from the source.</summary>
    public IReadOnlyList<ContactProfileField> FilledProfileFields { get; }

    /// <summary>The target was unblocked and became blocked because the source was blocked.</summary>
    public bool BlockedChanged { get; }

    public bool Equals(ContactMergeRecord? other) =>
        other is not null && Id == other.Id && Target == other.Target && Source == other.Source && MergedAt == other.MergedAt
        && Actor == other.Actor && SourceSnapshot == other.SourceSnapshot && MovedIdentities.SequenceEqual(other.MovedIdentities)
        && ReplacedIdentities.SequenceEqual(other.ReplacedIdentities) && AttributeChanges.SequenceEqual(other.AttributeChanges)
        && AddedLabels.SequenceEqual(other.AddedLabels) && FilledProfileFields.SequenceEqual(other.FilledProfileFields)
        && BlockedChanged == other.BlockedChanged;

    public override int GetHashCode() => HashCode.Combine(Id, Target, Source);

    public override string ToString() => $"ContactMergeRecord {{ Id = {Id}, Target = {Target}, Source = {Source} }}";

    private static IReadOnlyList<T> Bounded<T>(IEnumerable<T> values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.ToList();
        if (snapshot.Count > maximum || snapshot.Any(value => value is null))
            throw new ArgumentException($"At most {maximum} non-null values are allowed.", parameterName);
        return snapshot.AsReadOnly();
    }
}
