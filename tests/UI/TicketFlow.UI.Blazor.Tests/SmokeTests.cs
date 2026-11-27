using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Assist;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.SampleHost;
using TicketFlow.Workspaces;

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
            builder.Services.AddSingleton<IAssistService, TestAssist>();
        });
        await SampleHostApp.InitializeAsync(app, seed: true);
        await ProvisionTeamScopedAgentAsync(app.Services);
        await app.StartAsync();
    }

    public const string TeamAgent = "tier1lead";

    /// <summary>A user whose only grant is the Agent role on the Tier 1 team (no workspace-level grant).</summary>
    private static async Task ProvisionTeamScopedAgentAsync(IServiceProvider root)
    {
        await using var scope = root.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var settings = services.GetRequiredService<SampleSettings>();
        var tier1 = new TeamId("tier1");
        var teams = services.GetRequiredService<ITeamStore>();
        var team = (await teams.GetAsync(settings.Scope, tier1))!;
        await teams.UpdateAsync(settings.Scope, new Team(tier1, team.Team.Name, team.Team.Description, [.. team.Team.Members, new UserId(TeamAgent)]), team.Revision);
        await services.GetRequiredService<IAccessGrantStore>().CreateAsync(new AccessGrant(new GrantId("test-team-agent"), settings.Partition, new UserId(TeamAgent),
            BuiltInRoles.Agent.Id, new TeamGrantScope(settings.Workspace, tier1)));
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
    [DataRow("/support/access/service-accounts", "Ticket Sync Worker")]
    [DataRow("/support/access/federation", "idf:support-agents")]
    [DataRow("/support/settings", "Acme Support")]
    [DataRow("/support/settings/theme", "Color theme")]
    public async Task AdminPagesRenderWithStaticSsr(string path, string expected)
    {
        var response = await Client("admin").GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(html, expected, StringComparison.Ordinal, path);
        StringAssert.Contains(html, "data-tf-theme=\"zinc\"", StringComparison.Ordinal);
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
        StringAssert.Contains(dashboard, "href=\"/support/conversations\"", StringComparison.Ordinal);

        var access = await client.GetAsync("/support/access");
        Assert.AreEqual(HttpStatusCode.Forbidden, access.StatusCode);
        StringAssert.Contains(await access.Content.ReadAsStringAsync(), "Access denied", StringComparison.Ordinal);
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
        StringAssert.Contains(page, "Note added", StringComparison.Ordinal);
        StringAssert.Contains(page, "Checked the export settings for Ethan.", StringComparison.Ordinal);

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
        StringAssert.Contains(html, "Someone else changed this record", StringComparison.Ordinal);
        StringAssert.Contains(html, "A second note from a stale page.", StringComparison.Ordinal, "The re-rendered form keeps what the agent typed.");
    }

    [TestMethod]
    public async Task ForbiddenWritesShowAFriendlyMessage()
    {
        var client = Client("viewer");
        var (token, cookie, _) = await FormAsync(client, "/support/conversations/c-1010");
        var (_, _, current) = await FormAsync(Client("admin"), "/support/conversations/c-1010");   // viewers see no fenced forms
        var response = await PostAsync(client, "/support/conversations/c-1010", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "conversation.priority",
            ["_revision"] = current,
            ["priority"] = "Urgent",
        });
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "You don&#x27;t have permission to do that", StringComparison.Ordinal);
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
        StringAssert.Contains(html, "data-tf-theme=\"blue\"", StringComparison.Ordinal);
        Assert.IsFalse(Regex.IsMatch(html, "<html[^>]*class=\"dark\"", RegexOptions.None, TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task ReportExportStreamsCsvForAdmins()
    {
        var response = await Client("admin").GetAsync("/support/reports/export.csv");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
        StringAssert.StartsWith(await response.Content.ReadAsStringAsync(), "source_id,metric,occurred_at", StringComparison.Ordinal);
    }

    private static readonly (string Path, string Action)[] Sections =
    [
        ("/support/conversations", "conversation.priority"), ("/support/contacts", "contact.create"), ("/support/reports", "report.export"),
        ("/support/inboxes", "inbox.create"), ("/support/teams", "team.create"), ("/support/calendars", "calendar.create"),
        ("/support/channels", "channel.endpoint.create"), ("/support/routing", "routing.policy.save"), ("/support/sla", "sla.policy.save"),
        ("/support/automation", "automation.rule.save"), ("/support/help-center", "helpcenter.collection.create"), ("/support/surveys", "survey.toggle"),
        ("/support/access", "access.role.save"), ("/support/access/grants", "access.grant.create"), ("/support/access/field-policies", "access.field.create"),
        ("/support/access/break-glass", "access.breakglass"), ("/support/access/service-accounts", "access.serviceaccount.create"),
        ("/support/access/federation", "access.federation.create"), ("/support/settings", "settings.save"),
    ];

    [TestMethod]
    public async Task PostsCannotReachSectionsTheViewerCannotOpen()
    {
        var client = Client("viewer");
        var (token, cookie, _) = await FormAsync(client, "/support");
        var denied = 0;
        foreach (var (path, action) in Sections)
        {
            var page = await client.GetAsync(path);
            if (page.StatusCode != HttpStatusCode.Forbidden)
                continue;
            denied++;
            var post = await PostAsync(client, path, cookie, new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["_action"] = action,
                ["_revision"] = "1",
                ["name"] = "Sweep " + action,
                ["newid"] = "sweep-role",
                ["permission"] = "conversation:read",
            });
            Assert.AreEqual(HttpStatusCode.Forbidden, post.StatusCode, path);
            StringAssert.Contains(await post.Content.ReadAsStringAsync(), "Access denied", StringComparison.Ordinal, path);
        }
        Assert.IsGreaterThanOrEqualTo(5, denied, "The viewer is denied several sections (access, automation, channels, ...).");
        var roles = await (await Client("admin").GetAsync("/support/access")).Content.ReadAsStringAsync();
        Assert.IsFalse(roles.Contains("sweep-role", StringComparison.Ordinal), "A denied POST never runs its action.");
    }

    [TestMethod]
    public async Task ActionsOnlyRunOnTheirOwnSection()
    {
        var client = Client("admin");
        var (token, cookie, _) = await FormAsync(client, "/support/conversations");
        var response = await PostAsync(client, "/support/conversations", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "access.role.save",
            ["name"] = "Smuggled",
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task TeamScopedAgentSeesTeamConversations()
    {
        var client = Client(TeamAgent);
        var dashboard = await client.GetAsync("/support");
        var home = await dashboard.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, dashboard.StatusCode);
        StringAssert.Contains(home, "href=\"/support/conversations\"", StringComparison.Ordinal, "Team grants count for navigation.");
        StringAssert.Contains(home, "Recent activity", StringComparison.Ordinal);
        // Contacts are not team-scoped resources, so a team-only agent sees conversations but not contact names.
        Assert.IsTrue(new[] { "c-marcus", "c-mia", "c-hannah", "c-chloe" }.Any(home.Contains), "The dashboard shows the team's conversations.");
        Assert.IsFalse(home.Contains("c-james", StringComparison.Ordinal), "Escalations conversations stay hidden.");

        var list = await client.GetAsync("/support/conversations");
        var html = await list.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
        StringAssert.Contains(html, "/support/conversations/c-1002", StringComparison.Ordinal);
        Assert.IsFalse(html.Contains("/support/conversations/c-1004", StringComparison.Ordinal), "Billing-team conversations stay hidden.");
        Assert.IsFalse(html.Contains("href=\"/support/access\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConversationCommandsRequireTheExpectedRevision()
    {
        var client = Client("admin");
        var (token, cookie, _) = await FormAsync(client, "/support/conversations/c-1015");
        var response = await PostAsync(client, "/support/conversations/c-1015", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "conversation.assign",
            ["assignee"] = "agent2",
        });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "missing its revision", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ContactMergeIsFencedOnTheConfirmedDuplicateRevision()
    {
        var client = Client("admin");
        var (token, cookie, revision) = await FormAsync(client, "/support/contacts/c-leo");
        var preview = await PostAsync(client, "/support/contacts/c-leo", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "contact.merge.preview",
            ["_revision"] = revision,
            ["source"] = "c-amelia",
        });
        var html = await preview.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
        StringAssert.Contains(html, "name=\"sourceRevision\"", StringComparison.Ordinal);
        Assert.IsTrue(preview.Headers.CacheControl?.NoStore);

        var stale = await PostAsync(client, "/support/contacts/c-leo", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "contact.merge",
            ["_revision"] = revision,
            ["source"] = "c-amelia",
            ["sourceRevision"] = "999",
        });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [TestMethod]
    public async Task RevealedValuesAiDraftsAndDryRunsAreNeverCached()
    {
        var client = Client("admin");
        var (token, cookie, revision) = await FormAsync(client, "/support/contacts/c-olivia");
        var reveal = await PostAsync(client, "/support/contacts/c-olivia", cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "contact.reveal",
            ["field"] = "contact.phone",
            ["justification"] = "Customer asked for a callback",
        });
        Assert.AreEqual(HttpStatusCode.OK, reveal.StatusCode);
        Assert.IsTrue(reveal.Headers.CacheControl?.NoStore, "reveal");
        var revealed = await reveal.Content.ReadAsStringAsync();
        StringAssert.Contains(revealed, "14155550142", StringComparison.Ordinal);
        Assert.IsFalse(reveal.Headers.TryGetValues("Set-Cookie", out var revealCookies) && revealCookies.Any(value => value.StartsWith("tf-flash", StringComparison.Ordinal)));

        var (conversationToken, conversationCookie, conversationRevision) = await FormAsync(client, "/support/conversations/c-1001");
        var draft = await PostAsync(client, "/support/conversations/c-1001", conversationCookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = conversationToken,
            ["_action"] = "conversation.assist.draft",
            ["_revision"] = conversationRevision,
        });
        Assert.AreEqual(HttpStatusCode.OK, draft.StatusCode);
        Assert.IsTrue(draft.Headers.CacheControl?.NoStore, "AI draft");
        StringAssert.Contains(await draft.Content.ReadAsStringAsync(), TestAssist.Draft, StringComparison.Ordinal);

        var (ruleToken, ruleCookie, _) = await FormAsync(client, "/support/automation/rules/tag-refunds");
        var rulePage = await (await client.GetAsync("/support/automation/rules/tag-refunds")).Content.ReadAsStringAsync();
        var definition = WebUtility.HtmlDecode(Regex.Match(rulePage, "<textarea name=\"definition\"[^>]*>(?<v>[^<]*)</textarea>", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1)).Groups["v"].Value);
        var dryRun = await PostAsync(client, "/support/automation/rules/tag-refunds", ruleCookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ruleToken,
            ["_action"] = "automation.rule.dryrun",
            ["definition"] = definition,
            ["conversation"] = "c-1001",
            ["event"] = "message-received",
            ["message"] = "I want a refund",
        });
        Assert.AreEqual(HttpStatusCode.OK, dryRun.StatusCode);
        Assert.IsTrue(dryRun.Headers.CacheControl?.NoStore, "dry run");
        StringAssert.Contains(await dryRun.Content.ReadAsStringAsync(), "Dry run complete", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task SavingMultiLocaleArticlePreservesOtherTranslations()
    {
        var articleId = new ArticleId("multi-locale-test");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SampleSettings>();
            var helpCenter = scope.ServiceProvider.GetRequiredService<IHelpCenterService>();
            var admin = new UserPrincipal(settings.Scope, new UserId("admin"));
            var portalId = new PortalId("multi-locale-portal");
            var portal = new Portal(portalId, new HelpCenterSlug("multi-locale"), "Multi Locale", new HelpCenterLocale("en"),
                [new HelpCenterLocale("en"), new HelpCenterLocale("fr")]);
            Assert.IsInstanceOfType<TicketValue<StoredPortal>>((await helpCenter.CreatePortalAsync(admin, portal)).Value);
            var seeded = new Article(articleId, portalId,
            [
                new ArticleTranslation(new HelpCenterLocale("en"), "English Title", new HelpCenterSlug("english-title"), "English body"),
                new ArticleTranslation(new HelpCenterLocale("fr"), "Titre Francais", new HelpCenterSlug("titre-francais"), "Contenu francais"),
            ], position: 3);
            Assert.IsInstanceOfType<TicketValue<StoredArticle>>((await helpCenter.CreateArticleAsync(admin, seeded)).Value);
        }

        var client = Client("admin");
        var frenchPath = $"/support/help-center/articles/{articleId.Value}?locale=fr";
        var frenchGet = await client.GetAsync(frenchPath);
        var frenchHtml = await frenchGet.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, frenchGet.StatusCode);
        StringAssert.Contains(frenchHtml, "Titre Francais", StringComparison.Ordinal);
        StringAssert.Contains(frenchHtml, "name=\"locale\" value=\"fr\"", StringComparison.Ordinal);

        var (token, cookie, revision) = await FormAsync(client, frenchPath);
        var posted = await PostAsync(client, frenchPath, cookie, new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_action"] = "helpcenter.article.save",
            ["_return"] = frenchPath,
            ["_revision"] = revision,
            ["locale"] = "fr",
            ["title"] = "Titre Francais Modifie",
            ["slug"] = "titre-francais",
            ["body"] = "Nouveau contenu francais",
        });
        Assert.AreEqual(HttpStatusCode.Redirect, posted.StatusCode);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<SampleSettings>();
            var helpCenter = scope.ServiceProvider.GetRequiredService<IHelpCenterService>();
            var admin = new UserPrincipal(settings.Scope, new UserId("admin"));
            var stored = ((TicketValue<StoredArticle>)(await helpCenter.GetArticleAsync(admin, articleId)).Value!).Value;
            Assert.AreEqual(2, stored.Record.Article.Translations.Count, "Both translations must be preserved.");
            Assert.AreEqual(3, stored.Record.Article.Position);
            var en = stored.Record.Article.Translation(new HelpCenterLocale("en"))!;
            var fr = stored.Record.Article.Translation(new HelpCenterLocale("fr"))!;
            Assert.AreEqual("English Title", en.Title);
            Assert.AreEqual("English body", en.Body);
            Assert.AreEqual("Titre Francais Modifie", fr.Title);
            Assert.AreEqual("Nouveau contenu francais", fr.Body);
        }
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

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenPattern();

    [GeneratedRegex("name=\"_revision\" value=\"(?<v>\\d+)\"", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RevisionPattern();
}
