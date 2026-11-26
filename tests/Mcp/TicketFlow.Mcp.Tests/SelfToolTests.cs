using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;

namespace TicketFlow.Mcp.Tests;

[TestClass]
public sealed class SelfToolTests
{
    [TestMethod]
    public async Task WhoAmIDescribesTheCallerWithoutProbingSensitivePermissionsByDefault()
    {
        await using var host = await McpTestHost.CreateAsync();
        var auditBefore = await AuditCountAsync(host);
        var (_, me) = await host.CallAsync("whoami", user: null, apiClient: "assistant");
        Assert.AreEqual("apiClient", me.GetProperty("principal").GetProperty("kind").GetString());
        Assert.AreEqual("assistant", me.GetProperty("principal").GetProperty("id").GetString());
        Assert.AreEqual("workspace", me.GetProperty("scope").GetProperty("workspace").GetString());
        Assert.AreEqual("agent", me.GetProperty("roles")[0].GetString());
        var permissions = Strings(me.GetProperty("workspacePermissions"));
        CollectionAssert.Contains(permissions, "conversation:reply");
        CollectionAssert.DoesNotContain(permissions, "conversation:delete");
        CollectionAssert.Contains(Strings(me.GetProperty("notProbed")), "contact:reveal", "Sensitive permissions are not probed by default.");
        Assert.AreEqual(auditBefore, await AuditCountAsync(host), "whoami records no audit events unless sensitive probing is requested.");

        var (_, sensitive) = await host.CallAsync("whoami", new() { ["includeSensitive"] = true }, user: "owner");
        CollectionAssert.Contains(Strings(sensitive.GetProperty("workspacePermissions")), "conversation:delete");
        Assert.IsGreaterThan(auditBefore, await AuditCountAsync(host), "Sensitive probes are audited by Access.");
    }

    [TestMethod]
    public async Task DescribeToolsListsEveryToolIncludingDisabledOnesWithCallerVerdicts()
    {
        await using var host = await McpTestHost.CreateAsync(options => options.Toolsets = TicketFlowToolsets.Conversations);
        var (_, described) = await host.CallAsync("describe_tools", user: "light");
        var tools = described.GetProperty("tools").EnumerateArray().ToDictionary(tool => tool.GetProperty("name").GetString()!);
        CollectionAssert.AreEquivalent(TicketFlowToolCatalog.Tools.Select(tool => tool.Name).ToList(), tools.Keys.ToList());
        Assert.IsTrue(tools["conversations_add_note"].GetProperty("callerHasPermissions").GetBoolean());
        Assert.IsFalse(tools["conversations_reply"].GetProperty("callerHasPermissions").GetBoolean());
        Assert.IsTrue(tools["conversations_reply"].GetProperty("enabledOnServer").GetBoolean());
        Assert.IsFalse(tools["contacts_search"].GetProperty("enabledOnServer").GetBoolean(), "The contacts toolset is not plugged in on this server.");
        CollectionAssert.Contains(Strings(tools["conversations_reply"].GetProperty("grantedByBuiltInRoles")), "agent");
        Assert.AreEqual("conversations", tools["conversations_reply"].GetProperty("toolset").GetString());

        var (_, toolsets) = await host.CallAsync("list_toolsets");
        var conversations = toolsets.GetProperty("toolsets").EnumerateArray().Single(item => string.Equals(item.GetProperty("name").GetString(), "conversations", StringComparison.Ordinal));
        Assert.IsTrue(conversations.GetProperty("enabled").GetBoolean());
        var assist = toolsets.GetProperty("toolsets").EnumerateArray().Single(item => string.Equals(item.GetProperty("name").GetString(), "assist", StringComparison.Ordinal));
        Assert.IsFalse(assist.GetProperty("registered").GetBoolean());
        Assert.AreEqual("contacts_search", TicketFlowToolCatalog.Find("contacts_search")!.Name);
        Assert.AreEqual("toolset-unavailable", McpTestHost.Code((await host.CallAsync("contacts_search")).Content), "Unregistered toolsets are not callable.");
    }

