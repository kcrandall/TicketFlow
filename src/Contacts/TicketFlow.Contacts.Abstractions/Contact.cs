using System.Buffers;
using System.Collections.Frozen;
using System.Collections.ObjectModel;
using TicketFlow.Core;

namespace TicketFlow.Contacts;

/// <summary>Bounded descriptive contact fields. Every value is optional personal data and is never logged.</summary>
public sealed record ContactProfile
{
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumAvatarRefLength = 2048;
    public const int MaximumLocaleLength = 35;
    public const int MaximumTimeZoneLength = 64;
    public const int MaximumCompanyLength = 256;

    private static readonly SearchValues<char> LocaleChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-");
    private static readonly SearchValues<char> TimeZoneChars = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789/_-+");

    public ContactProfile(string? displayName = null, string? avatarRef = null, string? locale = null, string? timeZone = null, string? company = null)
    {
        DisplayName = ContactText.OptionalText(displayName, MaximumDisplayNameLength, nameof(displayName));
        AvatarRef = ContactText.OptionalText(avatarRef, MaximumAvatarRefLength, nameof(avatarRef));
        if (locale is not null && (locale.Length is < 2 or > MaximumLocaleLength || !char.IsAsciiLetter(locale[0]) || locale.AsSpan().ContainsAnyExcept(LocaleChars)))
            throw new ArgumentException("Locales are BCP 47 tags of 2-35 ASCII letters, digits or '-'.", nameof(locale));
        Locale = locale;
        if (timeZone is not null && (timeZone.Length is < 1 or > MaximumTimeZoneLength || timeZone.AsSpan().ContainsAnyExcept(TimeZoneChars)))
            throw new ArgumentException("Time zones are IANA identifiers of 1-64 ASCII letters, digits, '/', '_', '-' or '+'.", nameof(timeZone));
        TimeZone = timeZone;
        Company = ContactText.OptionalText(company, MaximumCompanyLength, nameof(company));
    }

    public static ContactProfile Empty { get; } = new();

    public string? DisplayName { get; }
    public string? AvatarRef { get; }
    public string? Locale { get; }
    public string? TimeZone { get; }
    public string? Company { get; }

    public override string ToString() => "ContactProfile { <redacted> }";
}

/// <summary>
/// A customer contact with its identity graph. Identities are unique by address and ordered by (kind, namespace, value); attributes
/// and labels are ordered by key (ordinal). A contact with <see cref="MergedInto"/> set is a merge tombstone: it has no identities
/// and is hidden from search and resolution until unmerged.
/// </summary>
public sealed record Contact
{
    public const int MaximumIdentities = 64;
    public const int MaximumAttributes = 128;
    public const int MaximumLabels = 64;

    private static readonly IReadOnlyDictionary<string, ContactAttributeValue> EmptyAttributes = FrozenDictionary<string, ContactAttributeValue>.Empty;

