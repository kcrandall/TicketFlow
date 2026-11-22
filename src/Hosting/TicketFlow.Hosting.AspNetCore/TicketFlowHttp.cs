using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TicketFlow.Hosting.AspNetCore;

/// <summary>
/// HTTP conventions of TicketFlow endpoints: ProblemDetails with a stable <c>code</c> extension (plus <c>errors</c> and
/// <c>currentRevision</c> where applicable), strong revision ETags (<c>"7"</c>) and <c>If-Match</c> fencing.
/// </summary>
public static class TicketFlowHttp
{
    /// <summary>Prefix of every problem <c>type</c>; the suffix equals the <c>code</c> extension.</summary>
    public const string ProblemTypePrefix = "urn:ticketflow:problem:";

    /// <summary>The strong entity tag of a revision: the decimal revision in quotes.</summary>
    public static string ETag(long revision) => "\"" + revision.ToString(CultureInfo.InvariantCulture) + "\"";

    /// <summary>Parses <c>If-Match: "7"</c>. Missing → <see langword="null"/>; malformed, weak or lists → false.</summary>
    public static bool TryParseIfMatch(HttpContext httpContext, out long? revision)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        revision = null;
        var values = httpContext.Request.Headers.IfMatch;
        if (values.Count == 0)
            return true;
        if (values.Count != 1 || values[0] is not { } header)
            return false;
        header = header.Trim();
        if (header.Length < 3 || header[0] != '"' || header[^1] != '"')
            return false;
        if (!long.TryParse(header.AsSpan(1, header.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            return false;
        revision = parsed;
        return true;
    }

    internal static bool HasIfMatch(HttpContext httpContext) => httpContext.Request.Headers.IfMatch.Count > 0;

    /// <summary>Requires a valid <c>If-Match</c>: 428 when missing, 400 when malformed.</summary>
    internal static bool TryRequireRevision(HttpContext httpContext, out long revision, out IResult failure)
    {
        revision = 0;
        failure = null!;
        if (!TryParseIfMatch(httpContext, out var parsed))
        {
            failure = Problem(StatusCodes.Status400BadRequest, "invalid-if-match", ["If-Match must be one strong revision tag such as \"3\"."]);
            return false;
        }
        if (parsed is not { } value)
        {
            failure = Problem(StatusCodes.Status428PreconditionRequired, "precondition-required", ["This write requires an If-Match revision tag."]);
            return false;
        }
        revision = value;
        return true;
    }

    /// <summary>Reads an optional <c>If-Match</c>: 400 when malformed.</summary>
    internal static bool TryOptionalRevision(HttpContext httpContext, out long? revision, out IResult failure)
    {
        failure = null!;
        if (TryParseIfMatch(httpContext, out revision))
            return true;
        failure = Problem(StatusCodes.Status400BadRequest, "invalid-if-match", ["If-Match must be one strong revision tag such as \"3\"."]);
        return false;
    }

    internal static IResult Problem(int status, string code, IEnumerable<string>? errors = null, long? currentRevision = null, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = ProblemTypePrefix + code,
            Title = Title(status),
        };
        problem.Extensions["code"] = code;
        if (errors?.ToArray() is { Length: > 0 } list)
            problem.Extensions["errors"] = list;
        if (currentRevision is { } revision)
            problem.Extensions["currentRevision"] = revision;
        if (extensions is not null)
            foreach (var (key, value) in extensions)
                problem.Extensions[key] = value;
        return TypedResults.Problem(problem);
    }

    internal static IResult Ok<T>(HttpContext httpContext, T body, long? revision = null)
    {
        if (revision is { } value)
            httpContext.Response.Headers.ETag = ETag(value);
        return TypedResults.Ok(body);
    }

    internal static IResult Created<T>(HttpContext httpContext, string location, T body, long? revision = null)
    {
        if (revision is { } value)
            httpContext.Response.Headers.ETag = ETag(value);
        return TypedResults.Created(location, body);
    }

    /// <summary>A revision conflict: 412 when the request was fenced with <c>If-Match</c>, otherwise 409 (for example create of an existing record).</summary>
    internal static IResult Conflict(HttpContext httpContext, long currentRevision, IEnumerable<string>? errors = null)
    {
        httpContext.Response.Headers.ETag = ETag(currentRevision);
        return HasIfMatch(httpContext)
            ? Problem(StatusCodes.Status412PreconditionFailed, "revision-mismatch", errors, currentRevision)
            : Problem(StatusCodes.Status409Conflict, "conflict", errors, currentRevision);
    }

    internal static IResult Refused(HttpContext httpContext, RefusalKind kind, IReadOnlyList<string>? errors = null, long? currentRevision = null) => kind switch
    {
        RefusalKind.Forbidden => Problem(StatusCodes.Status403Forbidden, "forbidden", errors),
        RefusalKind.Invalid => Problem(StatusCodes.Status422UnprocessableEntity, "invalid", errors),
        RefusalKind.NotFound => Problem(StatusCodes.Status404NotFound, "not-found", errors),
        RefusalKind.Conflict => currentRevision is { } revision ? Conflict(httpContext, revision, errors) : Problem(StatusCodes.Status409Conflict, "conflict", errors),
        RefusalKind.Rejected => Problem(StatusCodes.Status409Conflict, "rejected", errors),
        RefusalKind.Closed => Problem(StatusCodes.Status409Conflict, "closed", errors),
        RefusalKind.Unavailable => Problem(StatusCodes.Status503ServiceUnavailable, "unavailable", errors),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static IResult NotFound() => Problem(StatusCodes.Status404NotFound, "not-found");

    internal static IResult Forbidden() => Problem(StatusCodes.Status403Forbidden, "forbidden");

    /// <summary>The camelCase token of an enum value, for example <c>firstResponse</c>.</summary>
    internal static string Token<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        return name.Length == 0 || !char.IsUpper(name[0]) ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    internal static string? Token<T>(T? value) where T : struct, Enum => value is { } some ? Token(some) : null;

    /// <summary>Parses a defined enum token (case-insensitive, names only). Unknown values throw <see cref="ArgumentException"/> (422).</summary>
    internal static T Parse<T>(string? value, string name) where T : struct, Enum
    {
        if (value is null || value.Length is 0 or > 64 || !value.All(char.IsAsciiLetter) || !Enum.TryParse<T>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"'{name}' must be one of: {string.Join(", ", Enum.GetValues<T>().Select(item => Token(item)))}.", name);
        return parsed;
    }

    internal static T? ParseOptional<T>(string? value, string name) where T : struct, Enum => value is null ? null : Parse<T>(value, name);

    /// <summary>Encodes cursor parts as an opaque base64url token.</summary>
    internal static string EncodeCursor(params string[] parts) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(parts));

    /// <summary>Decodes a token from <see cref="EncodeCursor"/> with exactly <paramref name="count"/> parts; malformed tokens throw <see cref="ArgumentException"/>.</summary>
    internal static string[] DecodeCursor(string cursor, int count)
    {
        try
        {
            if (cursor.Length > 4096)
                throw new FormatException();
            var parts = JsonSerializer.Deserialize<string[]>(Base64Url.DecodeFromChars(cursor));
            if (parts is null || parts.Length != count || parts.Any(part => part is null))
                throw new FormatException();
            return parts;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new ArgumentException("The cursor is invalid.", nameof(cursor));
        }
    }

    internal static long MaximumBody(HttpContext httpContext) =>
        httpContext.RequestServices.GetRequiredService<IOptions<TicketFlowEndpointOptions>>().Value.MaximumRequestBodyBytes;

    private static string Title(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status412PreconditionFailed => "Precondition failed",
        StatusCodes.Status413PayloadTooLarge => "Payload too large",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable entity",
        StatusCodes.Status428PreconditionRequired => "Precondition required",
        StatusCodes.Status429TooManyRequests => "Too many requests",
        StatusCodes.Status502BadGateway => "Bad gateway",
        StatusCodes.Status503ServiceUnavailable => "Service unavailable",
        _ => "Error",
    };

    internal static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}

/// <summary>Normalized refusal kinds of the feature result unions.</summary>
internal enum RefusalKind
{
    Forbidden = 1,
    Invalid = 2,
    NotFound = 3,
    Conflict = 4,
    Rejected = 5,
    Closed = 6,
    Unavailable = 7,
}
