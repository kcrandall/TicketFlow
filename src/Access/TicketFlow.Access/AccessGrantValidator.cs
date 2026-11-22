namespace TicketFlow.Access;

/// <summary>Contextual grant validation shared by the administrator and custom provisioning code.</summary>
public static class AccessGrantValidator
{
    /// <summary>
    /// Validates <paramref name="grant"/> given its resolved <paramref name="role"/> (<see langword="null"/> when it does not exist):
    /// the role must exist, tenant grants must use built-in roles, and the grant must not already be expired at <paramref name="now"/>.
    /// </summary>
    public static GrantValidationResult Validate(AccessGrant grant, AccessRole? role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var errors = new List<string>();
        if (role is null)
            errors.Add("The grant's role does not exist in the grant's workspace.");
        else if (role.Id != grant.Role)
            errors.Add("The resolved role does not match the grant's role.");
        else if (!role.IsBuiltIn && grant.Scope is TenantGrantScope)
            errors.Add("Tenant grants can only use built-in roles.");
        if (grant.ValidUntil is { } until && until <= now)
            errors.Add("The grant is already expired.");
        return errors.Count == 0 ? new GrantValid(grant) : new GrantInvalid(errors);
    }
}
