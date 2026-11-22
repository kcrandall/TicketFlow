using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using TicketFlow.Access;

namespace TicketFlow.Mcp.Tests;

[TestClass]
public sealed class ResourceAndPromptTests
{
    [TestMethod]
    public async Task ConversationAndHelpCenterResourcesEnforceAccessMaskingAndUntrustedWrapping()
    {
        await using var host = await McpTestHost.CreateAsync();

        // Add a private note and customer-facing message so we can verify note visibility and untrusted wrapping.
        var (noteErr, _) = await host.CallAsync("conversations_add_note", new() { ["conversationId"] = host.Conversation.Value, ["body"] = "Secret internal note" });
        Assert.IsFalse(noteErr);
        var (replyErr, _) = await host.CallAsync("conversations_reply", new() { ["conversationId"] = host.Conversation.Value, ["body"] = "Customer visible reply" });
        Assert.IsFalse(replyErr);

        // Owner reads conversation resource.
        host.Caller.User = "owner";
        var convRes = await host.Client.ReadResourceAsync($"ticketflow://conversations/{host.Conversation.Value}");
        var convText = Assert.IsInstanceOfType<TextResourceContents>(Assert.ContainsSingle(convRes.Contents)).Text;
        using (var doc = JsonDocument.Parse(convText))
            Assert.AreEqual(host.Conversation.Value, doc.RootElement.GetProperty("id").GetString());

        // Owner reads timeline resource (sees private note).
        var ownerTimeline = await host.Client.ReadResourceAsync($"ticketflow://conversations/{host.Conversation.Value}/timeline");
        using (var doc = JsonDocument.Parse(Assert.IsInstanceOfType<TextResourceContents>(Assert.ContainsSingle(ownerTimeline.Contents)).Text))
        {
            Assert.IsTrue(doc.RootElement.GetProperty("notesIncluded").GetBoolean());
            StringAssert.Contains(doc.RootElement.GetRawText(), "Secret internal note");
            StringAssert.Contains(doc.RootElement.GetRawText(), "\"untrusted\"");
        }

        // Caller with conversation:read but without note:read -> timeline omits private note.
        await AllToolsTests.GrantAsync(host, "no-notes", "no-notes", [AccessPermissions.ConversationRead]);
        host.Caller.User = "no-notes";
        var viewerTimeline = await host.Client.ReadResourceAsync($"ticketflow://conversations/{host.Conversation.Value}/timeline");
        using (var doc = JsonDocument.Parse(Assert.IsInstanceOfType<TextResourceContents>(Assert.ContainsSingle(viewerTimeline.Contents)).Text))
        {
            Assert.IsFalse(doc.RootElement.GetProperty("notesIncluded").GetBoolean());
            Assert.IsFalse(doc.RootElement.GetRawText().Contains("Secret internal note", StringComparison.Ordinal));
        }

        // Owner reads help center article resource with untrusted wrapping.
        host.Caller.User = "owner";
        var articleRes = await host.Client.ReadResourceAsync("ticketflow://helpcenter/articles/reset");
        using (var doc = JsonDocument.Parse(Assert.IsInstanceOfType<TextResourceContents>(Assert.ContainsSingle(articleRes.Contents)).Text))
        {
            Assert.AreEqual("reset", doc.RootElement.GetProperty("id").GetString());
            var body = doc.RootElement.GetProperty("translations")[0].GetProperty("body").GetProperty("untrusted");
            Assert.AreEqual("article", body.GetProperty("source").GetString());
        }

        // Principal with no permissions is forbidden.
        host.Caller.User = "nobody";
        var forbidden = await Assert.ThrowsExactlyAsync<McpProtocolException>(() => host.Client.ReadResourceAsync($"ticketflow://conversations/{host.Conversation.Value}").AsTask());
        StringAssert.Contains(forbidden.Message, "forbidden");

        // Unauthenticated caller is rejected.
        host.Caller.User = null;
        var unauth = await Assert.ThrowsExactlyAsync<McpProtocolException>(() => host.Client.ReadResourceAsync("ticketflow://helpcenter/articles/reset").AsTask());
        StringAssert.Contains(unauth.Message, "unauthenticated");

        // Disabled toolset refuses resource access.
        await using var restricted = await McpTestHost.CreateAsync(o => o.Toolsets = TicketFlowToolsets.Conversations);
        restricted.Caller.User = "owner";
        var unavailable = await Assert.ThrowsExactlyAsync<McpProtocolException>(() => restricted.Client.ReadResourceAsync("ticketflow://helpcenter/articles/reset").AsTask());
        StringAssert.Contains(unavailable.Message, "toolset-unavailable");
    }

    [TestMethod]
    public async Task PromptsListAndReturnSafetyFramedWorkflowInstructions()
    {
        await using var host = await McpTestHost.CreateAsync();
        var prompts = await host.Client.ListPromptsAsync();
        var names = prompts.Select(p => p.Name).ToList();
        CollectionAssert.Contains(names, "triage_conversation");
        CollectionAssert.Contains(names, "draft_customer_reply");
        CollectionAssert.Contains(names, "summarize_conversation");
        CollectionAssert.Contains(names, "investigate_sla_risk");

        var triage = await host.Client.GetPromptAsync("triage_conversation", new Dictionary<string, object?> { ["conversationId"] = host.Conversation.Value });
        var triageText = Assert.IsInstanceOfType<TextContentBlock>(Assert.ContainsSingle(triage.Messages).Content).Text;
        StringAssert.Contains(triageText, host.Conversation.Value);
        StringAssert.Contains(triageText, "untrusted");

        var draft = await host.Client.GetPromptAsync("draft_customer_reply", new Dictionary<string, object?> { ["conversationId"] = host.Conversation.Value, ["tone"] = "empathetic" });
        var draftText = Assert.IsInstanceOfType<TextContentBlock>(Assert.ContainsSingle(draft.Messages).Content).Text;
        StringAssert.Contains(draftText, "empathetic");
        StringAssert.Contains(draftText, "conversations_reply");

        var summary = await host.Client.GetPromptAsync("summarize_conversation", new Dictionary<string, object?> { ["conversationId"] = host.Conversation.Value });
        StringAssert.Contains(Assert.IsInstanceOfType<TextContentBlock>(Assert.ContainsSingle(summary.Messages).Content).Text, "notesIncluded");

        var sla = await host.Client.GetPromptAsync("investigate_sla_risk", new Dictionary<string, object?> { ["inboxId"] = "support" });
        StringAssert.Contains(Assert.IsInstanceOfType<TextContentBlock>(Assert.ContainsSingle(sla.Messages).Content).Text, "support");
    }
}
