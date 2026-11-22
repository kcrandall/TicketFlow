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

    [McpServerTool(Name = "whoami", Title = "Who am I", ReadOnly = true, Idempotent = true)]
    [Description("Returns the calling TicketFlow principal (kind, id, teams), its scope, a summary of its own access grants and the catalog permissions it holds at workspace level. Sensitive permissions are only probed when includeSensitive is true because every sensitive decision is audited.")]
    [TicketFlowTool]
    public Task<CallToolResult> WhoAmI(
        [Description(WorkspaceHelp)] string? workspace = null,
        [Description("Also probe sensitive permissions (each decision records an Access audit event). Default false.")] bool includeSensitive = false,
        CancellationToken cancellationToken = default) =>
        context.RunAsync(workspace, async principal =>
        {
            var catalog = context.Services.GetService<IPermissionCatalog>();
            var definitions = catalog?.Definitions ?? [];
            var decisions = await TicketFlowPermissionProbe.ProbeAsync(context.Services, principal, definitions.Select(definition => definition.Permission.Value), includeSensitive,
                cancellationToken);
            var grants = await OwnGrantsAsync(principal, cancellationToken);
            return new
            {
                principal = Principal(principal),
                scope = Scope(principal.Scope),
                grants,
                roles = grants.Select(grant => grant.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                workspacePermissions = decisions.Where(pair => pair.Value == true).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList(),
                sensitivePermissionsProbed = includeSensitive,
                notProbed = decisions.Where(pair => pair.Value is null).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList(),
                note = "Workspace-level decisions; narrower team or inbox grants, conditions and deny rules are evaluated per resource on every call (use explain_access).",
            };
        }, cancellationToken);

    [McpServerTool(Name = "list_toolsets", Title = "List toolsets", ReadOnly = true, Idempotent = true)]
    [Description("Lists every TicketFlow toolset with its description, whether this server registered it, whether its feature services are installed, whether it is enabled, and its tool names.")]
    [TicketFlowTool]
    public Task<CallToolResult> ListToolsets(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var registry = context.Services.GetService<TicketFlowMcpRegistry>();
        var toolsets = TicketFlowToolsetCatalog.All.Select(info => new
        {
            name = info.Name,
            description = info.Description,
            registered = registry?.IsRegistered(info.Toolset) ?? false,
            servicesAvailable = registry?.ServicesAvailable(info.Toolset) ?? false,
            enabled = registry is not null && registry.IsRegistered(info.Toolset) && registry.ServicesAvailable(info.Toolset),
            sensitive = info.Toolset == TicketFlowToolsets.Access,
            tools = TicketFlowToolCatalog.Tools.Where(tool => tool.Toolset == info.Toolset).Select(tool => tool.Name).ToList(),
        }).ToList();
        return Task.FromResult(TicketFlowToolResults.Success(new
        {
            self = new { name = TicketFlowToolsetCatalog.SelfName, enabled = true, tools = TicketFlowToolCatalog.Tools.Where(tool => tool.Toolset == TicketFlowToolsets.None).Select(tool => tool.Name).ToList() },
            toolsets,
        }));
    }

    [McpServerTool(Name = "describe_tools", Title = "Describe tools", ReadOnly = true, Idempotent = true)]
    [Description("Describes EVERY TicketFlow tool across all toolsets — including ones not enabled on this server — with its description, annotations, required permissions, the built-in roles that grant them, whether the caller currently holds them (workspace level) and whether its toolset is enabled here. Use it to explain why something cannot be done and which role would be needed.")]
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
            return TicketFlowToolResults.Success(new
            {
                caller = principal is { } known ? Principal(known) : null,
                callerResolved = principal is not null,
                tools = tools.Select(item => new
                {
                    name = item.Name,
                    title = item.Title,
                    toolset = item.ToolsetName,
                    description = item.Description,
                    readOnly = item.ReadOnly,
                    destructive = item.Destructive,
                    idempotent = item.Idempotent,
                    requiredPermissions = item.Permissions.Select(permission => Permission(permission, decisions)).ToList(),
                    anyOf = item.AnyOf,
                    optionalPermissions = item.OptionalPermissions.Select(permission => Permission(permission, decisions)).ToList(),
                    grantedByBuiltInRoles = item.GrantedByBuiltInRoles,
                    callerHasPermissions = decisions is null ? null : TicketFlowPermissionProbe.Satisfies(item, decisions),
                    enabledOnServer = registry?.IsEnabled(item) ?? false,
                }).ToList(),
            }, context.Options.MaximumResultBytes);
        }
        catch (ArgumentException error)
        {
            return context.Fail(TicketFlowToolError.Invalid([error.Message]));
        }
    }

    [McpServerTool(Name = "explain_access", Title = "Explain access", ReadOnly = true, Idempotent = true)]
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
            var results = new List<object>();
            var verdicts = new List<bool>();
            foreach (var value in permissions)
            {
                var parsed = new Permission(value);
                var definition = catalog?.Find(parsed);
                var decision = await evaluator.AuthorizeAsync(new AccessRequest(principal, parsed, resource), cancellationToken);
                verdicts.Add(decision.IsPermitted);
                results.Add(decision switch
                {
                    AccessAllowed allowed => new
                    {
                        permission = value, known = definition is not null, sensitive = definition?.IsSensitive ?? false, decision = "allowed",
                        reason = (string?)null, explanation = allowed.UsedBreakGlass ? "Allowed through an active break-glass grant." : "An allow grant applies.",
                        maskedFields = (IReadOnlyList<object>?)null, grantedByBuiltInRoles = TicketFlowToolCatalog.RolesGranting([value]),
                    },
                    AccessMasked masked => new
                    {
                        permission = value, known = definition is not null, sensitive = definition?.IsSensitive ?? false, decision = "masked",
                        reason = (string?)null, explanation = "Allowed, but the listed fields are redacted for you (field policies); revealable fields can be revealed through an audited reveal.",
                        maskedFields = (IReadOnlyList<object>?)[.. masked.Fields.Select(field => (object)new { field = field.Field.Value, revealable = field.Revealable })],
                        grantedByBuiltInRoles = TicketFlowToolCatalog.RolesGranting([value]),
                    },
                    AccessDenied denied => new
                    {
                        permission = value, known = definition is not null, sensitive = definition?.IsSensitive ?? false, decision = "denied",
                        reason = (string?)TicketFlowToolJson.Token(denied.Reason), explanation = Explain(denied.Reason, value),
                        maskedFields = (IReadOnlyList<object>?)null, grantedByBuiltInRoles = TicketFlowToolCatalog.RolesGranting([value]),
                    },
                });
            }
            var registry = context.Services.GetService<TicketFlowMcpRegistry>();
            bool allowedOverall = verdicts.Count == 0 || (descriptor?.AnyOf == true ? verdicts.Any(item => item) : verdicts.All(item => item));
            return new
            {
                principal = Principal(principal),
                resource = described,
                tool = descriptor is null ? null : new
                {
                    name = descriptor.Name,
                    toolset = descriptor.ToolsetName,
                    anyOf = descriptor.AnyOf,
                    enabledOnServer = registry?.IsEnabled(descriptor) ?? false,
                    grantedByBuiltInRoles = descriptor.GrantedByBuiltInRoles,
                },
                allowed = allowedOverall,
                permissions = results,
                note = "Feature services may apply further rules (for example closed conversations, ownership of personal macros or escalation limits); the call itself remains authoritative.",
            };
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

    private async Task<List<GrantSummary>> OwnGrantsAsync(PrincipalRef principal, CancellationToken cancellationToken)
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
            .. grants.Select(stored => stored.Grant).OrderBy(grant => grant.Id.Value, StringComparer.Ordinal).Select(grant => new GrantSummary(
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

    private static object Permission(string permission, IReadOnlyDictionary<string, bool?>? decisions)
    {
        var definition = TicketFlowToolCatalog.FindPermission(permission);
        return new
        {
            permission,
            sensitive = definition?.IsSensitive ?? false,
            description = definition?.Description,
            callerHas = decisions?.GetValueOrDefault(permission),
            grantedByBuiltInRoles = TicketFlowToolCatalog.RolesGranting([permission]),
        };
    }

    internal static object Principal(PrincipalRef principal) => principal switch
    {
        UserPrincipal user => new { kind = "user", id = user.UserId.Value, teams = user.Teams.Select(team => team.Value).Order(StringComparer.Ordinal).ToList() },
        ApiClientPrincipal client => new { kind = "apiClient", id = client.ClientId.Value, teams = new List<string>() },
        AutomationPrincipal automation => new { kind = "automation", id = automation.AutomationId, teams = new List<string>() },
        SystemPrincipal system => new { kind = "system", id = system.Component, teams = new List<string>() },
    };

    private static object Scope(TicketScope scope) => new
    {
        tenant = scope.TenantId,
        project = scope.ProjectId,
        environment = scope.EnvironmentId,
        app = scope.AppId,
        workspace = scope.WorkspaceId.Value,
    };

    private sealed record GrantSummary(string Role, bool BuiltIn, string Effect, string Kind, string Subject, string Scope, bool Conditional, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil);
}
