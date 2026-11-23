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
            var chunks = catalog.Definitions.Where(definition => includeSensitive == true || !definition.IsSensitive).Select(definition => definition.Permission).Chunk(DecisionBatch).ToArray();
            var contextTasks = new Task<IAccessDecisionContext>[chunks.Length];
            for (var i = 0; i < chunks.Length; i++)
                contextTasks[i] = evaluator.LoadDecisionContextAsync(actor.Principal, actor.Scope, chunks[i], http.RequestAborted);
            var contexts = await Task.WhenAll(contextTasks);
            var held = new List<string>();
            for (var i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                var context = contexts[i];
                for (var j = 0; j < chunk.Length; j++)
                    if ((await context.DecideAsync(chunk[j], resource, http.RequestAborted)).IsPermitted)
                        held.Add(chunk[j].Value);
            }
            held.Sort(StringComparer.Ordinal);
            return TicketFlowHttp.Ok(http, actor.Principal switch
            {
                UserPrincipal user => new MeResponse("user", user.UserId.Value, [.. user.Teams.Select(team => team.Value).Order(StringComparer.Ordinal)], held),
                ApiClientPrincipal client => new MeResponse("apiClient", client.ClientId.Value, [], held),
                AutomationPrincipal automation => new MeResponse("automation", automation.AutomationId, [], held),
                SystemPrincipal system => new MeResponse("system", system.Component, [], held),
            });
        }).WithName("GetMe").Produces<MeResponse>();

        group.MapGet("/access/roles", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            List(http, await administrator.ListRolesAsync(actor.Principal, http.RequestAborted),
                roles => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(roles, listing => listing.Role.Id.Value, listing => Role(listing.Role, listing.Revision), limit, cursor))))
            .WithName("ListRoles").Produces<PageResponse<RoleResponse>>();
        group.MapPost("/access/roles", async (TicketFlowActor actor, RoleRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            Map(http, await administrator.CreateRoleAsync(actor.Principal, Role(request.Id ?? "role-" + Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/roles/{stored.Role.Id.Value}", Role(stored.Role, stored.Revision), stored.Revision)))
            .WithName("CreateRole").Produces<RoleResponse>(StatusCodes.Status201Created);
        group.MapPut("/access/roles/{roleId}", async (TicketFlowActor actor, string roleId, RoleRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.UpdateRoleAsync(actor.Principal, Role(roleId, request), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Role(stored.Role, stored.Revision), stored.Revision));
        }).WithName("UpdateRole").Produces<RoleResponse>();
        group.MapDelete("/access/roles/{roleId}", async (TicketFlowActor actor, string roleId, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.DeleteRoleAsync(actor.Principal, new RoleId(roleId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteRole").Produces(StatusCodes.Status204NoContent);

        group.MapGet("/access/grants", async (TicketFlowActor actor, string? scope, string? id, int? limit, string? cursor, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            var target = Scope(actor.Scope, new GrantScopeModel(scope ?? "workspace", id));
            if (target is TenantGrantScope)
                throw new ArgumentException("Tenant grants are listed per subject, not per target.", nameof(scope));
            var size = TicketFlowHttp.PageSize(limit);
            GrantId? after = cursor is null ? null : new GrantId(TicketFlowHttp.DecodeCursor(cursor, 1)[0]);
            return List(http, await administrator.ListGrantsAsync(actor.Principal, target, size + 1, after, http.RequestAborted), page =>
            {
                var items = page.Take(size).ToList();
                return TicketFlowHttp.Ok(http, new PageResponse<GrantResponse>([.. items.Select(Grant)], page.Count > size ? TicketFlowHttp.EncodeCursor(items[^1].Grant.Id.Value) : null));
            });
        }).WithName("ListGrants").Produces<PageResponse<GrantResponse>>();
        group.MapPost("/access/grants", async (TicketFlowActor actor, GrantRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            Map(http, await administrator.CreateGrantAsync(actor.Principal, Grant(actor.Scope, request.Id ?? Ids.New(), request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/grants/{stored.Grant.Id.Value}", Grant(stored), stored.Revision)))
            .WithName("CreateGrant").Produces<GrantResponse>(StatusCodes.Status201Created);
        group.MapPut("/access/grants/{grantId}", async (TicketFlowActor actor, string grantId, GrantRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.UpdateGrantAsync(actor.Principal, Grant(actor.Scope, grantId, request), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Grant(stored), stored.Revision));
        }).WithName("UpdateGrant").Produces<GrantResponse>();
        group.MapDelete("/access/grants/{grantId}", async (TicketFlowActor actor, string grantId, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.RevokeGrantAsync(actor.Principal, new GrantId(grantId), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("RevokeGrant").Produces(StatusCodes.Status204NoContent);
        group.MapPost("/access/break-glass", async (TicketFlowActor actor, BreakGlassActivationRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (request.DurationMinutes is < 1 or > 24 * 60)
                throw new ArgumentException("Break-glass durations are 1-1440 minutes.", nameof(request.DurationMinutes));
            var breakGlass = new BreakGlassRequest(actor.Principal, new GrantId(request.Id ?? Ids.New()), new RoleId(request.Role), Scope(actor.Scope, request.Scope),
                TimeSpan.FromMinutes(request.DurationMinutes), request.Justification);
            return Map(http, await administrator.ActivateBreakGlassAsync(breakGlass, http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/grants/{stored.Grant.Id.Value}", Grant(stored), stored.Revision));
        }).WithName("ActivateBreakGlass").Produces<GrantResponse>(StatusCodes.Status201Created);

        group.MapGet("/access/field-policies", async (TicketFlowActor actor, int? limit, string? cursor, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            List(http, await administrator.ListFieldPoliciesAsync(actor.Principal, http.RequestAborted),
                policies => TicketFlowHttp.Ok(http, TicketFlowHttp.Page(policies, stored => stored.Policy.Field.Value, Policy, limit, cursor))))
            .WithName("ListFieldPolicies").Produces<PageResponse<FieldPolicyResponse>>();
        group.MapPost("/access/field-policies", async (TicketFlowActor actor, FieldPolicyRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
            Map(http, await administrator.CreateFieldPolicyAsync(actor.Principal, Policy(request), http.RequestAborted),
                stored => TicketFlowHttp.Created(http, $"access/field-policies/{stored.Policy.Field.Value}", Policy(stored), stored.Revision)))
            .WithName("CreateFieldPolicy").Produces<FieldPolicyResponse>(StatusCodes.Status201Created);
        group.MapPut("/access/field-policies/{field}", async (TicketFlowActor actor, string field, FieldPolicyRequest request, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            if (request.Field != field)
                throw new ArgumentException("The body field must match the route.", nameof(field));
            return Map(http, await administrator.UpdateFieldPolicyAsync(actor.Principal, Policy(request), revision, http.RequestAborted),
                stored => TicketFlowHttp.Ok(http, Policy(stored), stored.Revision));
        }).WithName("UpdateFieldPolicy").Produces<FieldPolicyResponse>();
        group.MapDelete("/access/field-policies/{field}", async (TicketFlowActor actor, string field, [FromServices] IAccessAdministrator administrator, HttpContext http) =>
        {
            if (!TicketFlowHttp.TryRequireRevision(http, out var revision, out var failure))
                return failure;
            return Map(http, await administrator.DeleteFieldPolicyAsync(actor.Principal, new FieldPath(field), revision, http.RequestAborted), _ => TypedResults.NoContent());
        }).WithName("DeleteFieldPolicy").Produces(StatusCodes.Status204NoContent);
        return group;
    }

    private static IResult List<T>(HttpContext http, TicketResult<T> result, Func<T, IResult> success) =>
        TicketFlowHttp.Map(http, result, success);

    private static IResult Map<T>(HttpContext http, TicketResult<AccessChangeApplied<T>> result, Func<T, IResult> success) => result switch
    {
        TicketValue<AccessChangeApplied<T>> applied => success(applied.Value.Value),
        TicketFailure failure => TicketFlowHttp.Failure(http, failure),
    };

    private static RoleResponse Role(AccessRole role, long? revision) =>
        new(role.Id.Value, role.Name, [.. role.Permissions.Select(permission => permission.Value).Order(StringComparer.Ordinal)], role.IncludesAllPermissions, role.IsBuiltIn, revision);

    private static AccessRole Role(string id, RoleRequest request) =>
        new(new RoleId(id), request.Name, (request.Permissions ?? throw new ArgumentException("Permissions are required.", nameof(request.Permissions))).Select(Permission.Parse));

    internal static GrantResponse Grant(StoredAccessGrant stored) => AccessDtos.Grant(stored);

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

    private static GrantScope Scope(TicketScope scope, GrantScopeModel dto)
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

    private static FieldPolicyResponse Policy(StoredAccessFieldPolicy stored) =>
        new(stored.Policy.Field.Value, [.. stored.Policy.AppliesTo.Select(permission => permission.Value).Order(StringComparer.Ordinal)],
            [.. stored.Policy.UnmaskedRoles.Select(role => role.Value).Order(StringComparer.Ordinal)], stored.Policy.RevealPermission?.Value, stored.Revision);
}

internal static class AccessDtos
{
    public static GrantResponse Grant(StoredAccessGrant stored)
    {
        var grant = stored.Grant;
        var subject = grant.Subject switch
        {
            UserId user => new GrantSubjectModel("user", user.Value),
            TeamId team => new GrantSubjectModel("team", team.Value),
            ApiClientId client => new GrantSubjectModel("apiClient", client.Value),
        };
        var scope = grant.Scope switch
        {
            TenantGrantScope => new GrantScopeModel("tenant"),
            WorkspaceGrantScope => new GrantScopeModel("workspace"),
            TeamGrantScope team => new GrantScopeModel("team", team.Team.Value),
            InboxGrantScope inbox => new GrantScopeModel("inbox", inbox.Inbox.Value),
        };
        return new GrantResponse(grant.Id.Value, subject, grant.Role.Value, scope, TicketFlowHttp.Token(grant.Effect),
            grant.Condition is { } condition ? Json.Parse(AccessConditionCodec.Serialize(condition)) : null,
            grant.ValidFrom, grant.ValidUntil, grant.Justification, TicketFlowHttp.Token(grant.Kind), stored.Revision);
    }
}
