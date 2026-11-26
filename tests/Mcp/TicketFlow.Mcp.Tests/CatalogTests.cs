using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using TicketFlow.Access;

namespace TicketFlow.Mcp.Tests;

[TestClass]
public sealed partial class CatalogTests
{
    [TestMethod]
    public void EveryToolMethodIsCatalogedWithUniqueValidNames()
    {
        var methods = typeof(TicketFlowToolCatalog).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();
        Assert.IsGreaterThan(50, methods.Count);
        Assert.AreEqual(methods.Count, TicketFlowToolCatalog.Tools.Count);
        var names = TicketFlowToolCatalog.Tools.Select(tool => tool.Name).ToList();
        Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        foreach (var tool in TicketFlowToolCatalog.Tools)
        {
            Assert.IsTrue(ToolName().IsMatch(tool.Name), tool.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(tool.Title), tool.Name);
            Assert.IsFalse(tool.ReadOnly && tool.Destructive, $"{tool.Name} cannot be read-only and destructive.");
            if (tool.Toolset != TicketFlowToolsets.None)
                Assert.IsTrue(tool.Name.StartsWith(TicketFlowToolsetCatalog.NameOf(tool.Toolset), StringComparison.Ordinal)
                    || tool.Toolset == TicketFlowToolsets.Automation && (tool.Name.StartsWith("macros_", StringComparison.Ordinal) || tool.Name.StartsWith("canned_responses_", StringComparison.Ordinal)),
                    $"{tool.Name} is named after its toolset.");
        }
    }

    [TestMethod]
    public void EveryFeatureToolDeclaresKnownPermissionsAndGrantingRoles()
    {
        var known = TicketFlowToolCatalog.KnownPermissions.Select(definition => definition.Permission.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var tool in TicketFlowToolCatalog.Tools)
        {
            if (tool.Toolset == TicketFlowToolsets.None)
            {
                Assert.IsEmpty(tool.Permissions, $"{tool.Name}: self-awareness tools need only an authenticated principal.");
                continue;
            }
            Assert.IsNotEmpty(tool.Permissions, tool.Name);
            foreach (var permission in tool.Permissions.Concat(tool.OptionalPermissions))
                Assert.Contains(permission, known, $"{tool.Name} declares an unknown permission.");
            CollectionAssert.Contains(tool.GrantedByBuiltInRoles.ToList(), BuiltInRoles.Owner.Id.Value, $"{tool.Name}: Owner holds every permission.");
            Assert.IsNotEmpty(tool.Services, tool.Name);
        }
    }

    [TestMethod]
    public void WriteToolsAreNotReadOnlyAndDeletesMergesAndGrantsAreDestructive()
    {
        string[] destructive = ["conversations_delete", "contacts_merge", "contacts_unmerge", "contacts_accept_link_suggestion", "macros_run", "access_create_grant",
            "access_revoke_grant", "admin_remove_team_member", "helpcenter_unpublish"];
        foreach (var name in destructive)
            Assert.IsTrue(TicketFlowToolCatalog.Find(name)!.Destructive, name);
        string[] writes = ["conversations_reply", "conversations_add_note", "conversations_assign", "conversations_set_status", "contacts_create", "contacts_reveal_field",
            "helpcenter_create_draft", "helpcenter_publish", "admin_add_team_member", "assist_summarize"];
        foreach (var name in writes)
            Assert.IsFalse(TicketFlowToolCatalog.Find(name)!.ReadOnly, name);
        foreach (var tool in TicketFlowToolCatalog.Tools.Where(tool => tool.Name.Contains("_list") || tool.Name.Contains("_get") || tool.Name is "whoami" or "describe_tools"))
            Assert.IsTrue(tool.ReadOnly && tool.Idempotent, tool.Name);
    }

