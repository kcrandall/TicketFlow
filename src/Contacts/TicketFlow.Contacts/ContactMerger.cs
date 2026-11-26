using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>
/// Pure merge and unmerge computation.
/// <list type="bullet">
/// <item>Identities: source addresses missing on the target move to it; when the target holds an address as claimed and the source
/// holds it verified, the verified identity replaces the claimed one; otherwise the target's identity wins.</item>
/// <item>Attributes: keys missing on the target are copied; for keys on both sides the target wins unless
/// <see cref="ContactAttributeConflict.PreferSource"/>.</item>
/// <item>Labels: union. Profile: empty target fields are filled from the source. Blocked: the target becomes blocked when the source is.</item>
/// </list>
/// Unmerge restores the source snapshot and removes from the target exactly the recorded contributions that are still unchanged
/// (moved identities are always returned to the source).
/// </summary>
public static class ContactMerger
{
    public sealed record MergeComputation(Contact Target, Contact Tombstone, ContactMergeRecord Record);

    public static MergeComputation Merge(Guid mergeId, Contact target, Contact source, ContactAttributeConflict conflict, AccessActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(actor);
        if (target.MergedInto is not null || source.MergedInto is not null || target.Id == source.Id)
            throw new ArgumentException("Only two distinct live contacts can be merged.", target.MergedInto is not null ? nameof(target) : nameof(source));

        var identities = target.Identities.ToDictionary(identity => identity.Address);
        var moved = new List<ContactIdentityAddress>();
        var replaced = new List<ContactIdentity>();
        foreach (var incoming in source.Identities)
        {
            if (!identities.TryGetValue(incoming.Address, out var existing))
            {
                identities[incoming.Address] = incoming;
                moved.Add(incoming.Address);
            }
            else if (!existing.Verification.IsVerified && incoming.Verification.IsVerified)
            {
                identities[incoming.Address] = incoming;
                replaced.Add(existing);
            }
        }

        var attributes = new Dictionary<string, ContactAttributeValue>(target.Attributes, StringComparer.Ordinal);
        var attributeChanges = new List<ContactMergeAttributeChange>();
        foreach (var key in source.AttributeKeys)
        {
            var incoming = source.Attributes[key];
            if (!attributes.TryGetValue(key, out var existing))
            {
                attributes[key] = incoming;
                attributeChanges.Add(new(key, null, incoming));
            }
            else if (conflict == ContactAttributeConflict.PreferSource && !Equals(existing.Value, incoming.Value))
            {
                attributes[key] = incoming;
                attributeChanges.Add(new(key, existing, incoming));
            }
        }

        var addedLabels = new List<LabelId>();
        if (source.Labels.Count > 0)
        {
            if (target.Labels.Count == 0)
                addedLabels.AddRange(source.Labels);
            else
            {
                var targetLabels = new HashSet<LabelId>(target.Labels);
                foreach (var label in source.Labels)
                    if (!targetLabels.Contains(label))
                        addedLabels.Add(label);
            }
        }
        var filled = new List<ContactProfileField>();
        var profile = new ContactProfile(
            Fill(target.Profile.DisplayName, source.Profile.DisplayName, ContactProfileField.DisplayName, filled),
            Fill(target.Profile.AvatarRef, source.Profile.AvatarRef, ContactProfileField.AvatarRef, filled),
            Fill(target.Profile.Locale, source.Profile.Locale, ContactProfileField.Locale, filled),
            Fill(target.Profile.TimeZone, source.Profile.TimeZone, ContactProfileField.TimeZone, filled),
            Fill(target.Profile.Company, source.Profile.Company, ContactProfileField.Company, filled));
        var blockedChanged = !target.Blocked && source.Blocked;

        var mergedLabels = addedLabels.Count == 0 ? target.Labels : target.Labels.Concat(addedLabels);
        var merged = target.With(profile, identities.Values, attributes, mergedLabels, target.Blocked || source.Blocked, Later(target.UpdatedAt, at));
        var record = new ContactMergeRecord(mergeId, target.Id, source.Id, at, actor, source, moved, replaced, attributeChanges, addedLabels, filled, blockedChanged);
        return new(merged, source.Tombstone(target.Id, at), record);
    }

    public static (Contact Target, Contact Source) Unmerge(ContactMergeRecord record, Contact target, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(target);
        var snapshot = record.SourceSnapshot;
        var sourceIdentities = snapshot.Identities.ToDictionary(identity => identity.Address);
        var identities = target.Identities.ToDictionary(identity => identity.Address);
        foreach (var address in record.MovedIdentities)
            identities.Remove(address);
        foreach (var previous in record.ReplacedIdentities)
            if (identities.TryGetValue(previous.Address, out var current) && sourceIdentities.TryGetValue(previous.Address, out var contributed) && current == contributed)
                identities[previous.Address] = previous;

        var attributes = new Dictionary<string, ContactAttributeValue>(target.Attributes, StringComparer.Ordinal);
        foreach (var change in record.AttributeChanges)
        {
            if (!attributes.TryGetValue(change.Key, out var current) || !Equals(current.Value, change.Merged.Value))
                continue;
            if (change.Previous is { } previous)
                attributes[change.Key] = previous;
            else
                attributes.Remove(change.Key);
        }

        IEnumerable<LabelId> labels;
        if (record.AddedLabels.Count == 0 || target.Labels.Count == 0)
            labels = target.Labels;
        else
        {
            var addedSet = new HashSet<LabelId>(record.AddedLabels);
            var remaining = new List<LabelId>(target.Labels.Count);
            foreach (var label in target.Labels)
                if (!addedSet.Contains(label))
                    remaining.Add(label);
            labels = remaining;
        }
        var profile = target.Profile;
        var source = snapshot.Profile;
        profile = new ContactProfile(
            Unfill(profile.DisplayName, source.DisplayName, ContactProfileField.DisplayName, record),
            Unfill(profile.AvatarRef, source.AvatarRef, ContactProfileField.AvatarRef, record),
            Unfill(profile.Locale, source.Locale, ContactProfileField.Locale, record),
            Unfill(profile.TimeZone, source.TimeZone, ContactProfileField.TimeZone, record),
            Unfill(profile.Company, source.Company, ContactProfileField.Company, record));
        var blocked = target.Blocked && !record.BlockedChanged;

        var restoredTarget = target.With(profile, identities.Values, attributes, labels, blocked, Later(target.UpdatedAt, at));
        return (restoredTarget, snapshot.Restore(at));
    }

    private static string? Fill(string? target, string? source, ContactProfileField field, List<ContactProfileField> filled)
    {
        if (target is not null || source is null)
            return target;
        filled.Add(field);
        return source;
    }

    private static string? Unfill(string? current, string? contributed, ContactProfileField field, ContactMergeRecord record) =>
        record.FilledProfileFields.Contains(field) && string.Equals(current, contributed, StringComparison.Ordinal) ? null : current;

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}
