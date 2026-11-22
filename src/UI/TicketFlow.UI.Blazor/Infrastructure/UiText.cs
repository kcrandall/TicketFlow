using System.Globalization;
using TicketFlow.Contacts;
using TicketFlow.Conversations;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>Display text for domain values.</summary>
public static class UiText
{
    public static string ContactName(ContactView contact) =>
        contact.Profile.DisplayName ?? contact.Identities.FirstOrDefault(identity => identity.Value is not null)?.Value ?? "Unnamed contact";

    public static string Attribute(ContactAttributeValue? value) => value is not { } present ? "—" : present switch
    {
        TextAttributeValue text => text.Value,
        NumberAttributeValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        BooleanAttributeValue boolean => boolean.Value ? "Yes" : "No",
        DateAttributeValue date => date.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
        ListAttributeValue list => string.Join(", ", list.Items),
    };

    public static string Attribute(ConversationAttributeValue? value) => value is not { } present ? "—" : present switch
    {
        ConversationTextValue text => text.Value,
        ConversationNumberValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        ConversationBooleanValue boolean => boolean.Value ? "Yes" : "No",
        ConversationDateValue date => date.Value.ToString("MMM d, yyyy", CultureInfo.InvariantCulture),
        ConversationListValue list => string.Join(", ", list.Items),
    };

    public static string Excerpt(string? body, int length = 120)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;
        var flat = string.Join(' ', body.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Replace("**", "", StringComparison.Ordinal);
        return flat.Length <= length ? flat : flat[..length].TrimEnd() + "…";
    }
}
