using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using TicketFlow.UI.Blazor.Theming;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>Reads and writes the theme cookie, so server rendering emits the chosen theme and there is no flash of the default.</summary>
public static class UiTheme
{
    public static TicketFlowThemePreference Read(HttpContext http, TicketFlowBlazorUIOptions options) =>
        TicketFlowThemePreference.Parse(http.Request.Cookies[options.ThemeCookieName], options.Themes) ?? options.DefaultTheme;

    public static void Write(HttpContext http, TicketFlowBlazorUIOptions options, TicketFlowThemePreference preference) =>
        http.Response.Cookies.Append(options.ThemeCookieName, preference.ToCookieValue(), new CookieOptions
        {
            Path = "/",
            MaxAge = TimeSpan.FromDays(365),
            SameSite = SameSiteMode.Lax,
            HttpOnly = false,
            Secure = http.Request.IsHttps,
            IsEssential = true,
        });
}

/// <summary>
/// One-shot outcome of a form post carried across the post/redirect/get cycle in a short-lived cookie. Only the outcome kind, title,
/// TicketFlow-produced validation messages and revision travel; revealed values and other sensitive data never do.
/// </summary>
internal static class UiFlash
{
    public const string CookieName = "tf-flash";
    private const int MaximumMessages = 8;
    private const int MaximumLength = 300;

    internal sealed record Payload(int K, string T, string[] M, long? R);

    public static void Write(HttpContext http, TicketFlowBlazorUIOptions options, UiOutcome outcome)
    {
        var payload = new Payload((int)outcome.Kind, Clip(outcome.Title), [.. outcome.Messages.Take(MaximumMessages).Select(Clip)], outcome.CurrentRevision);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, UiFlashJsonContext.Default.Payload);
        http.Response.Cookies.Append(CookieName, Base64Url.EncodeToString(json), new CookieOptions
        {
            Path = options.BasePath,
            MaxAge = TimeSpan.FromMinutes(1),
            SameSite = SameSiteMode.Lax,
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            IsEssential = true,
        });
    }

    public static UiOutcome? Take(HttpContext http, TicketFlowBlazorUIOptions options)
    {
        if (http.Request.Cookies[CookieName] is not { Length: > 0 and < 4096 } value)
            return null;
        http.Response.Cookies.Delete(CookieName, new CookieOptions { Path = options.BasePath });
        try
        {
            var payload = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(value), UiFlashJsonContext.Default.Payload);
            if (payload is null || !Enum.IsDefined((UiOutcomeKind)payload.K))
                return null;
            return new UiOutcome((UiOutcomeKind)payload.K, Clip(payload.T ?? string.Empty), [.. (payload.M ?? []).Take(MaximumMessages).Select(Clip)], payload.R);
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Clip(string value) => value.Length > MaximumLength ? value[..MaximumLength] : value;
}

[JsonSerializable(typeof(UiFlash.Payload))]
internal sealed partial class UiFlashJsonContext : JsonSerializerContext;