    public Contact(
        ContactId id,
        ContactProfile profile,
        IEnumerable<ContactIdentity> identities,
        IEnumerable<KeyValuePair<string, ContactAttributeValue>> attributes,
        IEnumerable<LabelId> labels,
        bool blocked,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        ContactId? mergedInto = null)
    {
        if (id == default)
            throw new ArgumentException("A contact identifier is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(labels);
        if (mergedInto == default(ContactId) || mergedInto == id)
            throw new ArgumentException("A merge target must be initialized and differ from the contact.", nameof(mergedInto));
        Id = id;
        Profile = profile;
        Identities = NormalizeIdentities(identities, mergedInto);
        (Attributes, AttributeKeys) = NormalizeAttributes(attributes);
        Labels = NormalizeLabels(labels);
        Blocked = blocked;
        CreatedAt = ContactText.Instant(createdAt);
        UpdatedAt = ContactText.Instant(updatedAt);
        if (UpdatedAt < CreatedAt)
            throw new ArgumentException("A contact cannot be updated before it was created.", nameof(updatedAt));
        MergedInto = mergedInto;
    }

    private Contact(
        ContactId id,
        ContactProfile profile,
        IReadOnlyList<ContactIdentity> identities,
        IReadOnlyDictionary<string, ContactAttributeValue> attributes,
        IReadOnlyList<string> attributeKeys,
        IReadOnlyList<LabelId> labels,
        bool blocked,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        ContactId? mergedInto)
    {
        Id = id;
        Profile = profile;
        Identities = identities;
        Attributes = attributes;
        AttributeKeys = attributeKeys;
        Labels = labels;
        Blocked = blocked;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        MergedInto = mergedInto;
    }

    public ContactId Id { get; }
    public ContactProfile Profile { get; }
    public IReadOnlyList<ContactIdentity> Identities { get; }

    /// <summary>Attribute values by key.</summary>
    public IReadOnlyDictionary<string, ContactAttributeValue> Attributes { get; }

    /// <summary>Attribute keys in ordinal order.</summary>
    public IReadOnlyList<string> AttributeKeys { get; }

    /// <summary>Labels in ordinal order.</summary>
    public IReadOnlyList<LabelId> Labels { get; }

    public bool Blocked { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }

    /// <summary>The surviving contact when this contact is a merge tombstone.</summary>
    public ContactId? MergedInto { get; }

    /// <summary>Returns a copy with the supplied parts replaced.</summary>
    public Contact With(
        ContactProfile? profile = null,
        IEnumerable<ContactIdentity>? identities = null,
        IEnumerable<KeyValuePair<string, ContactAttributeValue>>? attributes = null,
        IEnumerable<LabelId>? labels = null,
        bool? blocked = null,
        DateTimeOffset? updatedAt = null)
    {
        var nextUpdated = updatedAt is { } ua ? ContactText.Instant(ua) : UpdatedAt;
        if (nextUpdated < CreatedAt)
            throw new ArgumentException("A contact cannot be updated before it was created.", nameof(updatedAt));
        var nextIdentities = identities is null || ReferenceEquals(identities, Identities) ? Identities : NormalizeIdentities(identities, MergedInto);
        var (nextAttributes, nextKeys) = attributes is null || ReferenceEquals(attributes, Attributes) ? (Attributes, AttributeKeys) : NormalizeAttributes(attributes);
        var nextLabels = labels is null || ReferenceEquals(labels, Labels) ? Labels : NormalizeLabels(labels);
        return new(Id, profile ?? Profile, nextIdentities, nextAttributes, nextKeys, nextLabels, blocked ?? Blocked, CreatedAt, nextUpdated, MergedInto);
    }

    /// <summary>A merge tombstone: profile, attributes and labels are kept, identities move to <paramref name="target"/>.</summary>
    public Contact Tombstone(ContactId target, DateTimeOffset at)
    {
        if (target == default || target == Id)
            throw new ArgumentException("A merge target must be initialized and differ from the contact.", nameof(target));
        return new(Id, Profile, [], Attributes, AttributeKeys, Labels, Blocked, CreatedAt, Max(UpdatedAt, ContactText.Instant(at)), target);
    }

    /// <summary>This contact restored as live at <paramref name="at"/> (clears <see cref="MergedInto"/>).</summary>
    public Contact Restore(DateTimeOffset at) =>
        new(Id, Profile, Identities, Attributes, AttributeKeys, Labels, Blocked, CreatedAt, Max(UpdatedAt, ContactText.Instant(at)), null);

    public bool Equals(Contact? other)
    {
        if (other is null || Id != other.Id || Profile != other.Profile || Blocked != other.Blocked || CreatedAt != other.CreatedAt
            || UpdatedAt != other.UpdatedAt || MergedInto != other.MergedInto
            || Identities.Count != other.Identities.Count || Labels.Count != other.Labels.Count || AttributeKeys.Count != other.AttributeKeys.Count)
            return false;
        for (var i = 0; i < Identities.Count; i++)
            if (Identities[i] != other.Identities[i])
                return false;
        for (var i = 0; i < Labels.Count; i++)
            if (Labels[i] != other.Labels[i])
                return false;
        for (var i = 0; i < AttributeKeys.Count; i++)
        {
            var key = AttributeKeys[i];
            if (!string.Equals(key, other.AttributeKeys[i], StringComparison.Ordinal) || !Equals(Attributes[key].Value, other.Attributes[key].Value))
                return false;
        }
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Id, Identities.Count, Attributes.Count, Labels.Count, UpdatedAt);

    public override string ToString() => $"Contact {{ Id = {Id}, <redacted> }}";

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static ReadOnlyCollection<ContactIdentity> NormalizeIdentities(IEnumerable<ContactIdentity> identities, ContactId? mergedInto)
    {
        var capacity = identities.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumIdentities) : 0;
        if (capacity == 0 && identities is ICollection<ContactIdentity> { Count: 0 } or IReadOnlyCollection<ContactIdentity> { Count: 0 })
            return ReadOnlyCollection<ContactIdentity>.Empty;
        var list = capacity > 0 ? new List<ContactIdentity>(capacity) : [];
        HashSet<ContactIdentityAddress>? addresses = capacity > 1 ? new(capacity) : null;
        foreach (var identity in identities)
        {
            ArgumentNullException.ThrowIfNull(identity, nameof(identities));
            if (list.Count == 1)
            {
                if (list[0].Address == identity.Address)
                    throw new ArgumentException("A contact cannot hold the same address twice.", nameof(identities));
                addresses ??= [list[0].Address];
                addresses.Add(identity.Address);
            }
            else if (addresses is not null && !addresses.Add(identity.Address))
                throw new ArgumentException("A contact cannot hold the same address twice.", nameof(identities));
            if (list.Count == MaximumIdentities)
                throw new ArgumentException($"A contact can hold at most {MaximumIdentities} identities.", nameof(identities));
            list.Add(identity);
        }
        if (list.Count == 0)
            return ReadOnlyCollection<ContactIdentity>.Empty;
        if (mergedInto is not null)
            throw new ArgumentException("A merge tombstone holds no identities.", nameof(identities));
        if (list.Count > 1)
            list.Sort(static (left, right) =>
            {
                var kindOrder = ((int)left.Address.Kind).CompareTo((int)right.Address.Kind);
                if (kindOrder != 0)
                    return kindOrder;
                var nsOrder = string.CompareOrdinal(left.Address.Namespace, right.Address.Namespace);
                return nsOrder != 0 ? nsOrder : string.CompareOrdinal(left.Address.Value, right.Address.Value);
            });
        return list.AsReadOnly();
    }

