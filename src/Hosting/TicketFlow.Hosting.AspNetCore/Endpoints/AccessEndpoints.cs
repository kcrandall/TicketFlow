using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore.Contracts;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.AspNetCore.Endpoints;

internal static class AccessEndpoints
{
    private const int DecisionBatch = 32;

    public static RouteGroupBuilder Map(RouteGroupBuilder group)
    {
        group.MapGet("/me", async (TicketFlowActor actor, bool? includeSensitive, [FromServices] IAccessEvaluator evaluator, [FromServices] IPermissionCatalog catalog, HttpContext http) =>
        {
            var resource = WorkspaceResources.Workspace(actor.Scope);
            var held = new List<string>();
            foreach (var chunk in catalog.Definitions.Where(definition => includeSensitive == true || !definition.IsSensitive).Select(definition => definition.Permission).Chunk(DecisionBatch))
            {
                var context = await evaluator.LoadDecisionContextAsync(actor.Principal, actor.Scope, chunk, http.RequestAborted);
                foreach (var permission in chunk)
                    if ((await context.DecideAsync(permission, resource, http.RequestAborted)).IsPermitted)
                        held.Add(permission.Value);
            }
            IReadOnlyList<string> permissions = [.. held.Order(StringComparer.Ordinal)];
            return TicketFlowHttp.Ok(http, actor.Principal switch
            {
                UserPrincipal user => new MeDto("user", user.UserId.Value, [.. user.Teams.Select(team => team.Value).Order(StringComparer.Ordinal)], permissions),
                ApiClientPrincipal client => new MeDto("apiClient", client.ClientId.Value, [], permissions),
                AutomationPrincipal automation => new MeDto("automation", automation.AutomationId, [], permissions),
                SystemPrincipal system => new MeDto("system", system.Component, [], permissions),
            });
        }).WithName("GetMe").Produces<MeDto>();

        group.MapGet("/access/roles", async (TicketFlowActor actor, [FromServices] IAccessEvaluator evaluator, [FromServices] IAccessRoleStore roles, HttpContext http) =>
        {
            if (!await CanManageAsync(evaluator, actor, WorkspaceResources.Workspace(actor.Scope), http))
                return TicketFlowHttp.Forbidden();
            var custom = await roles.ListAsync(actor.Scope, http.RequestAborted);
            return TicketFlowHttp.Ok(http, BuiltInRoles.All.Select(role => Role(role, null)).Concat(custom.Select(stored => Role(stored.Role, stored.Revision))).ToList());
        }).WithName("ListRoles").Produces<List<RoleDto>>();
        group.MapPost("/access/roles", async (TicketFlowActor actor, RoleRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            Map(http, await administrator.CreateRoleAsync(actor.Principal, Role(request.Id ?? "role-" + Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/roles/{stored.Role.Id.Value}", Role(stored.Role, stored.Revision), stored.Revision)))
            .WithName("CreateRole").Produces<RoleDto>(StatusCodes.Status201Created);
        group.MapPut("/access/roles/{roleId}", async (TicketFlowActor actor, string roleId, RoleRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.UpdateRoleAsync(actor.Principal, Role(roleId, request), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Role(stored.Role, stored.Revision), stored.Revision));
        }).WithName("UpdateRole").Produces<RoleDto>();
        group.MapDelete("/access/roles/{roleId}", async (TicketFlowActor actor, string roleId, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.DeleteRoleAsync(actor.Principal, new RoleId(roleId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteRole").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/access/grants", async (TicketFlowActor actor, string? scope, string? id, int? limit, string? after, [FromServices] IAccessEvaluator evaluator, [FromServices] IAccessGrantStore grants, HttpContext http) =>
        {
            var target = Scope(actor.Scope, new GrantScopeDto(scope ?? "workspace", id));
            var resource = target switch
            {
                TeamGrantScope team => WorkspaceResources.Team(actor.Scope, team.Team),
                InboxGrantScope inbox => WorkspaceResources.Inbox(actor.Scope, inbox.Inbox),
                WorkspaceGrantScope => WorkspaceResources.Workspace(actor.Scope),
                TenantGrantScope => throw new ArgumentException("Tenant grants are listed per subject, not per target.", nameof(scope)),
            };
            if (!await CanManageAsync(evaluator, actor, resource, http))
                return TicketFlowHttp.Forbidden();
            var size = Math.Clamp(limit ?? 100, 1, AccessLimits.MaximumTargetPage);
            var page = await grants.ListForTargetAsync(AccessPartition.From(actor.Scope), target, size + 1, after is null ? null : new GrantId(after), http.RequestAborted);
            var items = page.Take(size).Select(Grant).ToList();
            return TicketFlowHttp.Ok(http, new PageDto<GrantDto>(items, page.Count > size ? items[^1].Id : null));
        }).WithName("ListGrants").Produces<PageDto<GrantDto>>();
        group.MapPost("/access/grants", async (TicketFlowActor actor, GrantRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            Map(http, await administrator.CreateGrantAsync(actor.Principal, Grant(actor.Scope, request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/grants/{stored.Grant.Id.Value}", Grant(stored), stored.Revision)))
            .WithName("CreateGrant").Produces<GrantDto>(StatusCodes.Status201Created);
        group.MapPut("/access/grants/{grantId}", async (TicketFlowActor actor, string grantId, GrantRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.UpdateGrantAsync(actor.Principal, Grant(actor.Scope, grantId, request), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Grant(stored), stored.Revision));
        }).WithName("UpdateGrant").Produces<GrantDto>();
        group.MapDelete("/access/grants/{grantId}", async (TicketFlowActor actor, string grantId, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.RevokeGrantAsync(actor.Principal, new GrantId(grantId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("RevokeGrant").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/access/break-glass", async (TicketFlowActor actor, BreakGlassRequestDto request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (request.DurationMinutes is < 1 or > 24 * 60)
                throw new ArgumentException("Break-glass durations are 1-1440 minutes.", nameof(request.DurationMinutes));
            var breakGlass = new BreakGlassRequest(actor.Principal, new GrantId(request.Id ?? Ids.New()), new RoleId(request.Role), Scope(actor.Scope, request.Scope),
                TimeSpan.FromMinutes(request.DurationMinutes), request.Justification);
            return Map(http, await administrator.ActivateBreakGlassAsync(breakGlass, http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/grants/{stored.Grant.Id.Value}", Grant(stored), stored.Revision));
        }).WithName("ActivateBreakGlass").Produces<GrantDto>(StatusCodes.Status201Created);

        group.MapGet("/access/field-policies", async (TicketFlowActor actor, [FromServices] IAccessEvaluator evaluator, [FromServices] IAccessFieldPolicyStore policies, HttpContext http) =>
        {
            if (!await CanManageAsync(evaluator, actor, WorkspaceResources.Workspace(actor.Scope), http))
                return TicketFlowHttp.Forbidden();
            return TicketFlowHttp.Ok(http, (await policies.ListAsync(actor.Scope, http.RequestAborted)).Select(Policy).ToList());
        }).WithName("ListFieldPolicies").Produces<List<FieldPolicyDto>>();
        group.MapPost("/access/field-policies", async (TicketFlowActor actor, FieldPolicyRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            Map(http, await administrator.CreateFieldPolicyAsync(actor.Principal, Policy(request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/field-policies/{stored.Policy.Field.Value}", Policy(stored), stored.Revision)))
            .WithName("CreateFieldPolicy").Produces<FieldPolicyDto>(StatusCodes.Status201Created);
        group.MapPut("/access/field-policies/{field}", async (TicketFlowActor actor, string field, FieldPolicyRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            if (request.Field != field)
                throw new ArgumentException("The body field must match the route.", nameof(field));
            return Map(http, await administrator.UpdateFieldPolicyAsync(actor.Principal, Policy(request), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Policy(stored), stored.Revision));
        }).WithName("UpdateFieldPolicy").Produces<FieldPolicyDto>();
        group.MapDelete("/access/field-policies/{field}", async (TicketFlowActor actor, string field, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.DeleteFieldPolicyAsync(actor.Principal, new FieldPath(field), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteFieldPolicy").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    private static async Task<bool> CanManageAsync(IAccessEvaluator evaluator, TicketFlowActor actor, AccessResource resource, HttpContext http) =>
        (await evaluator.AuthorizeAsync(new AccessRequest(actor.Principal, AccessPermissions.AccessManage, resource), http.RequestAborted)).IsPermitted;

    private static IResult Map<T>(HttpContext http, AccessChangeResult<T> result, Func<T, IResult> success) => result switch
    {
        AccessChangeApplied<T> applied => success(applied.Value),
        AccessChangeRefused refused => TicketFlowHttp.Refused(http, refused.Reason switch
        {
            AccessChangeRefusal.Forbidden => RefusalKind.Forbidden,
            AccessChangeRefusal.Invalid => RefusalKind.Invalid,
            AccessChangeRefusal.NotFound => RefusalKind.NotFound,
            AccessChangeRefusal.Conflict => RefusalKind.Conflict,
            _ => RefusalKind.Rejected,
        }, refused.Errors, refused.CurrentRevision),
    };

    private static RoleDto Role(AccessRole role, long? revision) =>
        new(role.Id.Value, role.Name, [.. role.Permissions.Select(permission => permission.Value).Order(StringComparer.Ordinal)], role.IncludesAllPermissions, role.IsBuiltIn, revision);

    private static AccessRole Role(string id, RoleRequest request) =>
        new(new RoleId(id), request.Name, (request.Permissions ?? throw new ArgumentException("Permissions are required.", nameof(request.Permissions))).Select(Permission.Parse));

    internal static GrantDto Grant(StoredAccessGrant stored) => AccessDtos.Grant(stored);

    private static AccessGrant Grant(TicketScope scope, string id, GrantRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Subject, nameof(request.Subject));
        GrantSubject subject = request.Subject.Kind switch
        {
            "user" => new UserId(request.Subject.Id),
            "team" => new TeamId(request.Subject.Id),
            "apiClient" => new ApiClientId(request.Subject.Id),
            _ => throw new ArgumentException("Subject kinds are user, team or apiClient.", nameof(request.Subject)),
        };
        return new AccessGrant(new GrantId(id), AccessPartition.From(scope), subject, new RoleId(request.Role), Scope(scope, request.Scope),
            request.Effect is null ? GrantEffect.Allow : TicketFlowHttp.Parse<GrantEffect>(request.Effect, "effect"),
            request.Condition is { ValueKind: not System.Text.Json.JsonValueKind.Null } condition ? AccessConditionCodec.Deserialize(condition.GetRawText()) : null,
            request.ValidFrom, request.ValidUntil, request.Justification);
    }

    private static GrantScope Scope(TicketScope scope, GrantScopeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto, "scope");
        return dto.Kind switch
        {
            "tenant" => TenantGrantScope.Instance,
            "workspace" => new WorkspaceGrantScope(scope.WorkspaceId),
            "team" => new TeamGrantScope(scope.WorkspaceId, new TeamId(dto.Id ?? throw new ArgumentException("Team scopes name a team.", "scope"))),
            "inbox" => new InboxGrantScope(scope.WorkspaceId, new InboxId(dto.Id ?? throw new ArgumentException("Inbox scopes name an inbox.", "scope"))),
            _ => throw new ArgumentException("Scope kinds are tenant, workspace, team or inbox.", "scope"),
        };
    }

    private static AccessFieldPolicy Policy(FieldPolicyRequest request) =>
        new(new FieldPath(request.Field), (request.AppliesTo ?? []).Select(Permission.Parse), request.UnmaskedRoles?.Select(role => new RoleId(role)),
            request.RevealPermission is null ? null : Permission.Parse(request.RevealPermission));

    private static FieldPolicyDto Policy(StoredAccessFieldPolicy stored) =>
        new(stored.Policy.Field.Value, [.. stored.Policy.AppliesTo.Select(permission => permission.Value).Order(StringComparer.Ordinal)],
            [.. stored.Policy.UnmaskedRoles.Select(role => role.Value).Order(StringComparer.Ordinal)], stored.Policy.RevealPermission?.Value, stored.Revision);
}

internal static class AccessDtos
{
    public static GrantDto Grant(StoredAccessGrant stored)
    {
        var grant = stored.Grant;
        var subject = grant.Subject switch
        {
            UserId user => new GrantSubjectDto("user", user.Value),
            TeamId team => new GrantSubjectDto("team", team.Value),
            ApiClientId client => new GrantSubjectDto("apiClient", client.Value),
        };
        var scope = grant.Scope switch
        {
            TenantGrantScope => new GrantScopeDto("tenant"),
            WorkspaceGrantScope => new GrantScopeDto("workspace"),
            TeamGrantScope team => new GrantScopeDto("team", team.Team.Value),
            InboxGrantScope inbox => new GrantScopeDto("inbox", inbox.Inbox.Value),
        };
        return new GrantDto(grant.Id.Value, subject, grant.Role.Value, scope, TicketFlowHttp.Token(grant.Effect),
            grant.Condition is { } condition ? Json.Parse(AccessConditionCodec.Serialize(condition)) : null,
            grant.ValidFrom, grant.ValidUntil, grant.Justification, TicketFlowHttp.Token(grant.Kind), stored.Revision);
    }
}
