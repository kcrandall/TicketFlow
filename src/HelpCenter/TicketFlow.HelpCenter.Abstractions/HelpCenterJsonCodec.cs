using System.Text.Json;

namespace TicketFlow.HelpCenter;

/// <summary>
/// Versioned persisted encoding of translation lists for durable stores: <c>{"v":1,"items":[...]}</c> with fixed property names.
/// Decoding rejects unknown versions, unknown or duplicate properties and values that fail model validation.
/// </summary>
public static class HelpCenterJsonCodec
{
    private const int Version = 1;
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8 };

    public static string EncodeCollectionTranslations(IEnumerable<CollectionTranslation> translations) =>
        Encode(translations, (writer, translation) =>
        {
            writer.WriteString("locale", translation.Locale.Value);
            writer.WriteString("name", translation.Name);
            writer.WriteString("slug", translation.Slug.Value);
            if (translation.Description is not null)
                writer.WriteString("description", translation.Description);
        });

    public static IReadOnlyList<CollectionTranslation> DecodeCollectionTranslations(string json) =>
        Decode(json, ["locale", "name", "slug", "description"], item => new CollectionTranslation(
            new HelpCenterLocale(item.RequiredString("locale")), item.RequiredString("name"), new HelpCenterSlug(item.RequiredString("slug")), item.OptionalString("description")));

    public static string EncodeArticleTranslations(IEnumerable<ArticleTranslation> translations) =>
        Encode(translations, WriteArticle);

    public static IReadOnlyList<ArticleTranslation> DecodeArticleTranslations(string json) =>
        Decode(json, ["locale", "title", "slug", "body", "excerpt", "tags"], ReadArticle);

    public static string EncodePublishedTranslations(IEnumerable<PublishedTranslation> translations) =>
        Encode(translations, (writer, translation) =>
        {
            WriteArticle(writer, translation.Content);
            writer.WriteString("plain", translation.PlainText);
        });

    public static IReadOnlyList<PublishedTranslation> DecodePublishedTranslations(string json) =>
        Decode(json, ["locale", "title", "slug", "body", "excerpt", "tags", "plain"], item =>
            new PublishedTranslation(ReadArticle(item), item["plain"] is { ValueKind: JsonValueKind.String } plain
                ? plain.GetString()!
                : throw new FormatException("Missing plain text.")));

    private static void WriteArticle(Utf8JsonWriter writer, ArticleTranslation translation)
    {
        writer.WriteString("locale", translation.Locale.Value);
        writer.WriteString("title", translation.Title);
        writer.WriteString("slug", translation.Slug.Value);
        writer.WriteString("body", translation.Body);
        if (translation.Excerpt is not null)
            writer.WriteString("excerpt", translation.Excerpt);
        writer.WriteStartArray("tags");
        foreach (var tag in translation.Tags)
            writer.WriteStringValue(tag);
        writer.WriteEndArray();
    }

    private static ArticleTranslation ReadArticle(JsonElement item)
    {
        var tags = new List<string>();
        if (item["tags"] is not { ValueKind: JsonValueKind.Array } array)
            throw new FormatException("Missing tags.");
        foreach (var tag in array.EnumerateArray())
            tags.Add(tag.ValueKind == JsonValueKind.String ? tag.GetString()! : throw new FormatException("Tags must be strings."));
        return new ArticleTranslation(new HelpCenterLocale(item.RequiredString("locale")), item.RequiredString("title"), new HelpCenterSlug(item.RequiredString("slug")),
            item.RequiredString("body"), item.OptionalString("excerpt"), tags);
    }

    private static string Encode<T>(IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
    {
        ArgumentNullException.ThrowIfNull(items);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                write(writer, item);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static List<T> Decode<T>(string json, string[] allowed, Func<JsonElement, T> read)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json, Options);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("Translations must be a JSON object.");
        RequireProperties(root, ["v", "items"]);
        if (root["v"] is not { ValueKind: JsonValueKind.Number } version || !version.TryGetInt32(out var number) || number != Version)
            throw new FormatException("Unsupported translation format version.");
        if (root["items"] is not { ValueKind: JsonValueKind.Array } items)
            throw new FormatException("Missing translation items.");
        var result = new List<T>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new FormatException("Translation items must be objects.");
            RequireProperties(item, allowed);
            result.Add(read(item));
        }
        return result;
    }

    private static void RequireProperties(JsonElement element, string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new FormatException("Unknown or duplicate translation property.");
    }
}

internal static class HelpCenterJsonExtensions
{
    extension(JsonElement element)
    {
        public JsonElement? this[string name] => element.TryGetProperty(name, out var value) ? value : null;

        public string RequiredString(string name) =>
            element[name] is { ValueKind: JsonValueKind.String } value ? value.GetString()! : throw new FormatException($"Missing '{name}'.");

        public string? OptionalString(string name) =>
            element[name] is not { } value ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"'{name}' must be a string.");
    }
}
