using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static TicketFlow.Hosting.AspNetCore.Tests.EndpointHost;

namespace TicketFlow.Hosting.AspNetCore.Tests;

[TestClass]
public sealed class FoundationTests
{
    [TestMethod]
    public async Task RequestsWithoutAPrincipalAreUnauthorizedProblems()
    {
        await using var host = await CreateAsync();
        var response = await host.Client(user: null).GetAsync($"{Api}/inboxes");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await JsonAsync(response);
        Assert.AreEqual("unauthenticated", problem.GetProperty("code").GetString());
        Assert.AreEqual("urn:ticketflow:problem:unauthenticated", problem.GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task InvalidWorkspaceRouteValuesAreBadRequests()
    {
        await using var host = await CreateAsync();
        var response = await host.Client().GetAsync("/ticketflow/api/workspaces/%01bad/inboxes");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalid-workspace", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task PrincipalsWithoutGrantsAreForbidden()
    {
        await using var host = await CreateAsync();
        var response = await host.Client("stranger").GetAsync($"{Api}/inboxes/support");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual("forbidden", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task RevisionFencingUsesETagsAndIfMatch()
    {
        await using var host = await CreateAsync();
        var client = host.Client();
        var get = await client.GetAsync($"{Api}/inboxes/support");
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.AreEqual("\"1\"", get.Headers.ETag?.Tag);

        var body = new { name = "Support desk", channel = new { kind = "email", configurationId = "cfg-support" } };
        var missing = await client.SendAsync(Request(HttpMethod.Put, $"{Api}/inboxes/support", body));
        Assert.AreEqual(HttpStatusCode.PreconditionRequired, missing.StatusCode);

        var updated = await client.SendAsync(Request(HttpMethod.Put, $"{Api}/inboxes/support", body, ifMatch: 1));
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        Assert.AreEqual(2, Revision(updated));
        Assert.AreEqual("Support desk", (await JsonAsync(updated)).GetProperty("name").GetString());

        var stale = await client.SendAsync(Request(HttpMethod.Put, $"{Api}/inboxes/support", body, ifMatch: 1));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = await JsonAsync(stale);
        Assert.AreEqual("revision-mismatch", problem.GetProperty("code").GetString());
        Assert.AreEqual(2, problem.GetProperty("currentRevision").GetInt64());
        Assert.AreEqual("\"2\"", stale.Headers.ETag?.Tag);

        var malformed = new HttpRequestMessage(HttpMethod.Delete, $"{Api}/inboxes/support");
        malformed.Headers.TryAddWithoutValidation("If-Match", "\"two\"");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.SendAsync(malformed)).StatusCode);

        var weak = new HttpRequestMessage(HttpMethod.Put, $"{Api}/inboxes/support") { Content = JsonContent.Create(body) };
        weak.Headers.TryAddWithoutValidation("If-Match", "W/\"2\"");
        Assert.AreEqual(HttpStatusCode.OK, (await client.SendAsync(weak)).StatusCode, "Weak tags naming the revision are accepted.");
    }

    [TestMethod]
    public async Task CreatingAnExistingRecordIsAConflictAndMissingRecordsAreNotFound()
    {
        await using var host = await CreateAsync();
        var client = host.Client();
        var duplicate = await client.SendAsync(Request(HttpMethod.Post, $"{Api}/inboxes", new { id = "support", name = "Again", channel = new { kind = "sms", configurationId = "cfg-sms" } }));
        Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.AreEqual(1, (await JsonAsync(duplicate)).GetProperty("currentRevision").GetInt64());
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"{Api}/inboxes/missing")).StatusCode);
    }

    [TestMethod]
    public async Task DomainValidationFailuresAreUnprocessableWithoutEchoingInput()
    {
        await using var host = await CreateAsync();
        var response = await host.Client().SendAsync(Request(HttpMethod.Post, $"{Api}/inboxes",
            new { name = "Bad", channel = new { kind = "NOT A KIND secret@example.com", configurationId = "cfg" } }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret@example.com", text, StringComparison.Ordinal);
        Assert.AreEqual("invalid", (await JsonAsync(response)).GetProperty("code").GetString());

        var badEnum = await host.Client().GetAsync($"{Api}/conversations?status=1");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, badEnum.StatusCode);
    }

    [TestMethod]
    public async Task OversizedBodiesAreRejected()
    {
        await using var host = await CreateAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/inboxes")
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new { name = new string('x', 70_000), channel = new { kind = "sms", configurationId = "c" } }))
            {
                Headers = { ContentType = new("application/json") },
            },
        };
        var response = await host.Client().SendAsync(request);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [TestMethod]
    public async Task OpenApiDocumentDescribesTicketFlowEndpoints()
    {
        await using var host = await CreateAsync();
        var response = await host.Client(user: null).GetAsync("/ticketflow/openapi/ticketflow.json");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = await JsonAsync(response);
        var paths = document.GetProperty("paths");
        Assert.IsTrue(paths.TryGetProperty("/ticketflow/api/workspaces/{workspaceId}/conversations", out _));
        Assert.IsTrue(paths.TryGetProperty("/ticketflow/public/workspaces/{workspaceId}/surveys/respond", out _));
        Assert.IsTrue(document.GetProperty("components").GetProperty("schemas").TryGetProperty("ConversationResponse", out _));
        var listConversations = paths.GetProperty("/ticketflow/api/workspaces/{workspaceId}/conversations").GetProperty("get");
        Assert.AreEqual("ListConversations", listConversations.GetProperty("operationId").GetString());
        Assert.IsFalse(listConversations.GetRawText().Contains("TicketFlowActor", StringComparison.Ordinal), "Bound actors are not API parameters.");
    }

    [TestMethod]
    public async Task MeReportsWorkspacePermissions()
    {
        await using var host = await CreateAsync();
        var owner = await JsonAsync(await host.Client().GetAsync($"{Api}/me"));
        Assert.AreEqual("user", owner.GetProperty("kind").GetString());
        var permissions = owner.GetProperty("workspacePermissions").EnumerateArray().Select(item => item.GetString()).ToList();
        CollectionAssert.Contains(permissions, "conversation:read");
        CollectionAssert.DoesNotContain(permissions, "access:manage", "Sensitive permissions are probed only on request (audited).");
        var sensitive = await JsonAsync(await host.Client().GetAsync($"{Api}/me?includeSensitive=true"));
        CollectionAssert.Contains(sensitive.GetProperty("workspacePermissions").EnumerateArray().Select(item => item.GetString()).ToList(), "access:manage");
        var agent = await JsonAsync(await host.Client("agent").GetAsync($"{Api}/me?includeSensitive=true"));
        CollectionAssert.DoesNotContain(agent.GetProperty("workspacePermissions").EnumerateArray().Select(item => item.GetString()).ToList(), "access:manage");
        var stranger = await JsonAsync(await host.Client("stranger").GetAsync($"{Api}/me"));
        Assert.AreEqual(0, stranger.GetProperty("workspacePermissions").GetArrayLength());
    }
}