    [TestMethod]
    public async Task ExplainAccessGivesReasonsAndRolesWithoutLeakingResources()
    {
        await using var host = await McpTestHost.CreateAsync();
        var (_, reply) = await host.CallAsync("explain_access", new() { ["tool"] = "conversations_reply" }, user: "viewer");
        Assert.IsFalse(reply.GetProperty("allowed").GetBoolean());
        var decision = reply.GetProperty("permissions")[0];
        Assert.AreEqual("denied", decision.GetProperty("decision").GetString());
        Assert.AreEqual("noMatchingGrant", decision.GetProperty("reason").GetString());
        CollectionAssert.Contains(Strings(decision.GetProperty("grantedByBuiltInRoles")), "agent");

        var (_, masked) = await host.CallAsync("explain_access", new() { ["permission"] = "contact:read", ["contactId"] = host.Contact.Value }, user: "agent");
        Assert.AreEqual("masked", masked.GetProperty("permissions")[0].GetProperty("decision").GetString());
        Assert.AreEqual("contact.email", masked.GetProperty("permissions")[0].GetProperty("maskedFields")[0].GetProperty("field").GetString());
        Assert.DoesNotContain("grace", masked.GetRawText(), StringComparison.Ordinal);

        var (hidden, notVisible) = await host.CallAsync("explain_access", new() { ["permission"] = "conversation:read", ["conversationId"] = host.Conversation.Value }, user: "nobody");
        Assert.IsTrue(hidden);
        Assert.AreEqual("not-found", McpTestHost.Code(notVisible), "Resources the caller cannot read are reported as not found.");

        var (_, unknown) = await host.CallAsync("explain_access", new() { ["permission"] = "events:read" }, user: "owner");
        Assert.AreEqual("unknownPermission", unknown.GetProperty("permissions")[0].GetProperty("reason").GetString(), "the events feature is not installed here.");
        Assert.AreEqual("invalid", McpTestHost.Code((await host.CallAsync("explain_access", new() { ["permission"] = "a:b", ["tool"] = "whoami" })).Content));
    }

    [TestMethod]
    public async Task ToolListIsMarkedOrFilteredByTheCallersPermissions()
    {
        await using (var showAll = await McpTestHost.CreateAsync())
        {
            showAll.Caller.User = "light";
            var tools = (await showAll.Client.ListToolsAsync()).ToDictionary(tool => tool.Name);
            Assert.IsFalse(tools["conversations_reply"].ProtocolTool.Meta!["ticketflow/permitted"]!.GetValue<bool>());
            Assert.IsTrue(tools["conversations_add_note"].ProtocolTool.Meta!["ticketflow/permitted"]!.GetValue<bool>());
            Assert.IsFalse(tools["conversations_delete"].ProtocolTool.Meta!.ContainsKey("ticketflow/permitted"), "Sensitive permissions are not probed for tools/list by default.");
            Assert.IsTrue(tools.ContainsKey("whoami"));
        }
        await using var hide = await McpTestHost.CreateAsync(options => options.ToolList = TicketFlowToolListMode.HideUnpermitted);
        hide.Caller.User = "light";
        var visible = (await hide.Client.ListToolsAsync()).Select(tool => tool.Name).ToList();
        CollectionAssert.DoesNotContain(visible, "conversations_reply");
        CollectionAssert.Contains(visible, "conversations_add_note");
        CollectionAssert.Contains(visible, "conversations_delete", "Unprobed sensitive tools stay listed.");
        CollectionAssert.Contains(visible, "describe_tools");
    }

    [TestMethod]
    public async Task TeamScopedCallersSeeThePermissionsTheyHoldOnTheirTeam()
    {
        await using (var host = await McpTestHost.CreateAsync(options => options.ToolList = TicketFlowToolListMode.HideUnpermitted))
        {
            var (_, me) = await host.CallAsync("whoami", user: "teamagent");
            CollectionAssert.Contains(Strings(me.GetProperty("heldPermissions")), "conversation:reply", "A team-scoped Agent grant holds conversation:reply on its team.");
            CollectionAssert.DoesNotContain(Strings(me.GetProperty("workspacePermissions")), "conversation:reply", "It does not hold it workspace-wide.");

            var (_, described) = await host.CallAsync("describe_tools", new() { ["tool"] = "conversations_reply" }, user: "teamagent");
            Assert.IsTrue(described.GetProperty("tools")[0].GetProperty("callerHasPermissions").GetBoolean());

            host.Caller.User = "teamagent";
            var visible = (await host.Client.ListToolsAsync()).Select(tool => tool.Name).ToList();
            CollectionAssert.Contains(visible, "conversations_reply", "HideUnpermitted keeps tools usable under team/inbox grants.");
            CollectionAssert.DoesNotContain(visible, "conversations_start", "Agents do not hold conversation:create anywhere.");

            Assert.IsFalse((await host.CallAsync("conversations_reply", new() { ["conversationId"] = host.TeamConversation.Value, ["body"] = "On it" }, user: "teamagent")).IsError);
            Assert.AreEqual("forbidden", McpTestHost.Code((await host.CallAsync("conversations_reply", new() { ["conversationId"] = host.Conversation.Value, ["body"] = "x" },
                user: "teamagent")).Content), "Outside the team the service still refuses.");
        }
    }

    private static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    private static async Task<int> AuditCountAsync(McpTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var sink = scope.ServiceProvider.GetRequiredService<IAccessAuditSink>();
        return sink is InMemoryAccessAuditSink memory ? memory.Snapshot().Count : 0;
    }
}
