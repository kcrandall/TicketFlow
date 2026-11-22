using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Hosting.AspNetCore.Contracts;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

/// <summary>Attribute value and masking conversions shared by contacts and conversations.</summary>
internal static class Attributes
{
    public static AttributeValueModel Dto(ContactAttributeValue value) => value switch
    {
        TextAttributeValue text => new AttributeValueModel("text", Text: text.Value),
        NumberAttributeValue number => new AttributeValueModel("number", Number: number.Value),
        BooleanAttributeValue boolean => new AttributeValueModel("boolean", Boolean: boolean.Value),
        DateAttributeValue date => new AttributeValueModel("date", Date: date.Value),
        ListAttributeValue list => new AttributeValueModel("list", Items: list.Items),
    };

    public static ContactAttributeValue Contact(AttributeValueModel? dto)
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

    public static AttributeValueModel Dto(ConversationAttributeValue value) => value switch
    {
        ConversationTextValue text => new AttributeValueModel("text", Text: text.Value),
        ConversationNumberValue number => new AttributeValueModel("number", Number: number.Value),
        ConversationBooleanValue boolean => new AttributeValueModel("boolean", Boolean: boolean.Value),
        ConversationDateValue date => new AttributeValueModel("date", Date: date.Value),
        ConversationListValue list => new AttributeValueModel("list", Items: list.Items),
    };

    public static ConversationAttributeValue Conversation(AttributeValueModel? dto)
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

    public static IReadOnlyList<MaskedFieldResponse> Masked(IEnumerable<MaskedField> fields) => [.. fields.Select(field => new MaskedFieldResponse(field.Field.Value, field.Revealable))];

    private static ArgumentException Missing(string property) => new($"The '{property}' property is required for this attribute type.", "value");
}
