using System.Globalization;
using TicketFlow.Access;

namespace TicketFlow.Conversations;

/// <summary>Custom attribute types. Values are persisted; never renumber.</summary>
public enum ConversationAttributeType
{
    Text = 1,
    Number = 2,
    Boolean = 3,
    Date = 4,
    List = 5,
}

/// <summary>
/// A workspace-scoped conversation attribute definition. <see cref="Sensitive"/> attributes are masked through the Access field path
/// <c>conversation.attribute.&lt;key&gt;</c>; when no field policy covers a sensitive attribute its value fails closed (masked, not
/// revealable). The type is immutable once defined.
/// </summary>
public sealed record ConversationAttributeDefinition
{
    public const int MaximumKeyLength = 64;
    public const int MaximumDisplayNameLength = 128;

    public ConversationAttributeDefinition(string key, ConversationAttributeType type, bool sensitive = false, string? displayName = null)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        Key = RequireKey(key, nameof(key));
        Type = type;
        Sensitive = sensitive;
        DisplayName = ConversationText.OptionalText(displayName, MaximumDisplayNameLength, nameof(displayName));
    }

    /// <summary>Lowercase token (a-z, 0-9, '-', '_', starting with a letter), at most 64 characters.</summary>
    public string Key { get; }

    public ConversationAttributeType Type { get; }
    public bool Sensitive { get; }
    public string? DisplayName { get; }

    /// <summary>The Access field path masking this attribute.</summary>
    public FieldPath Field => ConversationFieldPaths.Attribute(Key);

    public static string RequireKey(string key, string parameterName) =>
        ConversationText.RequireToken(key, MaximumKeyLength, parameterName, "Attribute keys");

    public static bool IsValidKey(string? key) => key is not null && ConversationText.IsToken(key, MaximumKeyLength);

    /// <summary>True when <paramref name="value"/> has this definition's type.</summary>
    public bool Accepts(ConversationAttributeValue value) => value.Value is not null && value.Type == Type;
}

/// <summary>A definition with its scope and revision (first revision is 1).</summary>
public sealed record StoredConversationAttributeDefinition(TicketFlow.Core.TicketScope Scope, ConversationAttributeDefinition Definition, long Revision);

public sealed record ConversationTextValue
{
    public const int MaximumLength = 4096;

    public ConversationTextValue(string value) => Value = ConversationText.RequireText(value, MaximumLength, nameof(value), allowEmpty: true);

    public string Value { get; }

    public override string ToString() => "ConversationTextValue { <redacted> }";
}

public sealed record ConversationNumberValue(decimal Value)
{
    public override string ToString() => "ConversationNumberValue { <redacted> }";
}

public sealed record ConversationBooleanValue(bool Value)
{
    public override string ToString() => "ConversationBooleanValue { <redacted> }";
}

public sealed record ConversationDateValue(DateOnly Value)
{
    public override string ToString() => "ConversationDateValue { <redacted> }";
}

/// <summary>An ordered list of 0-64 distinct text items of at most 256 characters each.</summary>
public sealed record ConversationListValue
{
    public const int MaximumItems = 64;
    public const int MaximumItemLength = 256;

    public ConversationListValue(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ConversationText.RequireText(item, MaximumItemLength, nameof(items));
            if (!seen.Add(item))
                throw new ArgumentException("List items must be distinct.", nameof(items));
            if (snapshot.Count == MaximumItems)
                throw new ArgumentException($"Lists can contain at most {MaximumItems} items.", nameof(items));
            snapshot.Add(item);
        }
        Items = snapshot.AsReadOnly();
    }

    public IReadOnlyList<string> Items { get; }

    public bool Equals(ConversationListValue? other) => other is not null && Items.SequenceEqual(other.Items, StringComparer.Ordinal);

    public override int GetHashCode() => Items.Count;

    public override string ToString() => "ConversationListValue { <redacted> }";
}

/// <summary>A typed attribute value. May be personal data: never logged or audited. Switch over every case without a discard arm.</summary>
public union ConversationAttributeValue(ConversationTextValue, ConversationNumberValue, ConversationBooleanValue, ConversationDateValue, ConversationListValue)
{
    public ConversationAttributeType Type => this switch
    {
        ConversationTextValue => ConversationAttributeType.Text,
        ConversationNumberValue => ConversationAttributeType.Number,
        ConversationBooleanValue => ConversationAttributeType.Boolean,
        ConversationDateValue => ConversationAttributeType.Date,
        ConversationListValue => ConversationAttributeType.List,
    };

    /// <summary>
    /// The string form used by Access attribute conditions and filters: text verbatim, numbers in invariant culture, booleans as
    /// <c>true</c>/<c>false</c>, dates as <c>yyyy-MM-dd</c>. Lists have no condition form (<see langword="null"/>: unknown).
    /// </summary>
    public string? ConditionValue => this switch
    {
        ConversationTextValue text => text.Value,
        ConversationNumberValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        ConversationBooleanValue boolean => boolean.Value ? "true" : "false",
        ConversationDateValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ConversationListValue => null,
    };
}
