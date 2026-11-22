using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;

namespace TicketFlow.Mcp;

/// <summary>
/// A tool outcome that is not a success: a stable <see cref="Code"/> (<c>unauthenticated</c>, <c>forbidden</c>, <c>not-found</c>,
/// <c>conflict</c>, <c>invalid</c>, <c>rejected</c>, <c>closed</c>, <c>merged</c>, <c>toolset-unavailable</c>, ...), a non-sensitive
/// message, optional validation messages, the current revision for conflicts and code-specific details. Returned as an MCP error
/// result (<c>isError</c>, structured content <c>{"error":{...}}</c>), never thrown.
/// </summary>
public sealed record TicketFlowToolError(string Code, string Message, IReadOnlyList<string>? Errors = null, long? CurrentRevision = null)
{
    /// <summary>For <c>forbidden</c>: the tool whose permissions are listed.</summary>
    public string? Tool { get; init; }

    public IReadOnlyList<string>? RequiredPermissions { get; init; }

    public bool? AnyOf { get; init; }

    public IReadOnlyList<string>? GrantedByBuiltInRoles { get; init; }

    public string? Hint { get; init; }

    /// <summary>For <c>merged</c>: the surviving contact.</summary>
    public string? MergedInto { get; init; }

    /// <summary>For audited refusals (<c>reveal-refused</c>).</summary>
    public Guid? AuditEventId { get; init; }

    public bool? Retryable { get; init; }

    public double? RetryAfterSeconds { get; init; }

    public static TicketFlowToolError Forbidden() => new("forbidden", "Access denied this operation for the calling principal.");

    public static TicketFlowToolError NotFound(string what = "The record") => new("not-found", $"{what} does not exist or is not visible to the calling principal.");

    public static TicketFlowToolError Conflict(long? currentRevision) =>
        new("conflict", "The record changed since the expected revision; re-read it and retry with the current revision.", CurrentRevision: currentRevision);

    public static TicketFlowToolError Invalid(IReadOnlyList<string>? errors) => new("invalid", "The request is invalid.", errors is { Count: > 0 } ? errors : null);

    public static TicketFlowToolError Rejected(IReadOnlyList<string>? errors) => new("rejected", "A store limit or contract rule refused the operation.", errors is { Count: > 0 } ? errors : null);
}

/// <summary>JSON conventions of TicketFlow tool results: source-generated web casing, omitted nulls, camelCase tokens, opaque cursors.</summary>
public static class TicketFlowToolJson
{
    /// <summary>Serializer options over <see cref="TicketFlowMcpJsonContext"/> (no reflection): every output type must be registered there.</summary>
    public static JsonSerializerOptions Options { get; } = TicketFlowMcpJsonContext.Default.Options;

    /// <summary>The camelCase token of an enum value (<c>WhatsApp</c> → <c>whatsApp</c>).</summary>
    public static string Token<TEnum>(TEnum value) where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    public static string? Token<TEnum>(TEnum? value) where TEnum : struct, Enum => value is { } some ? Token(some) : null;

    /// <summary>Parses a camelCase or PascalCase token of <typeparamref name="TEnum"/>; throws <see cref="ArgumentException"/> naming <paramref name="parameter"/>.</summary>
    public static TEnum Parse<TEnum>(string value, string parameter) where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length > 0 && char.IsAsciiLetter(value[0]) && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            return parsed;
        throw new ArgumentException($"'{parameter}' must be one of: {string.Join(", ", Enum.GetValues<TEnum>().Select(item => Token(item)))}.", parameter);
    }

    public static TEnum? ParseOptional<TEnum>(string? value, string parameter) where TEnum : struct, Enum =>
        value is null ? null : Parse<TEnum>(value, parameter);

    /// <summary>An opaque cursor of 1-4 parts (base64url of the joined parts).</summary>
    public static string EncodeCursor(params string[] parts) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Join('\n', parts)));

    /// <summary>Decodes a cursor produced by <see cref="EncodeCursor"/> with exactly <paramref name="count"/> parts.</summary>
    public static string[] DecodeCursor(string cursor, int count)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        try
        {
            if (cursor.Length is 0 or > 2048)
                throw new FormatException();
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('\n');
            if (parts.Length != count)
                throw new FormatException();
            return parts;
        }
        catch (FormatException)
        {
            throw new ArgumentException("The cursor is invalid; pass back the 'next' value of a previous page unchanged.", nameof(cursor));
        }
    }

    public static string Instant(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseInstant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The output schema of a tool returning <paramref name="outputType"/>: the type's schema (from the source-generated context) with an
    /// optional <c>error</c> property describing error results, and without root <c>required</c> (error results carry only <c>error</c>).
    /// </summary>
    public static JsonElement OutputSchema(Type outputType)
    {
        ArgumentNullException.ThrowIfNull(outputType);
        var schema = JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(outputType, serializerOptions: Options).GetRawText())!.AsObject();
        schema["type"] = "object";
        schema.Remove("required");
        var properties = schema["properties"] as JsonObject ?? [];
        properties["error"] = JsonNode.Parse(AIJsonUtilities.CreateJsonSchema(typeof(TicketFlowToolError), serializerOptions: Options).GetRawText());
        schema["properties"] = properties;
        return Parse(schema.ToJsonString());
    }
}

/// <summary>Builds MCP <see cref="CallToolResult"/>s: JSON structured content plus the same JSON as one text block.</summary>
public static class TicketFlowToolResults
{
    /// <summary>
    /// A success result for <paramref name="value"/> (a type registered in <see cref="TicketFlowMcpJsonContext"/>). The bound applies to
    /// the whole payload: structured content plus its text copy.
    /// </summary>
    public static CallToolResult Success(object value, int maximumBytes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, TicketFlowToolJson.Options.GetTypeInfo(value.GetType()));
        if ((long)bytes.Length * 2 > maximumBytes)
            return Error(new TicketFlowToolError("result-too-large",
                $"The result exceeds {maximumBytes} bytes; request a smaller page (lower 'limit') or a narrower range."));
        return Create(bytes, isError: false);
    }

    /// <summary>An error result (<c>isError</c>) for <paramref name="error"/>.</summary>
    public static CallToolResult Error(TicketFlowToolError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Create(JsonSerializer.SerializeToUtf8Bytes(new ToolErrorEnvelope(error), TicketFlowMcpJsonContext.Default.ToolErrorEnvelope), isError: true);
    }

    private static CallToolResult Create(byte[] bytes, bool isError)
    {
        using var document = JsonDocument.Parse(bytes);
        return new CallToolResult
        {
            StructuredContent = document.RootElement.Clone(),
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(bytes) }],
            IsError = isError ? true : null,
        };
    }
}