    [TestMethod]
    public void BuiltInRoleMappingFollowsAccessRoles()
    {
        CollectionAssert.AreEquivalent(new[] { "owner", "admin", "supervisor", "agent" }, TicketFlowToolCatalog.Find("conversations_reply")!.GrantedByBuiltInRoles.ToList());
        CollectionAssert.AreEquivalent(new[] { "owner", "admin", "supervisor", "agent", "light-agent" }, TicketFlowToolCatalog.Find("conversations_add_note")!.GrantedByBuiltInRoles.ToList());
        CollectionAssert.AreEquivalent(new[] { "owner" }, TicketFlowToolCatalog.Find("helpcenter_publish")!.GrantedByBuiltInRoles.ToList());
        CollectionAssert.AreEquivalent(new[] { "owner", "admin", "supervisor", "viewer" }, TicketFlowToolCatalog.Find("reports_overview")!.GrantedByBuiltInRoles.ToList());
        CollectionAssert.AreEquivalent(new[] { "owner" }, TicketFlowToolCatalog.RolesGranting([TicketFlowMcpPermissions.AccessManageTenant]).ToList());
        CollectionAssert.AreEquivalent(new[] { "owner", "admin" }, TicketFlowToolCatalog.Find("access_activate_break_glass")!.GrantedByBuiltInRoles.ToList());
        CollectionAssert.AreEquivalent(new[] { "owner", "admin" }, TicketFlowToolCatalog.Find("access_list_service_accounts")!.GrantedByBuiltInRoles.ToList());
        Assert.IsTrue(TicketFlowToolCatalog.Find("canned_responses_render")!.AnyOf);
        Assert.IsTrue(TicketFlowToolCatalog.Find("access_list_grants")!.AnyOf);
        Assert.IsTrue(TicketFlowToolCatalog.Find("access_create_grant")!.AnyOf);
        Assert.IsTrue(TicketFlowToolCatalog.Find("access_list_break_glass")!.AnyOf);
        Assert.IsTrue(TicketFlowToolCatalog.Find("access_list_service_accounts")!.AnyOf);
        Assert.IsTrue(TicketFlowToolCatalog.Find("access_list_federation_mappings")!.AnyOf);
        Assert.AreEqual(TicketFlowMcpPermissions.AccessManageTenant, AccessPermissions.AccessManageTenant.Value);
        Assert.AreEqual(TicketFlowMcpPermissions.AccessBreakGlass, AccessPermissions.AccessBreakGlass.Value);
    }

    [TestMethod]
    public void RegistrationAddsSelectedToolsetsOnceAndAlwaysTheSelfTools()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddMcpServer()
            .WithTicketFlowTools(options => options.Toolsets = TicketFlowToolsets.Conversations)
            .WithTicketFlowTools(TicketFlowToolsets.Conversations | TicketFlowToolsets.Reports);
        using var provider = services.BuildServiceProvider();
        var names = provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool.Name).ToList();
        Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count(), "Repeated registration must not duplicate tools.");
        var expected = TicketFlowToolCatalog.Tools
            .Where(tool => tool.Toolset is TicketFlowToolsets.None or TicketFlowToolsets.Conversations or TicketFlowToolsets.Reports)
            .Select(tool => tool.Name);
        CollectionAssert.AreEquivalent(expected.ToList(), names);
        var registry = provider.GetRequiredService<TicketFlowMcpRegistry>();
        Assert.AreEqual(TicketFlowToolsets.Conversations | TicketFlowToolsets.Reports, registry.Registered);
        Assert.IsFalse(registry.ServicesAvailable(TicketFlowToolsets.Conversations), "No conversation services are registered in this container.");
        var meta = provider.GetServices<McpServerTool>().Single(tool => string.Equals(tool.ProtocolTool.Name, "conversations_reply", StringComparison.Ordinal)).ProtocolTool;
        Assert.AreEqual("conversations", meta.Meta!["ticketflow/toolset"]!.GetValue<string>());
        Assert.AreEqual("conversation:reply", meta.Meta["ticketflow/permissions"]![0]!.GetValue<string>());
        Assert.IsFalse(meta.Annotations!.ReadOnlyHint ?? false);
        Assert.IsTrue(provider.GetServices<McpServerTool>().Single(tool => string.Equals(tool.ProtocolTool.Name, "conversations_list", StringComparison.Ordinal)).ProtocolTool.Annotations!.ReadOnlyHint);
        foreach (var tool in provider.GetServices<McpServerTool>().Select(tool => tool.ProtocolTool))
            Assert.IsTrue(tool.Annotations is { ReadOnlyHint: true } || tool.Annotations?.DestructiveHint is not null, $"{tool.Name} declares its destructive hint explicitly.");
    }

    [TestMethod]
    public void TheGuideAndDocsNameEveryTool()
    {
        var guide = TicketFlowMcpResources.Render(null);
        var docs = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "mcp.md"));
        foreach (var tool in TicketFlowToolCatalog.Tools)
        {
            Assert.Contains($"`{tool.Name}`", guide, StringComparison.Ordinal);
            Assert.Contains($"`{tool.Name}`", docs, StringComparison.Ordinal, $"docs/mcp.md must document {tool.Name}.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TicketFlow.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{2,63}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolName();
}