    private static (IReadOnlyDictionary<string, ContactAttributeValue> Attributes, IReadOnlyList<string> Keys) NormalizeAttributes(
        IEnumerable<KeyValuePair<string, ContactAttributeValue>> attributes)
    {
        var capacity = attributes.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumAttributes) : 0;
        if (capacity == 0 && attributes is ICollection<KeyValuePair<string, ContactAttributeValue>> { Count: 0 } or IReadOnlyCollection<KeyValuePair<string, ContactAttributeValue>> { Count: 0 })
            return (EmptyAttributes, []);
        var map = capacity > 0
            ? new Dictionary<string, ContactAttributeValue>(capacity, StringComparer.Ordinal)
            : new Dictionary<string, ContactAttributeValue>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            ContactAttributeDefinition.RequireKey(key, nameof(attributes));
            if (value.Value is null)
                throw new ArgumentException("Attribute values must be initialized.", nameof(attributes));
            if (!map.TryAdd(key, value))
                throw new ArgumentException("Attribute keys must be unique.", nameof(attributes));
            if (map.Count > MaximumAttributes)
                throw new ArgumentException($"A contact can hold at most {MaximumAttributes} attributes.", nameof(attributes));
        }
        if (map.Count == 0)
            return (EmptyAttributes, []);
        var keys = new string[map.Count];
        map.Keys.CopyTo(keys, 0);
        if (keys.Length > 1)
            Array.Sort(keys, StringComparer.Ordinal);
        return (new ReadOnlyDictionary<string, ContactAttributeValue>(map), keys);
    }

    private static ReadOnlyCollection<LabelId> NormalizeLabels(IEnumerable<LabelId> labels)
    {
        var capacity = labels.TryGetNonEnumeratedCount(out var count) ? Math.Min(count, MaximumLabels) : 0;
        if (capacity == 0 && labels is ICollection<LabelId> { Count: 0 } or IReadOnlyCollection<LabelId> { Count: 0 })
            return ReadOnlyCollection<LabelId>.Empty;
        var list = capacity > 0 ? new List<LabelId>(capacity) : [];
        foreach (var label in labels)
        {
            if (label == default)
                throw new ArgumentException("Labels must be initialized.", nameof(labels));
            list.Add(label);
        }
        if (list.Count == 0)
            return ReadOnlyCollection<LabelId>.Empty;
        if (list.Count == 1)
            return list.AsReadOnly();
        list.Sort(static (left, right) => string.CompareOrdinal(left.Value, right.Value));
        var write = 1;
        for (var read = 1; read < list.Count; read++)
        {
            if (!string.Equals(list[read].Value, list[write - 1].Value, StringComparison.Ordinal))
                list[write++] = list[read];
        }
        if (write > MaximumLabels)
            throw new ArgumentException($"A contact can carry at most {MaximumLabels} labels.", nameof(labels));
        if (write < list.Count)
            list.RemoveRange(write, list.Count - write);
        return list.AsReadOnly();
    }
}

/// <summary>A contact with its scope and revision (first revision is 1).</summary>
public sealed record StoredContact(TicketScope Scope, Contact Contact, long Revision)
{
    public ContactId Id => Contact.Id;
}
