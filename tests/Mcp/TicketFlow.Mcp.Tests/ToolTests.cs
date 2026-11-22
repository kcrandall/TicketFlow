using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;

namespace TicketFlow.Mcp.Tests;

[TestClass]
public sealed class ToolTests
{
    [TestMethod]
    public async Task ToolsRoundTripThroughTheMcpClientAndRunAsTheCaller()
    {
        await using var host = await McpTestHost.CreateAsync();
        var conversation = host.Conversation.Value;

        var (failed, reply) = await host.CallAsync("conversations_reply", new() { ["conversationId"] = conversation, ["body"] = "Hello from the assistant" }, user: null, apiClient: "assistant");
        Assert.IsFalse(failed, reply.GetRawText());
        var revision = reply.GetProperty("revision").GetInt64();

        var (_, timeline) = await host.CallAsync("conversations_timeline", new() { ["conversationId"] = conversation }, user: "agent");
        var message = timeline.GetProperty("events").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "message");
        Assert.AreEqual("apiClient", message.GetProperty("actor").GetProperty("kind").GetString(), "The reply is attributed to the MCP caller's principal.");
        Assert.AreEqual("assistant", message.GetProperty("actor").GetProperty("id").GetString());
        Assert.AreEqual("Hello from the assistant", message.GetProperty("data").GetProperty("body").GetString());

        var (stale, conflict) = await host.CallAsync("conversations_set_priority", new() { ["conversationId"] = conversation, ["priority"] = "high", ["expectedRevision"] = revision - 1 }, user: "agent");
        Assert.IsTrue(stale);
        Assert.AreEqual("conflict", McpTestHost.Code(conflict));
        Assert.AreEqual(revision, conflict.GetProperty("error").GetProperty("currentRevision").GetInt64());

