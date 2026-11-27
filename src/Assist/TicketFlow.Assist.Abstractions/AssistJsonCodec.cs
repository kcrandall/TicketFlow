using System.Text;
using System.Text.Json;
using TicketFlow.HelpCenter;

namespace TicketFlow.Assist;

/// <summary>Extension event kinds the assistant appends to conversation timelines.</summary>
public static class AssistTimelineKinds
{
    public const string Summary = "assist.summary";
    public const string Draft = "assist.draft";
    public const string Triage = "assist.triage";

    /// <summary>Current payload version.</summary>
    public const int PayloadVersion = 1;

    /// <summary>The kind recorded for <paramref name="feature"/>, or <see langword="null"/> when the feature is not recorded.</summary>
    public static string? For(AssistFeature feature) => feature switch
    {
        AssistFeature.ConversationSummary => Summary,
        AssistFeature.ReplyDraft => Draft,
        AssistFeature.Triage => Triage,
        AssistFeature.SentimentAndIntent or AssistFeature.Translate or AssistFeature.ToneRewrite => null,
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };
}

/// <summary>
/// Metadata recorded on the timeline for an assistant action: feature, TicketFlow model identifier, the durable usage record, token
/// usage and cited article identifiers. Never prompts, customer content or output text.
/// </summary>
public sealed record AssistTimelinePayload(AssistFeature Feature, string Model, Guid UsageId, AssistUsage Usage, IReadOnlyList<ArticleId> Citations)
{
    public bool Equals(AssistTimelinePayload? other) =>
        other is not null && Feature == other.Feature && string.Equals(Model, other.Model, StringComparison.Ordinal) && UsageId == other.UsageId && Usage == other.Usage && Citations.SequenceEqual(other.Citations);

    public override int GetHashCode() => HashCode.Combine(Feature, Model, UsageId);
}

/// <summary>
/// Persisted assistant JSON shapes with explicit versions. Settings: <c>{"v":1,"enabled":..,"daily":..,"rpm":..,"models":{"&lt;feature
/// number&gt;":"model"}}</c>. Timeline payload (version 1): <c>{"feature":n,"model":..,"usage_id":..,"usage":{"in":..,"out":..,
/// "cache_read":..,"cache_write":..},"citations":[..]}</c>. Decoding rejects unknown versions, features and properties.
/// </summary>
public static class AssistJsonCodec
{
    public static string EncodeSettings(AssistSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Write(writer =>
        {
            writer.WriteNumber("v", 1);
            writer.WriteBoolean("enabled", settings.Enabled);
            writer.WriteNumber("daily", settings.DailyTokenLimit);
            writer.WriteNumber("rpm", settings.RequestsPerMinute);
            writer.WriteStartObject("models");
            foreach (var (feature, model) in settings.Models)
                writer.WriteString(((int)feature).ToString(System.Globalization.CultureInfo.InvariantCulture), model);
            writer.WriteEndObject();
        });
    }

    public static AssistSettings DecodeSettings(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = Object(document.RootElement, "v", "enabled", "daily", "rpm", "models");
        if (root.GetProperty("v").GetInt32() != 1)
            throw new FormatException("Unsupported assist settings version.");
        var models = new Dictionary<AssistFeature, string>();
        foreach (var property in Object(root.GetProperty("models")).EnumerateObject())
        {
            if (!int.TryParse(property.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var featureNumber))
                throw new FormatException("Unknown assist feature.");
            models[Feature(featureNumber)] = property.Value.GetString()
                ?? throw new FormatException("Model identifiers are strings.");
        }
        return new AssistSettings(root.GetProperty("enabled").GetBoolean(), root.GetProperty("daily").GetInt64(), root.GetProperty("rpm").GetInt32(), models);
    }

    public static string EncodeTimelinePayload(AssistTimelinePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Write(writer =>
        {
            writer.WriteNumber("feature", (int)payload.Feature);
            writer.WriteString("model", payload.Model);
            writer.WriteString("usage_id", payload.UsageId.ToString("D"));
            writer.WriteStartObject("usage");
            writer.WriteNumber("in", payload.Usage.InputTokens);
            writer.WriteNumber("out", payload.Usage.OutputTokens);
            writer.WriteNumber("cache_read", payload.Usage.CacheReadInputTokens);
            writer.WriteNumber("cache_write", payload.Usage.CacheCreationInputTokens);
            writer.WriteEndObject();
            writer.WriteStartArray("citations");
            foreach (var citation in payload.Citations)
                writer.WriteStringValue(citation.Value);
            writer.WriteEndArray();
        });
    }

    public static AssistTimelinePayload DecodeTimelinePayload(int payloadVersion, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (payloadVersion != AssistTimelineKinds.PayloadVersion)
            throw new FormatException("Unsupported assist timeline payload version.");
        using var document = JsonDocument.Parse(json);
        var root = Object(document.RootElement, "feature", "model", "usage_id", "usage", "citations");
        var usage = Object(root.GetProperty("usage"), "in", "out", "cache_read", "cache_write");
        return new AssistTimelinePayload(Feature(root.GetProperty("feature").GetInt32()), root.GetProperty("model").GetString()!, root.GetProperty("usage_id").GetGuid(),
            new AssistUsage(usage.GetProperty("in").GetInt64(), usage.GetProperty("out").GetInt64(), usage.GetProperty("cache_read").GetInt64(), usage.GetProperty("cache_write").GetInt64()),
            [.. root.GetProperty("citations").EnumerateArray().Select(item => new ArticleId(item.GetString()!))]);
    }

    private static AssistFeature Feature(int value) =>
        Enum.IsDefined((AssistFeature)value) ? (AssistFeature)value : throw new FormatException("Unknown assist feature.");

    private static JsonElement Object(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException("Expected a JSON object.");
        if (required.Length > 0)
        {
            var names = element.EnumerateObject().Select(property => property.Name).ToList();
            if (names.Count != required.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Count || !required.All(names.Contains))
                throw new FormatException("Unexpected or missing properties.");
        }
        return element;
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
