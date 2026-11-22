using TicketFlow.Access;

namespace TicketFlow.Contacts;

/// <summary>Custom attribute types. Values are persisted; never renumber.</summary>
public enum ContactAttributeType
{
    Text = 1,
    Number = 2,
    Boolean = 3,
    Date = 4,
    List = 5,
}

/// <summary>
/// A workspace-scoped custom attribute definition. <see cref="Sensitive"/> attributes are masked through the Access field path
/// <c>contact.attribute.&lt;key&gt;</c>; when no field policy covers a sensitive attribute its value fails closed (masked, not
/// revealable). The type is immutable once defined.
/// </summary>
public sealed record ContactAttributeDefinition
{
    public const int MaximumKeyLength = 64;
    public const int MaximumDisplayNameLength = 128;

    public ContactAttributeDefinition(string key, ContactAttributeType type, bool sensitive = false, string? displayName = null)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        Key = RequireKey(key, nameof(key));
        Type = type;
        Sensitive = sensitive;
        DisplayName = ContactText.OptionalText(displayName, MaximumDisplayNameLength, nameof(displayName));
    }

    /// <summary>Lowercase token (a-z, 0-9, '-', '_', starting with a letter), at most 64 characters.</summary>
    public string Key { get; }

    public ContactAttributeType Type { get; }
    public bool Sensitive { get; }
    public string? DisplayName { get; }

    /// <summary>The Access field path masking this attribute.</summary>
    public FieldPath Field => ContactFieldPaths.Attribute(Key);

    public static string RequireKey(string key, string parameterName) =>
        ContactText.RequireToken(key, MaximumKeyLength, parameterName, "Attribute keys");

    public static bool IsValidKey(string? key) => key is not null && ContactText.IsToken(key, MaximumKeyLength);

    /// <summary>True when <paramref name="value"/> has this definition's type.</summary>
    public bool Accepts(ContactAttributeValue value) => value.Type == Type;
}

/// <summary>A definition with its scope revision (first revision is 1).</summary>
public sealed record StoredContactAttributeDefinition(TicketFlow.Core.TicketScope Scope, ContactAttributeDefinition Definition, long Revision);

public sealed record TextAttributeValue
{
    public const int MaximumLength = 4096;

    public TextAttributeValue(string value) => Value = ContactText.RequireText(value, MaximumLength, nameof(value), allowEmpty: true);

    public string Value { get; }

    public override string ToString() => "TextAttributeValue { <redacted> }";
}

public sealed record NumberAttributeValue(decimal Value)
{
    public override string ToString() => "NumberAttributeValue { <redacted> }";
}

public sealed record BooleanAttributeValue(bool Value)
{
    public override string ToString() => "BooleanAttributeValue { <redacted> }";
}

public sealed record DateAttributeValue(DateOnly Value)
{
    public override string ToString() => "DateAttributeValue { <redacted> }";
}

/// <summary>An ordered list of 0-64 distinct text items of at most 256 characters each.</summary>
public sealed record ListAttributeValue
{
    public const int MaximumItems = 64;
    public const int MaximumItemLength = 256;

    public ListAttributeValue(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ContactText.RequireText(item, MaximumItemLength, nameof(items));
            if (!seen.Add(item))
                throw new ArgumentException("List items must be distinct.", nameof(items));
            if (snapshot.Count == MaximumItems)
                throw new ArgumentException($"Lists can contain at most {MaximumItems} items.", nameof(items));
            snapshot.Add(item);
        }
        Items = snapshot.AsReadOnly();
    }

    public IReadOnlyList<string> Items { get; }

    public bool Equals(ListAttributeValue? other) => other is not null && Items.SequenceEqual(other.Items, StringComparer.Ordinal);

    public override int GetHashCode() => Items.Count;

    public override string ToString() => "ListAttributeValue { <redacted> }";
}

/// <summary>A typed attribute value. Personal data: never logged or audited. Switch over every case without a discard arm.</summary>
public union ContactAttributeValue(TextAttributeValue, NumberAttributeValue, BooleanAttributeValue, DateAttributeValue, ListAttributeValue)
{
    public ContactAttributeType Type => this switch
    {
        TextAttributeValue => ContactAttributeType.Text,
        NumberAttributeValue => ContactAttributeType.Number,
        BooleanAttributeValue => ContactAttributeType.Boolean,
        DateAttributeValue => ContactAttributeType.Date,
        ListAttributeValue => ContactAttributeType.List,
    };
}
