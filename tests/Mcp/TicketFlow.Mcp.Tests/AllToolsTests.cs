using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Automation;
using TicketFlow.Core;
using TicketFlow.Routing;
using TicketFlow.Sla;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp.Tests;

/// <summary>Every catalog tool is called by name at least once, and every declared permission matches what the service enforces.</summary>
[TestClass]
public sealed class AllToolsTests
{
    [TestMethod]
    public async Task EveryToolIsCalledByNameAndReturnsItsDeclaredOutputShape()
    {
        await using var host = await McpTestHost.CreateAsync();
        var called = new HashSet<string>(StringComparer.Ordinal);
        var schemas = (await host.Client.ListToolsAsync()).ToDictionary(tool => tool.Name, tool => tool.ProtocolTool.OutputSchema);
        await GrantAsync(host, "revealer", "revealer", [AccessPermissions.ContactRead, AccessPermissions.ContactReveal]);

        async Task<JsonElement> Call(string tool, Dictionary<string, object?>? arguments = null, string user = "owner", string expected = "ok")
        {
            called.Add(tool);
            var (failed, content) = await host.CallAsync(tool, arguments, user);
            Assert.AreEqual(expected, failed ? McpTestHost.Code(content) : "ok", $"{tool}: {content.GetRawText()}");
            if (!failed && schemas.TryGetValue(tool, out var schema) && schema is { } declared)
            {
                var properties = declared.GetProperty("properties");
                foreach (var property in content.EnumerateObject())
                    Assert.IsTrue(properties.TryGetProperty(property.Name, out _), $"{tool} returned '{property.Name}', which its outputSchema does not declare.");
            }
            return content;
        }

        var conversation = host.Conversation.Value;
        var now = DateTimeOffset.UtcNow;
        await Call("whoami");
        await Call("list_toolsets");
        await Call("describe_tools", new() { ["toolset"] = "conversations" });
        await Call("explain_access", new() { ["tool"] = "conversations_reply", ["conversationId"] = conversation });

        await Call("conversations_list");
        await Call("conversations_get", new() { ["conversationId"] = conversation });
        await Call("conversations_timeline", new() { ["conversationId"] = conversation });
        var started = await Call("conversations_start", new() { ["inboxId"] = "support", ["contactId"] = host.Contact.Value, ["priority"] = "low", ["labels"] = new[] { "new" } });
        await Call("conversations_reply", new() { ["conversationId"] = conversation, ["body"] = "Hello" });
        await Call("conversations_add_note", new() { ["conversationId"] = conversation, ["body"] = "Internal" });
        await Call("conversations_assign", new() { ["conversationId"] = conversation, ["assigneeId"] = "agent" });
        await Call("conversations_set_team", new() { ["conversationId"] = conversation, ["teamId"] = "tier1" });
        await Call("conversations_set_status", new() { ["conversationId"] = conversation, ["status"] = "pending" });
        await Call("conversations_set_priority", new() { ["conversationId"] = conversation, ["priority"] = "high" });
        await Call("conversations_update_labels", new() { ["conversationId"] = conversation, ["add"] = new[] { "vip" } });
        await Call("conversations_delete", new()
        {
            ["conversationId"] = started.GetProperty("conversationId").GetString(), ["expectedRevision"] = started.GetProperty("revision").GetInt64(),
        });

        await Call("contacts_search", new() { ["query"] = "grace" });
        var contact = await Call("contacts_get", new() { ["contactId"] = host.Contact.Value });
        await Call("contacts_create", new() { ["displayName"] = "Alan", ["company"] = "Bletchley" });
        contact = await Call("contacts_update_profile", new() { ["contactId"] = host.Contact.Value, ["expectedRevision"] = contact.GetProperty("revision").GetInt64(), ["company"] = "Navy" });
        var revealed = await Call("contacts_reveal_field", new() { ["contactId"] = host.Contact.Value, ["field"] = "contact.email", ["justification"] = "customer asked" }, "revealer");
        Assert.AreEqual("grace@example.com", revealed.GetProperty("identities")[0].GetProperty("value").GetProperty("untrusted").GetProperty("text").GetString());
        var second = await Call("contacts_get", new() { ["contactId"] = host.SecondContact.Value });
        var merged = await Call("contacts_merge", new()
        {
            ["targetId"] = host.Contact.Value, ["targetRevision"] = contact.GetProperty("revision").GetInt64(),
            ["sourceId"] = host.SecondContact.Value, ["sourceRevision"] = second.GetProperty("revision").GetInt64(),
        });
        await Call("contacts_unmerge", new() { ["mergeId"] = merged.GetProperty("mergeId").GetGuid() });
        await Call("contacts_list_link_suggestions");
        var (rejectId, rejectRevision) = await host.CreateLinkSuggestionAsync("Possible duplicate");
        Assert.AreEqual(1, (await Call("contacts_list_link_suggestions", new() { ["contactId"] = host.Contact.Value })).GetProperty("items").GetArrayLength());
        Assert.AreEqual("rejected", (await Call("contacts_reject_link_suggestion", new() { ["suggestionId"] = rejectId, ["expectedRevision"] = rejectRevision, ["justification"] = "different person" }))
            .GetProperty("status").GetString());
        var (acceptId, acceptRevision) = await host.CreateLinkSuggestionAsync("Same person");
        Assert.AreEqual(host.Contact.Value, (await Call("contacts_accept_link_suggestion", new() { ["suggestionId"] = acceptId, ["expectedRevision"] = acceptRevision }))
            .GetProperty("target").GetProperty("id").GetString());

        await Call("routing_queue");
        await Call("routing_list_agents");
        await Call("routing_get_agent", new() { ["agentId"] = "agent" });
        Assert.AreEqual("online", (await Call("routing_set_agent_status", new() { ["agentId"] = "me", ["status"] = "online" }, "agent")).GetProperty("status").GetString());
        await Call("routing_list_policies");
        var routingJson = RoutingPolicyCodec.Serialize(new RoutingPolicy(new RoutingPolicyId("rp-test"), "Round Robin", RoutingStrategy.RoundRobin));
        var routingPolicy = await Call("routing_create_policy", new() { ["policyId"] = "rp-test", ["definitionJson"] = routingJson });
        routingPolicy = await Call("routing_update_policy", new()
        {
            ["policyId"] = "rp-test", ["expectedRevision"] = routingPolicy.GetProperty("revision").GetInt64(),
            ["definitionJson"] = RoutingPolicyCodec.Serialize(new RoutingPolicy(new RoutingPolicyId("rp-test"), "Least Busy", RoutingStrategy.LeastBusy)),
        });
        await Call("routing_delete_policy", new() { ["policyId"] = "rp-test", ["expectedRevision"] = routingPolicy.GetProperty("revision").GetInt64() });

        await Call("sla_get_status", new() { ["conversationId"] = conversation });
        await Call("sla_risk_list");
        await Call("sla_list_policies");
        var slaJson = SlaPolicyCodec.Serialize(new SlaPolicy(new SlaPolicyId("sla-test"), "Standard",
            [new SlaRule("default", new SlaCondition(), new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.CalendarTime)]));
        var slaPolicy = await Call("sla_create_policy", new() { ["policyId"] = "sla-test", ["definitionJson"] = slaJson });
        slaPolicy = await Call("sla_update_policy", new()
        {
            ["policyId"] = "sla-test", ["expectedRevision"] = slaPolicy.GetProperty("revision").GetInt64(), ["definitionJson"] = slaJson,
        });
        await Call("sla_delete_policy", new() { ["policyId"] = "sla-test", ["expectedRevision"] = slaPolicy.GetProperty("revision").GetInt64() });

        await Call("automation_list_rules");
        var ruleJson = AutomationCodec.SerializeRule(new AutomationRule(new AutomationRuleId("rule-test"), "Auto Tag", ConversationCreatedTrigger.Instance, null,
            [new AddLabelsAction(new LabelId("auto"))]));
        var rule = await Call("automation_create_rule", new() { ["ruleId"] = "rule-test", ["definitionJson"] = ruleJson });
        rule = await Call("automation_update_rule", new()
        {
            ["ruleId"] = "rule-test", ["expectedRevision"] = rule.GetProperty("revision").GetInt64(), ["definitionJson"] = ruleJson,
        });
        await Call("automation_delete_rule", new() { ["ruleId"] = "rule-test", ["expectedRevision"] = rule.GetProperty("revision").GetInt64() });
        await Call("automation_dry_run", new() { ["conversationId"] = conversation, ["eventKind"] = "statusChanged", ["status"] = "resolved" });
        await Call("macros_list");
        var macroJson = AutomationCodec.SerializeMacro(new Macro(new MacroId("macro-test"), "Macro Test", MacroVisibility.Shared, null, [new AddLabelsAction(new LabelId("m"))]));
        var macro = await Call("macros_create", new() { ["macroId"] = "macro-test", ["definitionJson"] = macroJson });
        macro = await Call("macros_update", new() { ["macroId"] = "macro-test", ["expectedRevision"] = macro.GetProperty("revision").GetInt64(), ["definitionJson"] = macroJson });
        await Call("macros_delete", new() { ["macroId"] = "macro-test", ["expectedRevision"] = macro.GetProperty("revision").GetInt64() });
        Assert.IsTrue((await Call("macros_run", new() { ["macroId"] = "tag", ["conversationId"] = conversation })).GetProperty("completed").GetBoolean());
        await Call("canned_responses_list", user: "agent");
        var cannedJson = AutomationCodec.SerializeCannedResponse(new CannedResponse(new CannedResponseId("cr-test"), "hello", "Hello", "en",
            [new CannedResponseVariant("en", new AutomationTemplate("Hello!"))]));
        var canned = await Call("canned_responses_create", new() { ["responseId"] = "cr-test", ["definitionJson"] = cannedJson });
        canned = await Call("canned_responses_update", new()
        {
            ["responseId"] = "cr-test", ["expectedRevision"] = canned.GetProperty("revision").GetInt64(), ["definitionJson"] = cannedJson,
        });
        await Call("canned_responses_delete", new() { ["responseId"] = "cr-test", ["expectedRevision"] = canned.GetProperty("revision").GetInt64() });
        await Call("canned_responses_render", new() { ["responseId"] = "thanks", ["conversationId"] = conversation }, "agent");

        await Call("helpcenter_list_portals");
        await Call("helpcenter_list_articles", new() { ["portalId"] = "help" });
        var article = await Call("helpcenter_get_article", new() { ["articleId"] = "reset" });
        await Call("helpcenter_create_draft", new() { ["portalId"] = "help", ["locale"] = "en", ["title"] = "Billing", ["slug"] = "billing", ["body"] = "Invoices." });
        article = await Call("helpcenter_update_draft", new()
        {
            ["articleId"] = "reset", ["expectedRevision"] = article.GetProperty("revision").GetInt64(), ["locale"] = "en", ["body"] = "Open settings, then security.",
        });
        article = await Call("helpcenter_publish", new() { ["articleId"] = "reset", ["expectedRevision"] = article.GetProperty("revision").GetInt64() });
        await Call("helpcenter_search", new() { ["portalId"] = "help", ["locale"] = "en", ["query"] = "settings" });
        await Call("helpcenter_unpublish", new() { ["articleId"] = "reset", ["expectedRevision"] = article.GetProperty("revision").GetInt64() });

        await Call("surveys_list");
        await Call("surveys_list_responses", new() { ["from"] = now.AddDays(-30), ["to"] = now });
        await Call("surveys_aggregate", new() { ["kind"] = "nps", ["from"] = now.AddDays(-30), ["to"] = now, ["bucket"] = "week" });

        await Call("reports_overview", new() { ["from"] = now.AddDays(-7), ["to"] = now });
        await Call("reports_leaderboard", new() { ["from"] = now.AddDays(-7), ["to"] = now, ["sort"] = "messagesSent" });
        await Call("reports_trends", new() { ["from"] = now.AddDays(-7), ["to"] = now, ["metrics"] = new[] { "conversationCreated", "messageSent" } });

        await Call("admin_get_workspace");
        await Call("admin_list_inboxes");
        await Call("admin_get_inbox", new() { ["inboxId"] = "support" });
        await Call("admin_list_teams");
        var team = await Call("admin_get_team", new() { ["teamId"] = "tier1" });
        team = await Call("admin_add_team_member", new() { ["teamId"] = "tier1", ["userId"] = "light", ["expectedRevision"] = team.GetProperty("revision").GetInt64() });
        await Call("admin_remove_team_member", new() { ["teamId"] = "tier1", ["userId"] = "light", ["expectedRevision"] = team.GetProperty("revision").GetInt64() });
        await Call("admin_list_calendars");
        Assert.AreEqual("support", (await Call("admin_get_channel_endpoint", new() { ["kind"] = "email", ["configurationId"] = "cfg-support" })).GetProperty("inboxId").GetString());

        await Call("access_list_roles");
        var customRole = await Call("access_create_role", new() { ["roleId"] = "triage-bot", ["name"] = "Triage Bot", ["permissions"] = new[] { "conversation:read" } });
        customRole = await Call("access_update_role", new()
        {
            ["roleId"] = "triage-bot", ["expectedRevision"] = customRole.GetProperty("revision").GetInt64(),
            ["name"] = "Triage Bot v2", ["permissions"] = new[] { "conversation:read", "sla:read" },
        });
        await Call("access_delete_role", new() { ["roleId"] = "triage-bot", ["expectedRevision"] = customRole.GetProperty("revision").GetInt64() });
        await Call("access_list_grants", new() { ["scope"] = "team", ["targetId"] = "tier1" });
        await Call("access_list_field_policies");
        var fieldPolicy = await Call("access_create_field_policy", new()
        {
            ["field"] = "contact.phone", ["appliesTo"] = new[] { "contact:read" }, ["unmaskedRoles"] = new[] { "owner" }, ["revealPermission"] = "contact:reveal",
        });
        fieldPolicy = await Call("access_update_field_policy", new()
        {
            ["field"] = "contact.phone", ["expectedRevision"] = fieldPolicy.GetProperty("revision").GetInt64(),
            ["appliesTo"] = new[] { "contact:read" }, ["unmaskedRoles"] = new[] { "owner", "admin" },
        });
        await Call("access_delete_field_policy", new() { ["field"] = "contact.phone", ["expectedRevision"] = fieldPolicy.GetProperty("revision").GetInt64() });
        var grant = await Call("access_create_grant", new() { ["subjectKind"] = "team", ["subjectId"] = "tier1", ["role"] = "viewer", ["scope"] = "inbox", ["targetId"] = "support" });
        await Call("access_revoke_grant", new() { ["grantId"] = grant.GetProperty("id").GetString(), ["expectedRevision"] = grant.GetProperty("revision").GetInt64() });

        // No model provider is configured here, so the assistant tools are exercised on their authorization path: agents lack assist:use.
        await Call("assist_summarize", new() { ["conversationId"] = conversation }, "agent", "forbidden");
        await Call("assist_draft_reply", new() { ["conversationId"] = conversation, ["instructions"] = "be brief" }, "agent", "forbidden");
        await Call("assist_triage", new() { ["conversationId"] = conversation, ["labels"] = new[] { "billing" } }, "agent", "forbidden");

        CollectionAssert.AreEquivalent(TicketFlowToolCatalog.Tools.Select(tool => tool.Name).ToList(), called.ToList(), "Every catalog tool must be called by this test.");
    }

