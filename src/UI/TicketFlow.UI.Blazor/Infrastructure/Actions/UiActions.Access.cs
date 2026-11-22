using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.UI.Blazor.Infrastructure;

public static partial class UiActions
{
    private static void RegisterAccess(Dictionary<string, UiActionHandler> handlers)
    {
        handlers["access.role.save"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
        {
            var existing = context.Text("id");
            var permissions = context.All("permission").Select(permission => new Permission(permission));
            var role = new AccessRole(new RoleId(existing ?? context.Text("newid") ?? Slug(context.Require("name", "Name"))), context.Require("name", "Name"), permissions);
            return existing is null
                ? UiOutcomes.From(await administrator.CreateRoleAsync(context.Actor, role, context.Aborted), stored => UiOutcome.Success("Role created", $"access/roles/{stored.Role.Id.Value}"))
                : UiOutcomes.From(await administrator.UpdateRoleAsync(context.Actor, role, context.RequireRevision(), context.Aborted), _ => UiOutcome.Success("Role saved"));
        });
        handlers["access.role.delete"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
            UiOutcomes.From(await administrator.DeleteRoleAsync(context.Actor, new RoleId(context.Require("id", "Role")), context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success("Role deleted", "access")));
        handlers["access.grant.create"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
        {
            var subjectId = context.Require("subject", "Subject");
            GrantSubject subject = context.Text("subjectKind") switch
            {
                "team" => new TeamId(subjectId),
                "client" => new ApiClientId(subjectId),
                _ => new UserId(subjectId),
            };
            var grant = new AccessGrant(new GrantId(Guid.CreateVersion7().ToString("N")), AccessPartition.From(context.Scope), subject, new RoleId(context.Require("role", "Role")),
                Scope(context, context.Text("scope")), Enum.Parse<GrantEffect>(context.Text("effect") ?? "Allow", ignoreCase: true), justification: context.Text("justification"));
            return UiOutcomes.From(await administrator.CreateGrantAsync(context.Actor, grant, context.Aborted), _ => UiOutcome.Success("Grant created"));
        });
        handlers["access.grant.revoke"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
            UiOutcomes.From(await administrator.RevokeGrantAsync(context.Actor, new GrantId(context.Require("id", "Grant")), context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success("Grant revoked")));
        handlers["access.field.create"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
        {
            var field = context.Text("customField") ?? context.Require("field", "Field");
            var unmasked = (context.Text("unmasked") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(role => new RoleId(role));
            var policy = new AccessFieldPolicy(new FieldPath(field), [AccessPermissions.ContactRead], unmasked, context.Text("reveal") is { } reveal ? new Permission(reveal) : null);
            return UiOutcomes.From(await administrator.CreateFieldPolicyAsync(context.Actor, policy, context.Aborted), _ => UiOutcome.Success("Field policy created"));
        });
        handlers["access.field.delete"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
            UiOutcomes.From(await administrator.DeleteFieldPolicyAsync(context.Actor, new FieldPath(context.Require("field", "Field")), context.RequireRevision(), context.Aborted),
                _ => UiOutcome.Success("Field policy deleted")));
        handlers["access.breakglass"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
        {
            var minutes = context.Int("minutes") ?? 60;
            var request = new BreakGlassRequest(context.Actor, new GrantId(Guid.CreateVersion7().ToString("N")), new RoleId(context.Require("role", "Role")), Scope(context, context.Text("scope")),
                TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60)), context.Require("justification", "A justification"));
            return UiOutcomes.From(await administrator.ActivateBreakGlassAsync(request, context.Aborted), stored => UiOutcome.Success($"Break-glass active until {UiFormat.Short(stored.Grant.ValidUntil)}"));
        });
    }

    private static GrantScope Scope(UiActionContext context, string? value) => value switch
    {
        null or "workspace" => new WorkspaceGrantScope(context.Scope.WorkspaceId),
        _ when value.StartsWith("team:", StringComparison.Ordinal) => new TeamGrantScope(context.Scope.WorkspaceId, new TeamId(value[5..])),
        _ when value.StartsWith("inbox:", StringComparison.Ordinal) => new InboxGrantScope(context.Scope.WorkspaceId, new InboxId(value[6..])),
        _ => throw new UiInputException("Choose a valid scope."),
    };
}
