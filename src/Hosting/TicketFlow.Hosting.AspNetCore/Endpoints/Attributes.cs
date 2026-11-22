using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Hosting.AspNetCore.Contracts;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

/// <summary>Attribute value and masking conversions shared by contacts and conversations.</summary>
internal static class Attributes
{
    public static AttributeValueDto Dto(ContactAttributeValue value) => value switch
    {
        TextAttributeValue text => new AttributeValueDto("text", Text: text.Value),
        NumberAttributeValue number => new AttributeValueDto("number", Number: number.Value),
        BooleanAttributeValue boolean => new AttributeValueDto("boolean", Boolean: boolean.Value),
        DateAttributeValue date => new AttributeValueDto("date", Date: date.Value),
        ListAttributeValue list => new AttributeValueDto("list", Items: list.Items),
    };

    public static ContactAttributeValue Contact(AttributeValueDto? dto)
    {
        ArgumentNullException.ThrowIfNull(dto, "value");
        return dto.Type switch
        {
            "text" => new TextAttributeValue(dto.Text ?? throw Missing("text")),
            "number" => new NumberAttributeValue(dto.Number ?? throw Missing("number")),
            "boolean" => new BooleanAttributeValue(dto.Boolean ?? throw Missing("boolean")),
            "date" => new DateAttributeValue(dto.Date ?? throw Missing("date")),
            "list" => new ListAttributeValue(dto.Items ?? throw Missing("items")),
            _ => throw new ArgumentException("Attribute value types are text, number, boolean, date or list.", "value"),
        };
    }

    public static AttributeValueDto Dto(ConversationAttributeValue value) => value switch
    {
        ConversationTextValue text => new AttributeValueDto("text", Text: text.Value),
        ConversationNumberValue number => new AttributeValueDto("number", Number: number.Value),
        ConversationBooleanValue boolean => new AttributeValueDto("boolean", Boolean: boolean.Value),
        ConversationDateValue date => new AttributeValueDto("date", Date: date.Value),
        ConversationListValue list => new AttributeValueDto("list", Items: list.Items),
    };

    public static ConversationAttributeValue Conversation(AttributeValueDto? dto)
    {
        ArgumentNullException.ThrowIfNull(dto, "value");
        return dto.Type switch
        {
            "text" => new ConversationTextValue(dto.Text ?? throw Missing("text")),
            "number" => new ConversationNumberValue(dto.Number ?? throw Missing("number")),
            "boolean" => new ConversationBooleanValue(dto.Boolean ?? throw Missing("boolean")),
            "date" => new ConversationDateValue(dto.Date ?? throw Missing("date")),
            "list" => new ConversationListValue(dto.Items ?? throw Missing("items")),
            _ => throw new ArgumentException("Attribute value types are text, number, boolean, date or list.", "value"),
        };
    }

    public static IReadOnlyList<MaskedFieldDto> Masked(IEnumerable<MaskedField> fields) => [.. fields.Select(field => new MaskedFieldDto(field.Field.Value, field.Revealable))];

    private static ArgumentException Missing(string property) => new($"The '{property}' property is required for this attribute type.", "value");
}
