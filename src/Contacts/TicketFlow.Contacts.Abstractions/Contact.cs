using System.Collections.Frozen;
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

    public ContactProfile(string? displayName = null, string? avatarRef = null, string? locale = null, string? timeZone = null, string? company = null)
    {
        DisplayName = ContactText.OptionalText(displayName, MaximumDisplayNameLength, nameof(displayName));
        AvatarRef = ContactText.OptionalText(avatarRef, MaximumAvatarRefLength, nameof(avatarRef));
        if (locale is not null && (locale.Length is < 2 or > MaximumLocaleLength || !locale.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            || !char.IsAsciiLetter(locale[0])))
            throw new ArgumentException("Locales are BCP 47 tags of 2-35 ASCII letters, digits or '-'.", nameof(locale));
        Locale = locale;
        if (timeZone is not null && (timeZone.Length is < 1 or > MaximumTimeZoneLength
            || !timeZone.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '_' or '-' or '+')))
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

        var addresses = new HashSet<ContactIdentityAddress>();
        var identityList = new List<ContactIdentity>();
        foreach (var identity in identities)
        {
            ArgumentNullException.ThrowIfNull(identity, nameof(identities));
            if (!addresses.Add(identity.Address))
                throw new ArgumentException("A contact cannot hold the same address twice.", nameof(identities));
            if (identityList.Count == MaximumIdentities)
                throw new ArgumentException($"A contact can hold at most {MaximumIdentities} identities.", nameof(identities));
            identityList.Add(identity);
        }
        if (mergedInto is not null && identityList.Count > 0)
            throw new ArgumentException("A merge tombstone holds no identities.", nameof(identities));
        Identities = [.. identityList.OrderBy(identity => (int)identity.Address.Kind).ThenBy(identity => identity.Address.Namespace, StringComparer.Ordinal)
            .ThenBy(identity => identity.Address.Value, StringComparer.Ordinal)];

        var attributeMap = new SortedDictionary<string, ContactAttributeValue>(StringComparer.Ordinal);
        foreach (var (key, value) in attributes)
        {
            ContactAttributeDefinition.RequireKey(key, nameof(attributes));
            if (value.Value is null)
                throw new ArgumentException("Attribute values must be initialized.", nameof(attributes));
            if (!attributeMap.TryAdd(key, value))
                throw new ArgumentException("Attribute keys must be unique.", nameof(attributes));
            if (attributeMap.Count > MaximumAttributes)
                throw new ArgumentException($"A contact can hold at most {MaximumAttributes} attributes.", nameof(attributes));
        }
        Attributes = attributeMap.ToFrozenDictionary(StringComparer.Ordinal);
        AttributeKeys = [.. attributeMap.Keys];

        var labelSet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label == default)
                throw new ArgumentException("Labels must be initialized.", nameof(labels));
            if (labelSet.Add(label.Value) && labelSet.Count > MaximumLabels)
                throw new ArgumentException($"A contact can carry at most {MaximumLabels} labels.", nameof(labels));
        }
        Labels = [.. labelSet.Select(label => new LabelId(label))];
        Blocked = blocked;
        CreatedAt = ContactText.Instant(createdAt);
        UpdatedAt = ContactText.Instant(updatedAt);
        if (UpdatedAt < CreatedAt)
            throw new ArgumentException("A contact cannot be updated before it was created.", nameof(updatedAt));
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
        DateTimeOffset? updatedAt = null) =>
        new(Id, profile ?? Profile, identities ?? Identities, attributes ?? Attributes, labels ?? Labels, blocked ?? Blocked, CreatedAt,
            updatedAt ?? UpdatedAt, MergedInto);

    /// <summary>A merge tombstone: profile, attributes and labels are kept, identities move to <paramref name="target"/>.</summary>
    public Contact Tombstone(ContactId target, DateTimeOffset at) =>
        new(Id, Profile, [], Attributes, Labels, Blocked, CreatedAt, Max(UpdatedAt, at), target);

    /// <summary>This contact restored as live at <paramref name="at"/> (clears <see cref="MergedInto"/>).</summary>
    public Contact Restore(DateTimeOffset at) => new(Id, Profile, Identities, Attributes, Labels, Blocked, CreatedAt, Max(UpdatedAt, at));

    public bool Equals(Contact? other) =>
        other is not null && Id == other.Id && Profile == other.Profile && Blocked == other.Blocked && CreatedAt == other.CreatedAt
        && UpdatedAt == other.UpdatedAt && MergedInto == other.MergedInto && Identities.SequenceEqual(other.Identities)
        && Labels.SequenceEqual(other.Labels) && AttributeKeys.SequenceEqual(other.AttributeKeys, StringComparer.Ordinal)
        && AttributeKeys.All(key => Attributes[key].Equals(other.Attributes[key]));

    public override int GetHashCode() => HashCode.Combine(Id, Identities.Count, Attributes.Count, Labels.Count, UpdatedAt);

    public override string ToString() => $"Contact {{ Id = {Id}, <redacted> }}";

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}

/// <summary>A contact with its scope and revision (first revision is 1).</summary>
public sealed record StoredContact(TicketScope Scope, Contact Contact, long Revision)
{
    public ContactId Id => Contact.Id;
}
