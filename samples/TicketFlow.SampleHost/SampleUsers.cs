using System.Net;
using TicketFlow.Core;
using TicketFlow.UI.Blazor.Infrastructure;

namespace TicketFlow.SampleHost;

/// <summary>Demo staff directory: display names for the seeded user identifiers (applications own user profiles).</summary>
public static class SampleUsers
{
    public sealed record SampleUser(string Id, string DisplayName, string Email, string Role);

    public static IReadOnlyList<SampleUser> All { get; } =
    [
        new("admin", "Alex Morgan", "alex.morgan@acme.test", "Owner"),
        new("agent1", "Jamie Chen", "jamie.chen@acme.test", "Agent · Tier 1"),
        new("agent2", "Priya Patel", "priya.patel@acme.test", "Agent · Tier 1"),
        new("agent3", "Sam Rivera", "sam.rivera@acme.test", "Agent · Billing"),
        new("supervisor", "Morgan Lee", "morgan.lee@acme.test", "Supervisor"),
        new("viewer", "Taylor Brooks", "taylor.brooks@acme.test", "Viewer"),
    ];

    public static SampleUser? Find(string id) => All.FirstOrDefault(user => user.Id == id);
}

internal sealed class SampleUserDirectory : ITicketFlowUserDirectory
{
    public ValueTask<TicketFlowUserProfile?> FindAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(SampleUsers.Find(user.Value) is { } found ? new TicketFlowUserProfile(found.DisplayName, found.Email) : null);

    /// <summary>One lookup for every user a page shows (the UI calls this once per page).</summary>
    public ValueTask<IReadOnlyDictionary<UserId, TicketFlowUserProfile>> FindManyAsync(TicketScope scope, IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyDictionary<UserId, TicketFlowUserProfile>>(users.Distinct()
            .Select(user => (user, found: SampleUsers.Find(user.Value)))
            .Where(pair => pair.found is not null)
            .ToDictionary(pair => pair.user, pair => new TicketFlowUserProfile(pair.found!.DisplayName, pair.found.Email)));
}

/// <summary>DEVELOPMENT ONLY sign-in page for the support UI: pick a seeded user; the choice is stored in the development cookie.</summary>
internal static class DevelopmentLogin
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/dev/login", (HttpContext http, string? user, string? returnUrl) =>
        {
            var target = SafeReturnUrl(returnUrl);
            if (user is not null && SampleUsers.Find(user) is not null)
            {
                http.Response.Cookies.Append(DevelopmentAuthenticationHandler.CookieName, user, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
                return Results.Redirect(target);
            }
            var links = string.Concat(SampleUsers.All.Select(sample =>
                $"<a href=\"/dev/login?user={sample.Id}&amp;returnUrl={WebUtility.UrlEncode(target)}\"><strong>{WebUtility.HtmlEncode(sample.DisplayName)}</strong><span>{WebUtility.HtmlEncode(sample.Role)} · {sample.Id}</span></a>"));
            return Results.Content($$"""
                <!doctype html><html lang="en" class="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Sign in · TicketFlow sample</title>
                <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#09090b;color:#fafafa;font:14px/1.5 ui-sans-serif,system-ui,sans-serif}
                main{width:min(380px,90vw);border:1px solid #27272a;border-radius:12px;padding:24px;background:#18181b}h1{font-size:18px;margin:0 0 4px}p{color:#a1a1aa;margin:0 0 16px}
                a{display:flex;justify-content:space-between;gap:12px;padding:10px 12px;border:1px solid #27272a;border-radius:8px;color:inherit;text-decoration:none;margin-top:8px}a:hover{background:#27272a}a span{color:#a1a1aa}</style></head>
                <body><main><h1>Development sign-in</h1><p>Choose a seeded user. Development only — replace with your real authentication.</p>{{links}}</main></body></html>
                """, "text/html");
        }).ExcludeFromDescription();
        app.MapGet("/dev/logout", (HttpContext http) =>
        {
            http.Response.Cookies.Delete(DevelopmentAuthenticationHandler.CookieName, new CookieOptions { Path = "/" });
            return Results.Redirect("/dev/login");
        }).ExcludeFromDescription();
    }

    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl[0] != '/' || returnUrl.StartsWith("//", StringComparison.Ordinal))
            return "/support";
        for (var i = 0; i < returnUrl.Length; i++)
        {
            var c = returnUrl[i];
            if (c < 0x20 || c == 0x7f || c == '\\')
                return "/support";
        }
        return returnUrl;
    }
}

/// <summary>An adjustable clock: the seeder moves it into the past so demo history, reports and SLA clocks look realistic.</summary>
public sealed class SampleClock : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