    /// <summary>Tools whose service answers an Access-filtered (empty) result rather than a refusal when the caller lacks the permission.</summary>
    private static readonly HashSet<string> EmptyWhenDenied = new(StringComparer.Ordinal)
    {
        "conversations_list", "contacts_search", "helpcenter_list_portals", "helpcenter_list_articles", "surveys_list", "sla_risk_list", "surveys_list_responses", "surveys_aggregate", "reports_overview", "reports_leaderboard", "reports_trends",
        "admin_list_inboxes", "admin_list_teams",
    };

    [TestMethod]
    public async Task EveryDeclaredPermissionIsWhatTheServiceEnforces()
    {
        await using var host = await McpTestHost.CreateAsync();
        var permissions = await CatalogPermissionsAsync(host);
        var failures = new List<string>();
        foreach (var tool in TicketFlowToolCatalog.Tools.Where(tool => tool.Toolset != TicketFlowToolsets.None))
        {
            var user = "minus-" + tool.Name.Replace('_', '-');
            await GrantAsync(host, user, user, permissions.Where(permission => !tool.Permissions.Contains(permission.Value)));
            var (failed, content) = await host.CallAsync(tool.Name, await DeniedArgumentsAsync(host, tool.Name), user);
            var outcome = failed ? McpTestHost.Code(content) : "ok";
            var expected = tool.Name == "contacts_reveal_field" ? "reveal-refused" : EmptyWhenDenied.Contains(tool.Name) ? "ok" : "forbidden";
            if (outcome != expected)
                failures.Add($"{tool.Name}: expected {expected}, got {outcome} {content.GetRawText()}");
            else if (expected == "ok")
                Assert.AreEqual(0, Count(content), $"{tool.Name} must not return rows the caller cannot see.");
            else if (expected == "forbidden")
                Assert.AreEqual(tool.Permissions[0], content.GetProperty("error").GetProperty("requiredPermissions")[0].GetString());
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static async Task<Dictionary<string, object?>> SuggestionAsync(McpTestHost host, string tool)
    {
        var (id, revision) = await host.CreateLinkSuggestionAsync("Candidate for " + tool);
        return new() { ["suggestionId"] = id, ["expectedRevision"] = revision };
    }

    private static int Count(JsonElement content) =>
        content.TryGetProperty("items", out var items) ? items.GetArrayLength()
        : content.TryGetProperty("rows", out var rows) ? rows.GetArrayLength()
        : content.TryGetProperty("points", out var points) ? points.GetArrayLength()
        : (int)content.GetProperty("created").GetInt64();

    private static async Task<IReadOnlyList<Permission>> CatalogPermissionsAsync(McpTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return [.. scope.ServiceProvider.GetRequiredService<IPermissionCatalog>().Definitions.Select(definition => definition.Permission)];
    }

    internal static async Task GrantAsync(McpTestHost host, string user, string role, IEnumerable<Permission> permissions)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessRole>>((await services.GetRequiredService<IAccessRoleStore>().CreateAsync(host.Scope,
            new AccessRole(new RoleId(role), role, permissions))).Value);
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await services.GetRequiredService<IAccessGrantStore>().CreateAsync(new AccessGrant(new GrantId(role),
            AccessPartition.From(host.Scope), new UserId(user), new RoleId(role), new WorkspaceGrantScope(host.Scope.WorkspaceId)))).Value);
    }

    /// <summary>Valid arguments naming existing records with current revisions, so the call reaches the authorization decision.</summary>
    private static async Task<Dictionary<string, object?>> DeniedArgumentsAsync(McpTestHost host, string tool)
    {
        var conversation = host.Conversation.Value;
        var now = DateTimeOffset.UtcNow;
        async Task<long> RevisionAsync(string read, Dictionary<string, object?> arguments) =>
            (await host.CallAsync(read, arguments)).Content.GetProperty("revision").GetInt64();
        return tool switch
        {
            "conversations_list" or "routing_queue" or "sla_risk_list" or "contacts_list_link_suggestions" or "routing_list_agents" or "routing_list_policies" or "sla_list_policies"
                or "automation_list_rules" or "macros_list" or "canned_responses_list" or "helpcenter_list_portals" or "surveys_list" or "admin_get_workspace"
                or "admin_list_inboxes" or "admin_list_teams" or "admin_list_calendars" or "access_list_roles" or "access_list_grants" or "access_list_field_policies" => [],
            "conversations_get" or "conversations_timeline" or "sla_get_status" or "assist_summarize" or "assist_draft_reply" or "assist_triage" => new() { ["conversationId"] = conversation },
            "conversations_start" => new() { ["inboxId"] = "support", ["contactId"] = host.Contact.Value },
            "conversations_reply" or "conversations_add_note" => new() { ["conversationId"] = conversation, ["body"] = "x" },
            "conversations_assign" => new() { ["conversationId"] = conversation, ["assigneeId"] = "agent" },
            "conversations_set_team" => new() { ["conversationId"] = conversation, ["teamId"] = "tier1" },
            "conversations_set_status" => new() { ["conversationId"] = conversation, ["status"] = "resolved" },
            "conversations_set_priority" => new() { ["conversationId"] = conversation, ["priority"] = "urgent" },
            "conversations_update_labels" => new() { ["conversationId"] = conversation, ["add"] = new[] { "x" } },
            "conversations_delete" => new() { ["conversationId"] = conversation, ["expectedRevision"] = await RevisionAsync("conversations_get", new() { ["conversationId"] = conversation }) },
            "contacts_search" => new() { ["query"] = "grace" },
            "contacts_get" => new() { ["contactId"] = host.Contact.Value },
            "contacts_create" => new() { ["displayName"] = "Nobody" },
            "contacts_update_profile" => new()
            {
                ["contactId"] = host.Contact.Value, ["company"] = "x", ["expectedRevision"] = await RevisionAsync("contacts_get", new() { ["contactId"] = host.Contact.Value }),
            },
            "contacts_reveal_field" => new() { ["contactId"] = host.Contact.Value, ["field"] = "contact.email" },
            "contacts_merge" => new()
            {
                ["targetId"] = host.Contact.Value, ["targetRevision"] = await RevisionAsync("contacts_get", new() { ["contactId"] = host.Contact.Value }),
                ["sourceId"] = host.SecondContact.Value, ["sourceRevision"] = await RevisionAsync("contacts_get", new() { ["contactId"] = host.SecondContact.Value }),
            },
            "contacts_unmerge" => new() { ["mergeId"] = await host.CreateMergeAsync() },
            "contacts_accept_link_suggestion" or "contacts_reject_link_suggestion" => await SuggestionAsync(host, tool),
            "routing_get_agent" => new() { ["agentId"] = "agent" },
            "routing_set_agent_status" => new() { ["agentId"] = "agent", ["status"] = "away" },
            "routing_create_policy" => new()
            {
                ["policyId"] = "rp-denied",
                ["definitionJson"] = RoutingPolicyCodec.Serialize(new RoutingPolicy(new RoutingPolicyId("rp-denied"), "D", RoutingStrategy.RoundRobin)),
            },
            "routing_update_policy" => new()
            {
                ["policyId"] = "rp-denied", ["expectedRevision"] = 1L,
                ["definitionJson"] = RoutingPolicyCodec.Serialize(new RoutingPolicy(new RoutingPolicyId("rp-denied"), "D", RoutingStrategy.RoundRobin)),
            },
            "routing_delete_policy" => new() { ["policyId"] = "rp-denied", ["expectedRevision"] = 1L },
            "sla_create_policy" => new()
            {
                ["policyId"] = "sla-denied",
                ["definitionJson"] = SlaPolicyCodec.Serialize(new SlaPolicy(new SlaPolicyId("sla-denied"), "D",
                    [new SlaRule("r", new SlaCondition(), new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.CalendarTime)])),
            },
            "sla_update_policy" => new()
            {
                ["policyId"] = "sla-denied", ["expectedRevision"] = 1L,
                ["definitionJson"] = SlaPolicyCodec.Serialize(new SlaPolicy(new SlaPolicyId("sla-denied"), "D",
                    [new SlaRule("r", new SlaCondition(), new SlaTargets(TimeSpan.FromHours(1)), SlaClockBasis.CalendarTime)])),
            },
            "sla_delete_policy" => new() { ["policyId"] = "sla-denied", ["expectedRevision"] = 1L },
            "automation_create_rule" => new()
            {
                ["ruleId"] = "r-denied",
                ["definitionJson"] = AutomationCodec.SerializeRule(new AutomationRule(new AutomationRuleId("r-denied"), "D", ConversationCreatedTrigger.Instance, null,
                    [new AddLabelsAction(new LabelId("x"))])),
            },
            "automation_update_rule" => new()
            {
                ["ruleId"] = "r-denied", ["expectedRevision"] = 1L,
                ["definitionJson"] = AutomationCodec.SerializeRule(new AutomationRule(new AutomationRuleId("r-denied"), "D", ConversationCreatedTrigger.Instance, null,
                    [new AddLabelsAction(new LabelId("x"))])),
            },
            "automation_delete_rule" => new() { ["ruleId"] = "r-denied", ["expectedRevision"] = 1L },
            "automation_dry_run" => new() { ["conversationId"] = conversation, ["eventKind"] = "messageReceived" },
            "macros_create" => new()
            {
                ["macroId"] = "m-denied",
                ["definitionJson"] = AutomationCodec.SerializeMacro(new Macro(new MacroId("m-denied"), "D", MacroVisibility.Shared, null, [new AddLabelsAction(new LabelId("x"))])),
            },
            "macros_update" => new()
            {
                ["macroId"] = "tag", ["expectedRevision"] = 1L,
                ["definitionJson"] = AutomationCodec.SerializeMacro(new Macro(new MacroId("tag"), "D", MacroVisibility.Shared, null, [new AddLabelsAction(new LabelId("x"))])),
            },
            "macros_delete" => new() { ["macroId"] = "tag", ["expectedRevision"] = 1L },
            "macros_run" => new() { ["macroId"] = "tag", ["conversationId"] = conversation },
            "canned_responses_create" => new()
            {
                ["responseId"] = "cr-denied",
                ["definitionJson"] = AutomationCodec.SerializeCannedResponse(new CannedResponse(new CannedResponseId("cr-denied"), "d", "D", "en",
                    [new CannedResponseVariant("en", new AutomationTemplate("D"))])),
            },
            "canned_responses_update" => new()
            {
                ["responseId"] = "thanks", ["expectedRevision"] = 1L,
                ["definitionJson"] = AutomationCodec.SerializeCannedResponse(new CannedResponse(new CannedResponseId("thanks"), "thanks", "D", "en",
                    [new CannedResponseVariant("en", new AutomationTemplate("D"))])),
            },
            "canned_responses_delete" => new() { ["responseId"] = "thanks", ["expectedRevision"] = 1L },
            "canned_responses_render" => new() { ["responseId"] = "thanks", ["conversationId"] = conversation },
            "helpcenter_search" => new() { ["portalId"] = "help", ["locale"] = "en", ["query"] = "password" },
            "helpcenter_list_articles" => new() { ["portalId"] = "help" },
            "helpcenter_get_article" => new() { ["articleId"] = "reset" },
            "helpcenter_create_draft" => new() { ["portalId"] = "help", ["locale"] = "en", ["title"] = "T", ["slug"] = "t", ["body"] = "B" },
            "helpcenter_update_draft" or "helpcenter_publish" or "helpcenter_unpublish" => new()
            {
                ["articleId"] = "reset", ["locale"] = "en", ["body"] = "B", ["expectedRevision"] = await RevisionAsync("helpcenter_get_article", new() { ["articleId"] = "reset" }),
            },
            "surveys_list_responses" => new() { ["from"] = now.AddDays(-1), ["to"] = now },
            "surveys_aggregate" => new() { ["kind"] = "csat", ["from"] = now.AddDays(-1), ["to"] = now },
            "reports_overview" or "reports_leaderboard" => new() { ["from"] = now.AddDays(-1), ["to"] = now },
            "reports_trends" => new() { ["from"] = now.AddDays(-1), ["to"] = now, ["metrics"] = new[] { "conversationCreated" } },
            "admin_get_inbox" => new() { ["inboxId"] = "support" },
            "admin_get_team" => new() { ["teamId"] = "tier1" },
            "admin_add_team_member" or "admin_remove_team_member" => new()
            {
                ["teamId"] = "tier1", ["userId"] = "agent", ["expectedRevision"] = await RevisionAsync("admin_get_team", new() { ["teamId"] = "tier1" }),
            },
            "admin_get_channel_endpoint" => new() { ["kind"] = "email", ["configurationId"] = "cfg-support" },
            "access_create_grant" => new() { ["subjectKind"] = "user", ["subjectId"] = "light", ["role"] = "viewer" },
            "access_revoke_grant" => new() { ["grantId"] = "seed-1", ["expectedRevision"] = 1 },
            "access_create_role" => new() { ["roleId"] = "r-denied", ["name"] = "Denied", ["permissions"] = new[] { "conversation:read" } },
            "access_update_role" => new() { ["roleId"] = "r-denied", ["expectedRevision"] = 1L, ["name"] = "Denied", ["permissions"] = new[] { "conversation:read" } },
            "access_delete_role" => new() { ["roleId"] = "r-denied", ["expectedRevision"] = 1L },
            "access_create_field_policy" => new() { ["field"] = "contact.phone", ["appliesTo"] = new[] { "contact:read" } },
            "access_update_field_policy" => new() { ["field"] = "contact.email", ["expectedRevision"] = 1L, ["appliesTo"] = new[] { "contact:read" } },
            "access_delete_field_policy" => new() { ["field"] = "contact.email", ["expectedRevision"] = 1L },
            _ => throw new AssertFailedException($"No denied-call arguments for {tool}."),
        };
    }
}
