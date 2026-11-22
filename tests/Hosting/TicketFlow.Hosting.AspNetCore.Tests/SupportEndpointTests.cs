using System.Net;
using System.Text.Json;
using static TicketFlow.Hosting.AspNetCore.Tests.EndpointHost;

namespace TicketFlow.Hosting.AspNetCore.Tests;

[TestClass]
public sealed class SupportEndpointTests
{
    [TestMethod]
    public async Task TeamsCalendarsAndAccessAdministration()
    {
        await using var host = await CreateAsync();
        var owner = host.Client();
        var team = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/teams", new { id = "tier1", name = "Tier 1", members = new[] { "agent" } }));
        Assert.AreEqual(HttpStatusCode.Created, team.StatusCode);
        Assert.AreEqual("teams/tier1", team.Headers.Location?.OriginalString);
        var member = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/teams/tier1/members", new { userId = "owner" }, ifMatch: 1));
        Assert.AreEqual(HttpStatusCode.OK, member.StatusCode);
        Assert.AreEqual(2, (await JsonAsync(member)).GetProperty("members").GetArrayLength());

        var calendar = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/calendars", new
        {
            id = "office",
            name = "Office",
            timeZoneId = "UTC",
            schedule = JsonDocument.Parse("""{"v":1,"week":[{"d":1,"s":540,"e":1020}],"overrides":[]}""").RootElement,
        }));
        Assert.AreEqual(HttpStatusCode.Created, calendar.StatusCode);
        Assert.AreEqual(540, (await JsonAsync(calendar)).GetProperty("schedule").GetProperty("week")[0].GetProperty("s").GetInt32());

        var role = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/access/roles", new { id = "triage", name = "Triage", permissions = new[] { "conversation:read", "conversation:assign" } }));
        Assert.AreEqual(HttpStatusCode.Created, role.StatusCode);
        var grant = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/access/grants", new
        {
            id = "g-triage",
            subject = new { kind = "user", id = "helper" },
            role = "triage",
            scope = new { kind = "team", id = "tier1" },
        }));
        Assert.AreEqual(HttpStatusCode.Created, grant.StatusCode);
        var listed = await JsonAsync(await owner.GetAsync($"{Api}/access/grants?scope=team&id=tier1"));
        Assert.AreEqual("g-triage", listed.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.AreEqual("allow", listed.GetProperty("items")[0].GetProperty("effect").GetString());
        var roles = await JsonAsync(await owner.GetAsync($"{Api}/access/roles"));
        Assert.IsTrue(roles.EnumerateArray().Any(item => item.GetProperty("id").GetString() == "owner" && item.GetProperty("builtIn").GetBoolean()));

        Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client("agent").GetAsync($"{Api}/access/roles")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client("agent").SendAsync(Request(HttpMethod.Post, $"{Api}/access/roles",
            new { name = "Escalate", permissions = new[] { "access:manage" } }))).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await owner.SendAsync(Request(HttpMethod.Delete, $"{Api}/access/grants/g-triage", ifMatch: 1))).StatusCode);
    }

    [TestMethod]
    public async Task ContactsSearchUpdateRevealAndExportStreams()
    {
        await using var host = await CreateAsync();
        var owner = host.Client();
        var created = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/contacts", new
        {
            id = "c1",
            profile = new { displayName = "Ada Lovelace" },
            identities = new[] { new { kind = "email", value = "ada@example.com", verified = true, source = "import" } },
        }));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var contact = await JsonAsync(created);
        Assert.AreEqual("email", contact.GetProperty("identities")[0].GetProperty("kind").GetString());

        var search = await JsonAsync(await owner.GetAsync($"{Api}/contacts?q=ada"));
        Assert.AreEqual("c1", search.GetProperty("items")[0].GetProperty("id").GetString());
        var byEmail = await JsonAsync(await owner.GetAsync($"{Api}/contacts?kind=email&q=ada@"));
        Assert.AreEqual(1, byEmail.GetProperty("items").GetArrayLength());

        var update = await owner.SendAsync(Request(HttpMethod.Patch, $"{Api}/contacts/c1", new { edits = new object[] { new { kind = "addLabel", label = "vip" }, new { kind = "setBlocked", blocked = true } } }, ifMatch: 1));
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
        Assert.AreEqual("vip", (await JsonAsync(update)).GetProperty("labels")[0].GetString());
        var stale = await owner.SendAsync(Request(HttpMethod.Patch, $"{Api}/contacts/c1", new { edits = new object[] { new { kind = "removeLabel", label = "vip" } } }, ifMatch: 1));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.AreEqual(2, (await JsonAsync(stale)).GetProperty("currentRevision").GetInt64());

        var reveal = await host.Client("agent").SendAsync(Request(HttpMethod.Post, $"{Api}/contacts/c1/reveal", new { field = "contact.email", justification = "verify caller" }));
        Assert.AreEqual(HttpStatusCode.Forbidden, reveal.StatusCode);
        Assert.AreEqual("no-store", reveal.Headers.CacheControl?.ToString());

        var export = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/contacts/export", new { maximumContacts = 10, justification = "quarterly audit export" }));
        Assert.AreEqual(HttpStatusCode.OK, export.StatusCode);
        Assert.AreEqual("application/x-ndjson", export.Content.Headers.ContentType?.MediaType);
        Assert.IsTrue(export.Headers.Contains("X-TicketFlow-Audit-Event"));
        var lines = (await export.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(1, lines);
        Assert.AreEqual("c1", JsonDocument.Parse(lines[0]).RootElement.GetProperty("id").GetString());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client("agent").SendAsync(Request(HttpMethod.Post, $"{Api}/contacts/export",
            new { maximumContacts = 10, justification = "quarterly audit export" }))).StatusCode);
    }

    [TestMethod]
    public async Task ConversationLifecycleTimelineAndFencedCommands()
    {
        await using var host = await CreateAsync();
        var owner = host.Client();
        await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/contacts", new { id = "c1", profile = new { displayName = "Ada" } }));
        var started = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations", new { id = "conv-1", inboxId = "support", contactId = "c1", priority = "high", labels = new[] { "billing" } }));
        Assert.AreEqual(HttpStatusCode.Created, started.StatusCode);
        var revision = Revision(started);

        var agent = host.Client("agent");
        var reply = await agent.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations/conv-1/replies", new { body = "We are on it." }, ifMatch: revision));
        Assert.AreEqual(HttpStatusCode.OK, reply.StatusCode);
        var command = await JsonAsync(reply);
        Assert.AreEqual(revision + 1, command.GetProperty("revision").GetInt64());
        var note = await agent.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations/conv-1/notes", new { body = "Internal note" }));
        Assert.AreEqual(HttpStatusCode.OK, note.StatusCode);

        var stale = await agent.SendAsync(Request(HttpMethod.Put, $"{Api}/conversations/conv-1/priority", new { priority = "low" }, ifMatch: revision));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var assign = await agent.SendAsync(Request(HttpMethod.Put, $"{Api}/conversations/conv-1/assignee", new { userId = "agent" }));
        Assert.AreEqual(HttpStatusCode.OK, assign.StatusCode);
        var status = await agent.SendAsync(Request(HttpMethod.Put, $"{Api}/conversations/conv-1/status", new { status = "snoozed" }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, status.StatusCode);

        var view = await agent.GetAsync($"{Api}/conversations/conv-1");
        Assert.AreEqual(HttpStatusCode.OK, view.StatusCode);
        var conversation = await JsonAsync(view);
        Assert.AreEqual("high", conversation.GetProperty("priority").GetString());
        Assert.AreEqual("agent", conversation.GetProperty("assigneeId").GetString());
        Assert.AreEqual("open", conversation.GetProperty("status").GetProperty("kind").GetString());

        var timeline = await JsonAsync(await agent.GetAsync($"{Api}/conversations/conv-1/timeline"));
        var kinds = timeline.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("kind").GetString()).ToList();
        CollectionAssert.Contains(kinds, "message");
        CollectionAssert.Contains(kinds, "note");
        var message = timeline.GetProperty("events").EnumerateArray().First(item => item.GetProperty("kind").GetString() == "message").GetProperty("message");
        Assert.AreEqual("We are on it.", message.GetProperty("body").GetString());
        Assert.AreEqual("outgoing", message.GetProperty("direction").GetString());
        Assert.AreEqual("pending", message.GetProperty("delivery").GetProperty("state").GetString());

        var mine = await JsonAsync(await agent.GetAsync($"{Api}/conversations?assignee=me&status=open"));
        Assert.AreEqual(1, mine.GetProperty("items").GetArrayLength());
        var none = await JsonAsync(await agent.GetAsync($"{Api}/conversations?label=other"));
        Assert.AreEqual(0, none.GetProperty("items").GetArrayLength());

        Assert.AreEqual(HttpStatusCode.Forbidden, (await host.Client("stranger").GetAsync($"{Api}/conversations/conv-1")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await agent.GetAsync($"{Api}/conversations/missing")).StatusCode);
        Assert.AreEqual(HttpStatusCode.PreconditionRequired, (await owner.DeleteAsync($"{Api}/conversations/conv-1")).StatusCode);
    }

    [TestMethod]
    public async Task RoutingStatusPresenceAndSlaRiskEndpoints()
    {
        await using var host = await CreateAsync();
        var owner = host.Client();
        var configure = await owner.SendAsync(Request(HttpMethod.Put, $"{Api}/routing/agents/agent/settings", new { defaultCapacity = 3, skills = new[] { new { name = "billing", level = 2 } } }));
        Assert.AreEqual(HttpStatusCode.OK, configure.StatusCode);
        var agent = host.Client("agent");
        var online = await agent.SendAsync(Request(HttpMethod.Put, $"{Api}/routing/agents/me/status", new { status = "online" }));
        Assert.AreEqual(HttpStatusCode.OK, online.StatusCode);
        Assert.AreEqual("online", (await JsonAsync(online)).GetProperty("status").GetString());
        Assert.AreEqual(HttpStatusCode.OK, (await agent.PostAsync($"{Api}/routing/agents/me/heartbeat", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await agent.GetAsync($"{Api}/routing/queue")).StatusCode);

        await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/contacts", new { id = "c1" }));
        await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations", new { id = "conv-1", inboxId = "support", contactId = "c1" }));
        var presence = await agent.SendAsync(Request(HttpMethod.Put, $"{Api}/conversations/conv-1/presence", new { activity = "composing" }));
        Assert.AreEqual(HttpStatusCode.OK, presence.StatusCode);
        var lockResponse = await agent.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations/conv-1/reply-lock", new { durationSeconds = 60 }));
        Assert.AreEqual(HttpStatusCode.OK, lockResponse.StatusCode);
        var held = await owner.SendAsync(Request(HttpMethod.Post, $"{Api}/conversations/conv-1/reply-lock", new { }));
        Assert.AreEqual(HttpStatusCode.Conflict, held.StatusCode);
        Assert.AreEqual("agent", (await JsonAsync(held)).GetProperty("lock").GetProperty("holderId").GetString());

        var risk = await agent.GetAsync($"{Api}/sla/risk?filter=breached");
        Assert.AreEqual(HttpStatusCode.OK, risk.StatusCode);
        Assert.AreEqual(0, (await JsonAsync(risk)).GetProperty("items").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, (await agent.GetAsync($"{Api}/sla/risk?filter=sometimes")).StatusCode);
    }
}
