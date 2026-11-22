using System.Text.Json;

namespace TicketFlow.Surveys;

/// <summary>
/// Versioned persisted encoding of survey questions for durable stores: <c>{"v":1,"items":[{"locale","text","prompt"?}]}</c>.
/// Decoding rejects unknown versions and unknown or duplicate properties.
/// </summary>
public static class SurveyJsonCodec
{
    public static string EncodeQuestions(IEnumerable<SurveyQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteStartArray("items");
            foreach (var question in questions)
            {
                writer.WriteStartObject();
                writer.WriteString("locale", question.Locale.Value);
                writer.WriteString("text", question.Text);
                if (question.CommentPrompt is not null)
                    writer.WriteString("prompt", question.CommentPrompt);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static IReadOnlyList<SurveyQuestion> DecodeQuestions(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Questions must be a JSON object.");
        Only(root, "v", "items");
        if (!root.TryGetProperty("v", out var version) || !version.TryGetInt32(out var number) || number != 1)
            throw new FormatException("Unsupported question format version.");
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new FormatException("Missing question items.");
        var result = new List<SurveyQuestion>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new FormatException("Question items must be objects.");
            Only(item, "locale", "text", "prompt");
            result.Add(new SurveyQuestion(new SurveyLocale(Text(item, "locale")!), Text(item, "text")!, item.TryGetProperty("prompt", out _) ? Text(item, "prompt") : null));
        }
        return result;
    }

    private static void Only(JsonElement element, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new FormatException("Unknown or duplicate question property.");
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"Missing '{name}'.");
}