        var (_, list) = await host.CallAsync("conversations_list", new() { ["limit"] = 10 }, user: "agent");
        Assert.AreEqual(conversation, list.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task RolesLackingThePermissionGetForbiddenWithTheRequiredPermissionsAndRoles()
    {
        await using var host = await McpTestHost.CreateAsync();
        var (failed, content) = await host.CallAsync("conversations_reply", new() { ["conversationId"] = host.Conversation.Value, ["body"] = "x" }, user: "viewer");
        Assert.IsTrue(failed);
        var error = content.GetProperty("error");
        Assert.AreEqual("forbidden", error.GetProperty("code").GetString());
        Assert.AreEqual("conversation:reply", error.GetProperty("requiredPermissions")[0].GetString());
        var roles = error.GetProperty("grantedByBuiltInRoles").EnumerateArray().Select(role => role.GetString()).ToList();
        CollectionAssert.Contains(roles, "agent");
        CollectionAssert.DoesNotContain(roles, "viewer");

        // Light agents may write notes but not reply; nobody without grants sees anything.
        Assert.IsFalse((await host.CallAsync("conversations_add_note", new() { ["conversationId"] = host.Conversation.Value, ["body"] = "internal" }, user: "light")).IsError);
        Assert.AreEqual("forbidden", McpTestHost.Code((await host.CallAsync("conversations_set_status", new() { ["conversationId"] = host.Conversation.Value, ["status"] = "resolved" }, user: "light")).Content));
        var (_, empty) = await host.CallAsync("conversations_list", user: "nobody");
        Assert.AreEqual(0, empty.GetProperty("items").GetArrayLength(), "List paths filter through Access plans, so a principal without grants sees nothing.");
        Assert.AreEqual("forbidden", McpTestHost.Code((await host.CallAsync("helpcenter_create_draft", new()
        {
            ["portalId"] = "help", ["locale"] = "en", ["title"] = "T", ["slug"] = "t", ["body"] = "B",
        }, user: "agent")).Content));
        Assert.AreEqual("forbidden", McpTestHost.Code((await host.CallAsync("access_list_roles", user: "agent")).Content));
        var current = (await host.CallAsync("conversations_get", new() { ["conversationId"] = host.Conversation.Value })).Content.GetProperty("revision").GetInt64();
        Assert.AreEqual("forbidden", McpTestHost.Code((await host.CallAsync("conversations_delete", new() { ["conversationId"] = host.Conversation.Value, ["expectedRevision"] = current }, user: "agent")).Content));
    }

    [TestMethod]
    public async Task MaskedContactDataStaysMaskedAndNotesFollowNoteRead()
    {
        await using var host = await McpTestHost.CreateAsync();
        var (_, agentView) = await host.CallAsync("contacts_get", new() { ["contactId"] = host.Contact.Value }, user: "agent");
        var identity = agentView.GetProperty("identities")[0];
        Assert.IsTrue(identity.GetProperty("masked").GetBoolean());
        Assert.IsFalse(identity.TryGetProperty("value", out _), "Masked values are absent, not obfuscated.");
        Assert.DoesNotContain("grace@example.com", agentView.GetRawText());
        Assert.AreEqual("contact.email", agentView.GetProperty("maskedFields")[0].GetProperty("field").GetString());

        var (_, ownerView) = await host.CallAsync("contacts_get", new() { ["contactId"] = host.Contact.Value }, user: "owner");
        Assert.AreEqual("grace@example.com", ownerView.GetProperty("identities")[0].GetProperty("value").GetString());

        var (_, search) = await host.CallAsync("contacts_search", new() { ["query"] = "grace" }, user: "agent");
        Assert.DoesNotContain("grace@example.com", search.GetRawText());

        var (refusedReveal, reveal) = await host.CallAsync("contacts_reveal_field", new() { ["contactId"] = host.Contact.Value, ["field"] = "contact.email" }, user: "agent");
        Assert.IsTrue(refusedReveal);
        Assert.AreEqual("reveal-refused", McpTestHost.Code(reveal));

        await host.CallAsync("conversations_add_note", new() { ["conversationId"] = host.Conversation.Value, ["body"] = "secret note" }, user: "agent");
        var (_, withNotes) = await host.CallAsync("conversations_timeline", new() { ["conversationId"] = host.Conversation.Value }, user: "agent");
        Assert.IsTrue(withNotes.GetProperty("notesIncluded").GetBoolean());
        await using var serviceScope = host.Services.CreateAsyncScope();
        var roles = serviceScope.ServiceProvider.GetRequiredService<IAccessRoleStore>();
        await roles.CreateAsync(host.Scope, new AccessRole(new RoleId("reader"), "Reader", [AccessPermissions.ConversationRead]));
        await serviceScope.ServiceProvider.GetRequiredService<IAccessGrantStore>().CreateAsync(new AccessGrant(new GrantId("reader"), AccessPartition.From(host.Scope),
            new TicketFlow.Core.UserId("reader"), new RoleId("reader"), new WorkspaceGrantScope(host.Scope.WorkspaceId)));
        var (_, withoutNotes) = await host.CallAsync("conversations_timeline", new() { ["conversationId"] = host.Conversation.Value }, user: "reader");
        Assert.IsFalse(withoutNotes.GetProperty("notesIncluded").GetBoolean());
        Assert.DoesNotContain("secret note", withoutNotes.GetRawText());
    }

    [TestMethod]
    public async Task UnresolvedCallersInvalidInputAndMissingRecordsMapToErrorResults()
    {
        await using var host = await McpTestHost.CreateAsync();
        Assert.AreEqual("unauthenticated", McpTestHost.Code((await host.CallAsync("conversations_list", user: null)).Content));
        Assert.AreEqual("invalid", McpTestHost.Code((await host.CallAsync("conversations_list", new() { ["status"] = "bogus" })).Content));
        Assert.AreEqual("invalid", McpTestHost.Code((await host.CallAsync("conversations_list", new() { ["cursor"] = "not-a-cursor!" })).Content));
        Assert.AreEqual("not-found", McpTestHost.Code((await host.CallAsync("conversations_get", new() { ["conversationId"] = "missing" })).Content));
        Assert.AreEqual("invalid", McpTestHost.Code((await host.CallAsync("conversations_get", new() { ["conversationId"] = "missing", ["workspace"] = new string('w', 300) })).Content));
    }

    [TestMethod]
    public async Task FeatureToolsetsWithoutServicesAreUnavailable()
    {
        await using var host = await McpTestHost.CreateAsync();
        var (failed, content) = await host.CallAsync("assist_summarize", new() { ["conversationId"] = host.Conversation.Value });
        Assert.IsTrue(failed);
        Assert.AreEqual("toolset-unavailable", McpTestHost.Code(content));
        var listed = (await host.Client.ListToolsAsync()).Select(tool => tool.Name).ToList();
        CollectionAssert.DoesNotContain(listed, "assist_summarize", "Tools of toolsets without services are hidden from tools/list.");
        CollectionAssert.Contains(listed, "conversations_reply");
    }

    [TestMethod]
    public async Task ResultsAreBoundedAndLongBodiesTruncated()
    {
        await using var host = await McpTestHost.CreateAsync(options => options.MaximumTextLength = 300);
        await host.CallAsync("conversations_add_note", new() { ["conversationId"] = host.Conversation.Value, ["body"] = new string('n', 2000) }, user: "owner");
        var (_, timeline) = await host.CallAsync("conversations_timeline", new() { ["conversationId"] = host.Conversation.Value });
        var note = timeline.GetProperty("events").EnumerateArray().Single(item => item.GetProperty("kind").GetString() == "note").GetProperty("data");
        Assert.AreEqual(300, note.GetProperty("body").GetString()!.Length);
        Assert.IsTrue(note.GetProperty("truncated").GetBoolean());

        var tooLarge = TicketFlowToolResults.Success(new { text = new string('x', 10_000) }, 4096);
        Assert.IsTrue(tooLarge.IsError);
        Assert.AreEqual("result-too-large", McpTestHost.Code(tooLarge.StructuredContent!.Value));
    }

    [TestMethod]
    public async Task FeatureToolsetsCoverTheirServices()
    {
        await using var host = await McpTestHost.CreateAsync();
        async Task<JsonElement> OkAsync(string tool, Dictionary<string, object?>? arguments = null, string user = "owner")
        {
            var (failed, content) = await host.CallAsync(tool, arguments, user);
            Assert.IsFalse(failed, $"{tool}: {content.GetRawText()}");
            return content;
        }
        var now = DateTimeOffset.UtcNow;
        await OkAsync("routing_queue");
        await OkAsync("routing_list_agents");
        await OkAsync("sla_get_status", new() { ["conversationId"] = host.Conversation.Value });
        await OkAsync("sla_risk_list");
        await OkAsync("automation_list_rules");
        await OkAsync("automation_dry_run", new() { ["conversationId"] = host.Conversation.Value, ["eventKind"] = "messageReceived" });
        await OkAsync("macros_list");
        await OkAsync("canned_responses_list", user: "agent");
        await OkAsync("surveys_list");
        await OkAsync("surveys_aggregate", new() { ["kind"] = "csat", ["from"] = now.AddDays(-7), ["to"] = now });
        var overview = await OkAsync("reports_overview", new() { ["from"] = now.AddDays(-7), ["to"] = now });
        Assert.IsTrue(overview.TryGetProperty("openBacklog", out _));
        await OkAsync("reports_trends", new() { ["from"] = now.AddDays(-7), ["to"] = now, ["metrics"] = new[] { "conversationCreated" } });
        Assert.AreEqual("Test", (await OkAsync("admin_get_workspace")).GetProperty("name").GetString());
        Assert.AreEqual("support", (await OkAsync("admin_list_inboxes", user: "agent")).GetProperty("items")[0].GetProperty("id").GetString());
        var team = await OkAsync("admin_get_team", new() { ["teamId"] = "tier1" });
        await OkAsync("admin_add_team_member", new() { ["teamId"] = "tier1", ["userId"] = "light", ["expectedRevision"] = team.GetProperty("revision").GetInt64() });
        await OkAsync("access_list_roles");
        var grant = await OkAsync("access_create_grant", new() { ["subjectKind"] = "apiClient", ["subjectId"] = "reporter", ["role"] = "viewer" });
        await OkAsync("access_revoke_grant", new() { ["grantId"] = grant.GetProperty("id").GetString(), ["expectedRevision"] = grant.GetProperty("revision").GetInt64() });

        var portal = await OkAsync("helpcenter_list_portals");
        Assert.AreEqual(0, portal.GetProperty("items").GetArrayLength());
        Assert.AreEqual("forbidden", McpTestHost.Code((await host.CallAsync("access_create_grant", new()
        {
            ["subjectKind"] = "user", ["subjectId"] = "agent", ["role"] = "owner",
        }, user: "agent")).Content), "Grant escalation is refused by the Access administrator.");
    }
}
