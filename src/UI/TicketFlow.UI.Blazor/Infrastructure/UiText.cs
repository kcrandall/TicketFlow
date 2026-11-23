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
        var span = body.AsSpan();
        var sb = new System.Text.StringBuilder(Math.Min(span.Length, length + 2));
        var needSpace = false;
        var i = 0;
        while (i < span.Length)
        {
            var c = span[i];
            if (c is '\r' or '\n' or '\t')
            {
                if (sb.Length > 0)
                    needSpace = true;
                i++;
                continue;
            }
            if (c == '*' && i + 1 < span.Length && span[i + 1] == '*')
            {
                i += 2;
                continue;
            }
            if (needSpace)
            {
                if (sb.Length >= length)
                    break;
                sb.Append(' ');
                needSpace = false;
            }
            if (sb.Length >= length)
            {
                // Truncate at length with trailing whitespace trimmed.
                while (sb.Length > 0 && char.IsWhiteSpace(sb[^1]))
                    sb.Length--;
                sb.Append('…');
                return sb.ToString();
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
