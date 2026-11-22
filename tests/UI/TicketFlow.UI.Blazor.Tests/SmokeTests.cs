using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using TicketFlow.SampleHost;

namespace TicketFlow.UI.Blazor.Tests;

/// <summary>
/// Renders the UI through the sample host (in-memory stores, seeded demo data, development authentication) with TestServer:
/// static SSR pages, permission gating, sign-in redirects and antiforgery-protected form posts with revision fencing.
/// </summary>
[TestClass]
public sealed partial class SmokeTests
{
    private static WebApplication app = default!;

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        app = SampleHostApp.Build(["--environment", "Development"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TicketFlow:Storage"] = "InMemory",
                ["TicketFlow:DevelopmentAuthentication"] = "true",
            });
        });
        await SampleHostApp.InitializeAsync(app, seed: true);
        await app.StartAsync();
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private static HttpClient Client(string? user)
    {
        var client = app.GetTestServer().CreateClient();
        if (user is not null)
            client.DefaultRequestHeaders.Add("X-Dev-User", user);
        return client;
    }

    [TestMethod]
    [DataRow("/support", "Good ")]
    [DataRow("/support/conversations", "Olivia Bennett")]
    [DataRow("/support/conversations/c-1001", "charged twice")]
    [DataRow("/support/contacts", "Northwind Traders")]
    [DataRow("/support/contacts/c-olivia", "Reveal")]
    [DataRow("/support/reports", "Agent leaderboard")]
    [DataRow("/support/inboxes", "Email support")]
    [DataRow("/support/teams", "Escalations")]
    [DataRow("/support/calendars", "Support hours")]
    [DataRow("/support/channels", "support@acme.test")]
    [DataRow("/support/routing", "Chat round robin")]
    [DataRow("/support/sla", "Standard support")]
    [DataRow("/support/automation", "Tag refund requests")]
    [DataRow("/support/automation/rules/tag-refunds", "Dry run")]
    [DataRow("/support/automation/macros", "Escalate to Tier 2")]
    [DataRow("/support/automation/canned", "/refund")]
    [DataRow("/support/help-center", "Refunds and returns")]
    [DataRow("/support/help-center/articles/sso-setup", "Preview")]
    [DataRow("/support/surveys", "Customer satisfaction")]
    [DataRow("/support/access", "Billing specialist")]
    [DataRow("/support/access/roles/billing-specialist", "conversation:read")]
    [DataRow("/support/access/grants", "New grant")]
    [DataRow("/support/access/field-policies", "contact.phone")]
    [DataRow("/support/access/break-glass", "Activate")]
    [DataRow("/support/settings", "Acme Support")]
    [DataRow("/support/settings/theme", "Color theme")]
    public async Task AdminPagesRenderWithStaticSsr(string path, string expected)
    {
        var response = await Client("admin").GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(html, expected, path);
        StringAssert.Contains(html, "data-tf-theme=\"zinc\"");
        Assert.IsTrue(response.Headers.CacheControl?.NoStore, "Support data is never cached.");
    }

    [TestMethod]
    public async Task UnauthenticatedUsersAreSentToTheHostSignIn()
    {
        var response = await Client(null).GetAsync("/support/conversations");
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("/dev/login?returnUrl=%2Fsupport%2Fconversations", response.Headers.Location?.OriginalString);
    }

    [TestMethod]
    public async Task ViewerCannotOpenAdministrationAndDoesNotSeeIt()
    {
        var client = Client("viewer");
        var dashboard = await (await client.GetAsync("/support")).Content.ReadAsStringAsync();
        Assert.IsFalse(dashboard.Contains("href=\"/support/access\"", StringComparison.Ordinal), "Viewers do not hold access:manage.");
        StringAssert.Contains(dashboard, "href=\"/support/conversations\"");

        var access = await client.GetAsync("/support/access");
        Assert.AreEqual(HttpStatusCode.Forbidden, access.StatusCode);
        StringAssert.Contains(await access.Content.ReadAsStringAsync(), "Access denied");
    }

    [TestMethod]
    public async Task UnmappedUsersGetForbiddenNotData()
    {
        var response = await Client("stranger").GetAsync("/support/conversations");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("Olivia", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PostsRequireAntiforgery()
    {
        var response = await Client("admin").PostAsync("/support/conversations/c-1010", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_action"] = "conversation.send",
            ["body"] = "Hello",
        }));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task NoteIsAddedWithPostRedirectGetAndStaleRevisionsConflict()
    {
        var client = Client("admin");
        var (token, cookie, revision) = await FormAsync(client, "/support/conversations/c-1010");

        var posted = await PostAsync(client, "/support/conversations/c-1010", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "conversation.send",
            ["_return"] = "/support/conversations/c-1010",
            ["_revision"] = revision,
            ["kind"] = "note",
            ["body"] = "Checked the export settings for Ethan.",
        });
        Assert.AreEqual(HttpStatusCode.Redirect, posted.StatusCode);
        Assert.AreEqual("/support/conversations/c-1010", posted.Headers.Location?.OriginalString);
        var flash = posted.Headers.GetValues("Set-Cookie").First(value => value.StartsWith("tf-flash=", StringComparison.Ordinal)).Split(';')[0];

        using var follow = new HttpRequestMessage(HttpMethod.Get, "/support/conversations/c-1010");
        follow.Headers.Add("Cookie", flash);
        var page = await (await client.SendAsync(follow)).Content.ReadAsStringAsync();
        StringAssert.Contains(page, "Note added");
        StringAssert.Contains(page, "Checked the export settings for Ethan.");

        var stale = await PostAsync(client, "/support/conversations/c-1010", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "conversation.send",
            ["_revision"] = revision,
            ["kind"] = "note",
            ["body"] = "A second note from a stale page.",
        });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        var html = await stale.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "Someone else changed this record");
        StringAssert.Contains(html, "A second note from a stale page.", "The re-rendered form keeps what the agent typed.");
    }

    [TestMethod]
    public async Task ForbiddenWritesShowAFriendlyMessage()
    {
        var client = Client("viewer");
        var (token, cookie, revision) = await FormAsync(client, "/support/conversations/c-1010");
        var response = await PostAsync(client, "/support/conversations/c-1010", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "conversation.priority",
            ["_revision"] = revision,
            ["priority"] = "Urgent",
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "You don&#x27;t have permission to do that");
    }

    [TestMethod]
    public async Task ThemePostPersistsTheChoiceInACookie()
    {
        var client = Client("agent1");
        var (token, cookie, _) = await FormAsync(client, "/support/settings/theme");
        var response = await PostAsync(client, "/support/settings/theme", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "theme.set",
            ["_return"] = "/support/settings/theme",
            ["theme"] = "blue",
            ["mode"] = "light",
        });
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var themeCookie = response.Headers.GetValues("Set-Cookie").First(value => value.StartsWith("tf-theme=", StringComparison.Ordinal)).Split(';')[0];
        Assert.AreEqual("tf-theme=blue.light", themeCookie);

        using var follow = new HttpRequestMessage(HttpMethod.Get, "/support");
        follow.Headers.Add("Cookie", themeCookie);
        var html = await (await client.SendAsync(follow)).Content.ReadAsStringAsync();
        StringAssert.Contains(html, "data-tf-theme=\"blue\"");
        Assert.IsFalse(Regex.IsMatch(html, "<html[^>]*class=\"dark\""));
    }

    [TestMethod]
    public async Task ReportExportStreamsCsvForAdmins()
    {
        var response = await Client("admin").GetAsync("/support/reports/export.csv");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
        StringAssert.StartsWith(await response.Content.ReadAsStringAsync(), "source_id,metric,occurred_at");
    }

    private static async Task<(string Token, string Cookie, string Revision)> FormAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        var token = TokenPattern().Match(html).Groups[1].Value;
        var revision = RevisionPattern().Match(html).Groups[1].Value;
        var cookie = response.Headers.GetValues("Set-Cookie").First(value => value.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal)).Split(';')[0];
        Assert.IsFalse(string.IsNullOrEmpty(token));
        return (token, cookie, revision);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string cookie, Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("name=\"_revision\" value=\"(\\d+)\"")]
    private static partial Regex RevisionPattern();
}
