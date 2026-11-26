using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Assist;
using TicketFlow.Automation;
using TicketFlow.Core;
using static TicketFlow.Hosting.AspNetCore.Tests.EndpointHost;

namespace TicketFlow.Hosting.AspNetCore.Tests;

[TestClass]
public sealed class ContentEndpointTests
{
    [TestMethod]
    public async Task CannedResponsesUseCodecDefinitionsAndRenderPerConversation()
    {
        await using var host = await CreateAsync();
        var owner = host.Client();
        var definition = JsonElement.Parse(AutomationCodec.SerializeCannedResponse(new CannedResponse(new CannedResponseId("ignored"), "thanks", "Thanks", "en",
            [new CannedResponseVariant("en", new AutomationTemplate("Thanks for reaching out."))])));
        var created = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/canned-responses", new { id = "cr-1", definition }));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        Assert.AreEqual("cr-1", (await JsonAsync(created)).GetProperty("id").GetString());

        await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/contacts", new { id = "c1" }));
        await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations", new { id = "conv-1", inboxId = "support", contactId = "c1" }));
        var agent = host.Client("agent");
        var found = await JsonAsync(await agent.GetAsync($"{Api}/canned-responses/by-shortcut/thanks"));
        Assert.AreEqual("cr-1", found.GetProperty("id").GetString());
        var rendered = await agent.SendAsync(Request(HttpMethod.Post, $"{Api}/canned-responses/cr-1/render", new { conversationId = "conv-1" }));
        Assert.AreEqual(HttpStatusCode.OK, rendered.StatusCode);
        Assert.AreEqual("Thanks for reaching out.", (await JsonAsync(rendered)).GetProperty("body").GetString());

        var invalid = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/canned-responses", new { definition = JsonElement.Parse("""{"v":99}""") }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        var dryRun = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/automation/dry-run", new { conversationId = "conv-1", @event = new { kind = "messageReceived" } }));
        Assert.AreEqual(HttpStatusCode.OK, dryRun.StatusCode);
        Assert.AreEqual(0, (await JsonAsync(dryRun)).GetProperty("rules").GetArrayLength());
    }

    [TestMethod]
    public async Task HelpCenterPublishingAndAnonymousPortal()
    {
        await using var host = await CreateAsync();
        var owner = host.Client();
        Assert.AreEqual(HttpStatusCode.Created, (await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/help-center/portals",
            new { id = "help", slug = "help", name = "Help", defaultLocale = "en", visibility = "public" }))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/help-center/portals",
            new { id = "internal", slug = "internal", name = "Internal", defaultLocale = "en" }))).StatusCode);
        var article = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/help-center/articles", new
        {
            id = "a1",
            portalId = "help",
            translations = new[] { new { locale = "en", title = "Reset your password", slug = "reset-password", body = "Use **Forgot password**. <script>alert(1)</script>" } },
        }));
        Assert.AreEqual(HttpStatusCode.Created, article.StatusCode);
        Assert.AreEqual("draft", (await JsonAsync(article)).GetProperty("state").GetString());
        var published = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/help-center/articles/a1/publish", ifMatch: Revision(article), header: true));
        Assert.AreEqual(HttpStatusCode.OK, published.StatusCode);
        Assert.AreEqual("published", (await JsonAsync(published)).GetProperty("state").GetString());

        var anonymous = host.Client(user: null);
        var portal = await anonymous.GetAsync($"{Public}/portals/help");
        Assert.AreEqual(HttpStatusCode.OK, portal.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{Public}/portals/internal")).StatusCode);
        var list = await JsonAsync(await anonymous.GetAsync($"{Public}/portals/help/en/articles"));
        Assert.AreEqual("reset-password", list.GetProperty("items")[0].GetProperty("slug").GetString());
        var read = await JsonAsync(await anonymous.GetAsync($"{Public}/portals/help/en/articles/reset-password"));
        var html = read.GetProperty("html").GetString()!;
        Assert.Contains("<strong>Forgot password</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.AreEqual(HttpStatusCode.NoContent, (await anonymous.SendAsync(Request(HttpMethod.Post, $"{Public}/portals/help/articles/a1/feedback",
            new { visitorKey = "visitor-1", helpful = true }))).StatusCode);
        var feedback = await JsonAsync(await owner.GetAsync($"{Api}/help-center/articles/a1/feedback"));
        Assert.AreEqual(1, feedback.GetProperty("helpful").GetInt64());
    }

    [TestMethod]
    public async Task PublicSurveyTokensTravelInBodiesAndUnknownTokensAreNotFound()
    {
        await using var host = await CreateAsync();
        var anonymous = host.Client(user: null);
        var lookup = await anonymous.SendAsync(Request(HttpMethod.Post, $"{Public}/surveys/invitation", new { token = "tfs1.not-a-real-token" }));
        Assert.AreEqual(HttpStatusCode.NotFound, lookup.StatusCode);
        var respond = await anonymous.SendAsync(Request(HttpMethod.Post, $"{Public}/surveys/respond", new { token = "tfs1.not-a-real-token", score = 5 }));
        Assert.AreEqual(HttpStatusCode.NotFound, respond.StatusCode);

        var owner = host.Client();
        var survey = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/surveys", new
        {
            id = "csat",
            name = "CSAT",
            kind = "csat",
            defaultLocale = "en",
            questions = new[] { new { locale = "en", text = "How did we do?" } },
            policy = new { delaySeconds = 0, contactCooldownSeconds = 0, invitationLifetimeSeconds = 86_400 },
        }));
        Assert.AreEqual(HttpStatusCode.Created, survey.StatusCode);
        Assert.AreEqual("csat", (await JsonAsync(survey)).GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task PublicEndpointsRequireAResolvedPartition()
    {
        await using var host = await CreateAsync(options => options.Partition = null);
        var response = await host.Client(user: null).GetAsync($"{Public}/portals/help");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual("partition-unavailable", (await JsonAsync(response)).GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await host.Client().GetAsync($"{Api}/inboxes")).StatusCode);
    }

    [TestMethod]
    public async Task AssistRefusalsAndProviderFailuresMapToRetryableStatuses()
    {
        await using var host = await CreateAsync(services: services => services.AddScoped<IAssistService, FakeAssistService>());
        var agent = host.Client("agent");
        var quota = await agent.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations/conv-1/assist/summary", header: true));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, quota.StatusCode);
        Assert.AreEqual("30", quota.Headers.RetryAfter?.ToString());
        var failed = await agent.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations/conv-1/assist/rewrite", new { text = "fix this", tone = "formal" }));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.AreEqual("provider-overloaded", (await JsonAsync(failed)).GetProperty("code").GetString());
        var ok = await agent.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations/conv-1/assist/sentiment", new { }));
        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        var body = await JsonAsync(ok);
        Assert.AreEqual("positive", body.GetProperty("value").GetProperty("sentiment").GetString());
        Assert.AreEqual(12, body.GetProperty("usage").GetProperty("inputTokens").GetInt64());
    }

    private sealed class FakeAssistService : IAssistService
    {
        public Task<AssistResult<AssistSummary>> SummarizeAsync(PrincipalRef actor, AssistSummaryRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<AssistResult<AssistSummary>>(new AssistRefused(AssistRefusal.QuotaExceeded, ["Daily token quota reached."], TimeSpan.FromSeconds(30)));

        public Task<AssistResult<AssistDraft>> DraftReplyAsync(PrincipalRef actor, AssistDraftRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistResult<AssistTriage>> TriageAsync(PrincipalRef actor, AssistTriageRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistResult<AssistSentimentAndIntent>> AnalyzeSentimentAsync(PrincipalRef actor, AssistSentimentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<AssistResult<AssistSentimentAndIntent>>(new AssistSucceeded<AssistSentimentAndIntent>(new AssistSentimentAndIntent(AssistSentiment.Positive, 0.9, "thanks", 0.8),
                new AssistUsage(12, 3), "test-model", Guid.NewGuid(), AssistRecording.NotApplicable));

        public Task<AssistResult<AssistTranslation>> TranslateAsync(PrincipalRef actor, AssistTranslateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AssistResult<AssistToneRewrite>> RewriteToneAsync(PrincipalRef actor, AssistToneRewriteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<AssistResult<AssistToneRewrite>>(new AssistProviderFailed(AssistProviderFailure.Overloaded, true));
    }
}
