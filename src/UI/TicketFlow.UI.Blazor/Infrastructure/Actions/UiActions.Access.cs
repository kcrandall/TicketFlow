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
                ? UiOutcomes.From(await administrator.CreateRoleAsync(context.Actor, role, context.Aborted), stored => UiOutcome.Success("Role created", $"access/roles/{stored.Value.Role.Id.Value}"))
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
            return UiOutcomes.From(await administrator.ActivateBreakGlassAsync(request, context.Aborted), stored => UiOutcome.Success($"Break-glass active until {UiFormat.Short(stored.Value.Grant.ValidUntil)}"));
        });

        handlers["access.serviceaccount.create"] = context => WithAsync<IServiceAccountService>(context, async serviceAccounts =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var name = context.Require("name", "Display name");
            var clientId = new ApiClientId(context.Text("clientId") ?? $"sa-{NewId()}");
            var grantScope = Scope(context, context.Text("scope"));
            var account = new ServiceAccount(clientId, AccessPartition.From(context.Scope), grantScope, name, context.Text("description"));
            var created = await serviceAccounts.CreateServiceAccountAsync(context.Actor, account, context.Aborted);
            if (created.TryGetValue(out TicketFailure createFailure))
                return UiOutcome.FromFailure(createFailure);

            if (context.Text("role") is { } initialRole && context.Session.Service<IAccessAdministrator>() is { } administrator)
            {
                var grant = new AccessGrant(
                    new GrantId(Guid.CreateVersion7().ToString("N")),
                    AccessPartition.From(context.Scope),
                    clientId,
                    new RoleId(initialRole),
                    grantScope,
                    GrantEffect.Allow,
                    justification: "Initial role assigned at service account creation");
                var grantResult = await administrator.CreateGrantAsync(context.Actor, grant, context.Aborted);
                if (grantResult.TryGetValue(out TicketFailure grantFailure))
                    return new UiOutcome(
                        UiOutcomeKind.Info,
                        $"Service account '{account.DisplayName}' created, but initial role grant failed",
                        grantFailure.Errors.Count > 0 ? grantFailure.Errors : ["Assign a role manually below."]);
                return UiOutcome.Success($"Service account '{account.DisplayName}' created with role '{initialRole}'");
            }

            return UiOutcome.Success($"Service account '{account.DisplayName}' created");
        });
        handlers["access.serviceaccount.update"] = context => WithAsync<IServiceAccountService>(context, async serviceAccounts =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var clientId = new ApiClientId(context.Require("clientId", "Client ID"));
            var result = await serviceAccounts.UpdateServiceAccountAsync(
                context.Actor,
                clientId,
                context.Require("name", "Display name"),
                context.Text("description"),
                context.RequireRevision(),
                context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("Service account updated"));
        });
        handlers["access.serviceaccount.enable"] = context => WithAsync<IServiceAccountService>(context, async serviceAccounts =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var clientId = new ApiClientId(context.Require("clientId", "Client ID"));
            var result = await serviceAccounts.SetServiceAccountEnabledAsync(
                context.Actor,
                clientId,
                isEnabled: true,
                context.Text("reason"),
                context.Revision,
                context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("Service account enabled"));
        });
        handlers["access.serviceaccount.disable"] = context => WithAsync<IServiceAccountService>(context, async serviceAccounts =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var clientId = new ApiClientId(context.Require("clientId", "Client ID"));
            var result = await serviceAccounts.SetServiceAccountEnabledAsync(
                context.Actor,
                clientId,
                isEnabled: false,
                context.Text("reason"),
                context.Revision,
                context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("Service account disabled"));
        });
        handlers["access.serviceaccount.delete"] = context => WithAsync<IServiceAccountService>(context, async serviceAccounts =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var clientId = new ApiClientId(context.Require("clientId", "Client ID"));
            var result = await serviceAccounts.DeleteServiceAccountAsync(context.Actor, clientId, context.Revision, context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("Service account deleted"));
        });
        handlers["access.serviceaccount.grant"] = context => WithAsync<IAccessAdministrator>(context, async administrator =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var clientId = new ApiClientId(context.Require("clientId", "Client ID"));
            var grant = new AccessGrant(
                new GrantId(Guid.CreateVersion7().ToString("N")),
                AccessPartition.From(context.Scope),
                clientId,
                new RoleId(context.Require("role", "Role")),
                Scope(context, context.Text("scope")),
                Enum.Parse<GrantEffect>(context.Text("effect") ?? "Allow", ignoreCase: true),
                justification: context.Text("justification"));
            return UiOutcomes.From(await administrator.CreateGrantAsync(context.Actor, grant, context.Aborted), _ => UiOutcome.Success("Role granted to service account"));
        });
        handlers["access.serviceaccount.key.create"] = context => WithAsync<IServiceAccountService>(context, async serviceAccounts =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var clientId = new ApiClientId(context.Require("clientId", "Client ID"));
            var name = context.Require("name", "Key name");
            DateTimeOffset? expiresAtUtc = null;
            if (context.Text("expiresDays") is { } rawDays)
            {
                if (!int.TryParse(rawDays, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var days) || days is < 1 or > 3650)
                    throw new UiInputException("Expiry days must be between 1 and 3650.");
                var time = context.Session.Service<TimeProvider>() ?? TimeProvider.System;
                expiresAtUtc = time.GetUtcNow().AddDays(days);
            }
            var request = new ApiKeyCreateRequest(clientId, name, expiresAtUtc);
            var issued = await serviceAccounts.IssueApiKeyAsync(context.Actor, request, context.Aborted);
            return issued switch
            {
                TicketValue<AccessChangeApplied<IssuedApiKey>> ok => new UiActionResult(
                    new UiOutcome(UiOutcomeKind.Success, $"API key '{ok.Value.Value.Key.Name}' issued", ["Copy the plaintext key below now — it will never be displayed again."]),
                    ok.Value.Value),
                TicketFailure failure => UiOutcome.FromFailure(failure),
            };
        });
        handlers["access.serviceaccount.key.revoke"] = context => WithAsync<IServiceAccountService>(context, async serviceAccounts =>
        {
            if (!context.Session.Options.ManageApiKeysInUI)
                return UiOutcome.Forbidden();
            var keyId = new ApiKeyId(context.Require("keyId", "API key"));
            var result = await serviceAccounts.RevokeApiKeyAsync(context.Actor, keyId, context.Text("reason"), context.Revision, context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("API key revoked"));
        });

        handlers["access.federation.create"] = context => WithAsync<IRoleFederationService>(context, async federation =>
        {
            var mapping = new RoleFederationMapping(
                context.Require("provider", "Provider"),
                context.Require("externalRole", "External role"),
                new RoleId(context.Require("role", "Target role")),
                AccessPartition.From(context.Scope),
                Scope(context, context.Text("scope")),
                context.Text("description"));
            var result = await federation.UpsertMappingAsync(context.Actor, RoleFederationMappingId.NewId(), mapping, isEnabled: true, expectedVersion: null, context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("External role mapping created"));
        });
        handlers["access.federation.toggle"] = context => WithAsync<IRoleFederationService>(context, async federation =>
        {
            var mappingId = new RoleFederationMappingId(context.Require("id", "Mapping"));
            var enabled = context.Flag("enabled");
            var mapping = new RoleFederationMapping(
                context.Require("provider", "Provider"),
                context.Require("externalRole", "External role"),
                new RoleId(context.Require("role", "Target role")),
                AccessPartition.From(context.Scope),
                Scope(context, context.Text("scope")),
                context.Text("description"));
            var result = await federation.UpsertMappingAsync(context.Actor, mappingId, mapping, enabled, context.RequireRevision(), context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success(enabled ? "External role mapping enabled" : "External role mapping disabled"));
        });
        handlers["access.federation.delete"] = context => WithAsync<IRoleFederationService>(context, async federation =>
        {
            var mappingId = new RoleFederationMappingId(context.Require("id", "Mapping"));
            var result = await federation.DeleteMappingAsync(context.Actor, mappingId, context.Revision, context.Aborted);
            return UiOutcomes.From(result, _ => UiOutcome.Success("External role mapping deleted"));
        });
    }

    private static GrantScope Scope(UiActionContext context, string? value) => value switch
    {
        null or "workspace" => new WorkspaceGrantScope(context.Scope.WorkspaceId),
        "tenant" => TenantGrantScope.Instance,
        _ when value.StartsWith("team:", StringComparison.Ordinal) => new TeamGrantScope(context.Scope.WorkspaceId, new TeamId(value[5..])),
        _ when value.StartsWith("inbox:", StringComparison.Ordinal) => new InboxGrantScope(context.Scope.WorkspaceId, new InboxId(value[6..])),
        _ => throw new UiInputException("Choose a valid scope."),
    };
}
