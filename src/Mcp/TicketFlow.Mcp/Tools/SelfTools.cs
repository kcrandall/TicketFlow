using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Workspaces;

namespace TicketFlow.Mcp.Tools;

/// <summary>
/// Always-registered self-awareness tools: who the caller is, every TicketFlow tool (enabled here or not) with the permissions and
/// built-in roles it needs, and why an operation is allowed or denied. They read Access decisions only, never support data.
/// </summary>
[McpServerToolType, TicketFlowToolset(TicketFlowToolsets.None)]
public sealed class SelfTools(TicketFlowToolContext context)
{
    private const string WorkspaceHelp = "Workspace identifier; defaults to the server's default workspace.";

    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(WhoAmIOutput))]
    [Description("Returns the calling TicketFlow principal (kind, id, teams), its scope, a summary of its own access grants, the catalog permissions it holds on some resource (heldPermissions, including team/inbox grants) and workspace-wide (workspacePermissions). Sensitive permissions are only probed when includeSensitive is true because every sensitive decision is audited.")]
    [TicketFlowTool]
    public Task<CallToolResult> WhoAmI(
        [Description(WorkspaceHelp)] string? workspace = null,
        [Description("Also probe sensitive permissions (each decision records an Access audit event). Default false.")] bool includeSensitive = false,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var catalog = context.Services.GetService<IPermissionCatalog>();
            var names = (catalog?.Definitions ?? []).Select(definition => definition.Permission.Value).ToList();
            var heldTask = TicketFlowPermissionProbe.ProbeAsync(context.Services, principal, names, includeSensitive, cancellationToken);
            var grantsTask = OwnGrantsAsync(principal, cancellationToken);
            await Task.WhenAll(heldTask, grantsTask).ConfigureAwait(false);
            var held = await heldTask.ConfigureAwait(false);
            var grants = await grantsTask.ConfigureAwait(false);
            var heldNames = held.Where(pair => pair.Value == true).Select(pair => pair.Key).ToList();
            heldNames.Sort(StringComparer.Ordinal);
            var workspaceLevel = heldNames.Count == 0
                ? (IReadOnlyDictionary<string, bool?>)new Dictionary<string, bool?>(StringComparer.Ordinal)
                : await TicketFlowPermissionProbe.WorkspaceAsync(context.Services, principal, heldNames, includeSensitive, cancellationToken).ConfigureAwait(false);
            return new WhoAmIOutput(
                Principal(principal),
                Scope(principal.Scope),
                grants,
                [.. grants.Select(grant => grant.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                heldNames,
                [.. workspaceLevel.Where(pair => pair.Value == true).Select(pair => pair.Key).Order(StringComparer.Ordinal)],
                includeSensitive,
                [.. held.Where(pair => pair.Value is null).Select(pair => pair.Key).Order(StringComparer.Ordinal)],
                "heldPermissions: held on some resource (workspace, team or inbox grants); workspacePermissions: held workspace-wide. Conditions, deny rules and scopes are evaluated per resource on every call (use explain_access).");
        }, cancellationToken);

    [McpServerTool(Name = "list_toolsets", Title = "List toolsets", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ToolsetsOutput))]
    [Description("Lists every TicketFlow toolset with its description, whether this server registered it, whether its feature services are installed, whether it is enabled, and its tool names.")]
    [TicketFlowTool]
    public Task<CallToolResult> ListToolsets(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var registry = context.Services.GetService<TicketFlowMcpRegistry>();
        var toolsets = TicketFlowToolsetCatalog.All.Select(info => new ToolsetOutput(
            info.Name,
            info.Description,
            registry?.IsRegistered(info.Toolset) ?? false,
            registry?.ServicesAvailable(info.Toolset) ?? false,
            registry is not null && registry.IsRegistered(info.Toolset) && registry.ServicesAvailable(info.Toolset),
            info.Toolset == TicketFlowToolsets.Access,
            [.. TicketFlowToolCatalog.Tools.Where(tool => tool.Toolset == info.Toolset).Select(tool => tool.Name)])).ToList();
        return Task.FromResult(TicketFlowToolResults.Success(new ToolsetsOutput(
            new SelfToolsetOutput(TicketFlowToolsetCatalog.SelfName, true, [.. TicketFlowToolCatalog.Tools.Where(tool => tool.Toolset == TicketFlowToolsets.None).Select(tool => tool.Name)]),
            toolsets)));
    }

    [McpServerTool(Name = "describe_tools", Title = "Describe tools", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(DescribeToolsOutput))]
    [Description("Describes EVERY TicketFlow tool across all toolsets — including ones not enabled on this server — with its description, annotations, required permissions, the built-in roles that grant them, whether the caller currently holds them (on some resource, including team/inbox grants) and whether its toolset is enabled here. Use it to explain why something cannot be done and which role would be needed.")]
    [TicketFlowTool]
    public async Task<CallToolResult> DescribeTools(
        [Description("Only this toolset (for example 'conversations').")] string? toolset = null,
        [Description("Only this tool name.")] string? tool = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        [Description("Also probe sensitive permissions (each decision is audited). Default false.")] bool includeSensitive = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            TicketFlowToolsets? filter = toolset is null ? null
                : string.Equals(toolset, TicketFlowToolsetCatalog.SelfName, StringComparison.OrdinalIgnoreCase) ? TicketFlowToolsets.None
                : TicketFlowToolsetCatalog.Find(toolset)?.Toolset ?? throw new ArgumentException("Unknown toolset; call list_toolsets.", nameof(toolset));
            var tools = TicketFlowToolCatalog.Tools.Where(item => (filter is null || item.Toolset == filter) && (tool is null || item.Name == tool)).ToList();
            var registry = context.Services.GetService<TicketFlowMcpRegistry>();
            PrincipalRef? principal = context.Workspace(workspace) is { } id ? await context.ResolvePrincipalAsync(id, cancellationToken) : null;
            IReadOnlyDictionary<string, bool?>? decisions = principal is { } caller
                ? await TicketFlowPermissionProbe.ProbeAsync(context.Services, caller, tools.SelectMany(item => item.Permissions.Concat(item.OptionalPermissions)), includeSensitive, cancellationToken)
                : null;
            return TicketFlowToolResults.Success(new DescribeToolsOutput(
                principal is { } known ? Principal(known) : null,
                principal is not null,
                [.. tools.Select(item => new ToolDescriptionOutput(item.Name, item.Title, item.ToolsetName, item.Description, item.ReadOnly, item.Destructive, item.Idempotent,
                    [.. item.Permissions.Select(permission => Permission(permission, decisions))], item.AnyOf,
                    [.. item.OptionalPermissions.Select(permission => Permission(permission, decisions))], item.GrantedByBuiltInRoles,
                    decisions is null ? null : TicketFlowPermissionProbe.Satisfies(item, decisions), registry?.IsEnabled(item) ?? false))]), context.Options.MaximumResultBytes);
        }
        catch (ArgumentException error)
        {
            return context.Fail(TicketFlowToolError.Invalid([error.Message]));
        }
    }

    [McpServerTool(Name = "explain_access", Title = "Explain access", ReadOnly = true, Idempotent = true, UseStructuredContent = true, OutputSchemaType = typeof(ExplainAccessOutput))]
    [Description("Explains whether the caller may use a permission or a tool, optionally on one resource (conversation, contact, inbox or team): allowed, masked (allowed with redacted fields) or denied, with the reason and the built-in roles that would grant it. Never returns resource data; a resource the caller cannot read is reported as not found. Sensitive permissions are audited by Access.")]
    [TicketFlowTool]
    public Task<CallToolResult> ExplainAccess(
        [Description("A permission such as 'conversation:reply'. Give this or 'tool'.")] string? permission = null,
        [Description("A tool name such as 'conversations_reply'. Give this or 'permission'.")] string? tool = null,
        [Description("Evaluate on this conversation.")] string? conversationId = null,
        [Description("Evaluate on this contact.")] string? contactId = null,
        [Description("Evaluate on this inbox.")] string? inboxId = null,
        [Description("Evaluate on this team.")] string? teamId = null,
        [Description(WorkspaceHelp)] string? workspace = null,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            if ((permission is null) == (tool is null))
                return TicketFlowToolError.Invalid(["Give exactly one of 'permission' or 'tool'."]);
            if (new[] { conversationId, contactId, inboxId, teamId }.Count(value => value is not null) > 1)
                return TicketFlowToolError.Invalid(["Give at most one resource."]);
            TicketFlowToolDescriptor? descriptor = null;
            IReadOnlyList<string> permissions;
            if (tool is not null)
            {
                descriptor = TicketFlowToolCatalog.Find(tool);
                if (descriptor is null)
                    return TicketFlowToolError.Invalid(["Unknown tool; call describe_tools."]);
                permissions = descriptor.Permissions;
            }
            else
                permissions = [new Permission(permission!).Value];

            var (resource, described) = await ResourceAsync(principal, conversationId, contactId, inboxId, teamId, cancellationToken);
            if (resource is null)
                return TicketFlowToolError.NotFound(described);
            var evaluator = context.Service<IAccessEvaluator>();
            var catalog = context.Services.GetService<IPermissionCatalog>();
            var results = new List<PermissionDecisionOutput>(permissions.Count);
            var verdicts = new List<bool>(permissions.Count);
            IAccessDecisionContext? batch = null;
            Permission[]? parsedAll = null;
            if (permissions.Count > 1)
            {
                parsedAll = new Permission[permissions.Count];
                for (var i = 0; i < permissions.Count; i++)
                    parsedAll[i] = new Permission(permissions[i]);
                batch = await evaluator.LoadDecisionContextAsync(principal, principal.Scope, parsedAll, cancellationToken).ConfigureAwait(false);
            }
            for (var i = 0; i < permissions.Count; i++)
            {
                var value = permissions[i];
                var parsed = parsedAll is not null ? parsedAll[i] : new Permission(value);
                var definition = catalog?.Find(parsed);
                var decision = batch is not null
                    ? await batch.DecideAsync(parsed, resource, cancellationToken).ConfigureAwait(false)
                    : await evaluator.AuthorizeAsync(new AccessRequest(principal, parsed, resource), cancellationToken).ConfigureAwait(false);
                verdicts.Add(decision.IsPermitted);
                var known = definition is not null;
                var sensitive = definition?.IsSensitive ?? false;
                var roles = TicketFlowToolCatalog.RolesGranting(value);
                results.Add(decision switch
                {
                    AccessAllowed allowed => new PermissionDecisionOutput(value, known, sensitive, "allowed", null,
                        allowed.UsedBreakGlass ? "Allowed through an active break-glass grant." : "An allow grant applies.", null, roles),
                    AccessMasked masked => new PermissionDecisionOutput(value, known, sensitive, "masked", null,
                        "Allowed, but the listed fields are redacted for you (field policies); revealable fields can be revealed through an audited reveal.",
                        [.. masked.Fields.Select(field => new MaskedFieldOutput(field.Field.Value, field.Revealable))], roles),
                    AccessDenied denied => new PermissionDecisionOutput(value, known, sensitive, "denied", TicketFlowToolJson.Token(denied.Reason), Explain(denied.Reason, value), null, roles),
                });
            }
            var registry = context.Services.GetService<TicketFlowMcpRegistry>();
            bool allowedOverall = verdicts.Count == 0 || (descriptor?.AnyOf == true ? verdicts.Any(item => item) : verdicts.All(item => item));
            return new ExplainAccessOutput(
                Principal(principal),
                described,
                descriptor is null ? null : new ExplainedToolOutput(descriptor.Name, descriptor.ToolsetName, descriptor.AnyOf, registry?.IsEnabled(descriptor) ?? false,
                    descriptor.GrantedByBuiltInRoles),
                allowedOverall,
                results,
                "Feature services may apply further rules (for example closed conversations, ownership of personal macros or escalation limits); the call itself remains authoritative.");
        }, cancellationToken);

    private async Task<(AccessResource? Resource, string Described)> ResourceAsync(PrincipalRef principal, string? conversationId, string? contactId, string? inboxId, string? teamId,
        CancellationToken cancellationToken)
    {
        var scope = principal.Scope;
        if (conversationId is not null)
        {
            var id = new ConversationId(conversationId);
            var service = context.Service<IConversationService>();
            if (await service.GetAsync(principal, id, cancellationToken) is not ConversationView)
                return (null, "The conversation");
            var stored = await context.Service<IConversationStore>().GetAsync(scope, id, cancellationToken);
            return stored is null ? (null, "The conversation") : (ConversationAccess.Resource(stored), $"conversation:{conversationId}");
        }
        if (contactId is not null)
        {
            var id = new ContactId(contactId);
            if (await context.Service<IContactService>().GetAsync(principal, id, cancellationToken) is not ContactApplied<ContactView>)
                return (null, "The contact");
            var stored = await context.Service<IContactStore>().GetAsync(scope, id, cancellationToken);
            return stored is null ? (null, "The contact") : (ContactAccess.Resource(stored), $"contact:{contactId}");
        }
        if (inboxId is not null)
            return (WorkspaceResources.Inbox(scope, new InboxId(inboxId)), $"inbox:{inboxId}");
        if (teamId is not null)
            return (WorkspaceResources.Team(scope, new TeamId(teamId)), $"team:{teamId}");
        return (WorkspaceResources.Workspace(scope), "workspace");
    }

    private static string Explain(AccessDenialReason reason, string permission) => reason switch
    {
        AccessDenialReason.UnknownPermission => $"'{permission}' is not registered on this server (its feature package is not installed), so nobody holds it.",
        AccessDenialReason.ScopeMismatch => "The resource belongs to a different scope than the caller.",
        AccessDenialReason.UnsupportedPrincipal => "Automation and system principals cannot hold grants.",
        AccessDenialReason.NoMatchingGrant => "No allow grant of a role containing this permission applies to you for this resource (check team/inbox scope and grant conditions). An administrator can grant one of the listed roles.",
        AccessDenialReason.ExplicitDeny => "A deny grant applies to you for this resource and overrides every allow.",
        AccessDenialReason.FilterLimitExceeded => "The access plan exceeded its size limit and failed closed.",
        _ => "Denied.",
    };

    private async Task<List<GrantSummaryOutput>> OwnGrantsAsync(PrincipalRef principal, CancellationToken cancellationToken)
    {
        if (context.Services.GetService<IAccessGrantStore>() is not { } store)
            return [];
        List<GrantSubject> subjects = principal switch
        {
            UserPrincipal user => [user.UserId, .. user.Teams.Select(team => (GrantSubject)team)],
            ApiClientPrincipal client => [client.ClientId],
            AutomationPrincipal => [],
            SystemPrincipal => [],
        };
        if (subjects.Count == 0)
            return [];
        var grants = await store.ListForSubjectsAsync(AccessPartition.From(principal.Scope), principal.Scope.WorkspaceId, subjects, cancellationToken);
        return
        [
            .. grants.Select(stored => stored.Grant).OrderBy(grant => grant.Id.Value, StringComparer.Ordinal).Select(grant => new GrantSummaryOutput(
                grant.Role.Value,
                BuiltInRoles.IsBuiltIn(grant.Role),
                TicketFlowToolJson.Token(grant.Effect),
                TicketFlowToolJson.Token(grant.Kind),
                Subject(grant.Subject),
                grant.Scope switch
                {
                    TenantGrantScope => "tenant",
                    WorkspaceGrantScope => "workspace",
                    TeamGrantScope team => "team:" + team.Team.Value,
                    InboxGrantScope inbox => "inbox:" + inbox.Inbox.Value,
                },
                grant.Condition is not null,
                grant.ValidFrom,
                grant.ValidUntil)),
        ];
    }

    private static string Subject(GrantSubject subject) => subject switch
    {
        UserId user => "user:" + user.Value,
        TeamId team => "team:" + team.Value,
        ApiClientId client => "apiClient:" + client.Value,
    };

    private static PermissionInfoOutput Permission(string permission, IReadOnlyDictionary<string, bool?>? decisions)
    {
        var definition = TicketFlowToolCatalog.FindPermission(permission);
        return new PermissionInfoOutput(permission, definition?.IsSensitive ?? false, definition?.Description, decisions?.GetValueOrDefault(permission),
            TicketFlowToolCatalog.RolesGranting(permission));
    }

    internal static PrincipalOutput Principal(PrincipalRef principal) => principal switch
    {
        UserPrincipal user => new("user", user.UserId.Value, [.. user.Teams.Select(team => team.Value).Order(StringComparer.Ordinal)]),
        ApiClientPrincipal client => new("apiClient", client.ClientId.Value, []),
        AutomationPrincipal automation => new("automation", automation.AutomationId, []),
        SystemPrincipal system => new("system", system.Component, []),
    };

    private static ScopeOutput Scope(TicketScope scope) => new(scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value);

}
