using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TicketFlow.Access;

public static class AccessTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the access engine: permission catalog (built-ins plus every <see cref="AccessPermissionRegistration"/>),
    /// scoped <see cref="IAccessEvaluator"/>, <see cref="IAccessQueryPlanner"/>, <see cref="IAccessAdministrator"/>,
    /// <see cref="IServiceAccountService"/> and <see cref="IRoleFederationService"/>, and singleton <see cref="IAccessPrincipalResolver"/>.
    /// Uses TryAdd, so application registrations are preserved and repeated calls are safe. No store is selected; call
    /// <see cref="AddInMemoryAccessStores"/> for development or register durable stores.
    /// </summary>
    public static TicketFlowBuilder AddAccessControl(this TicketFlowBuilder builder, Action<AccessControlOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<AccessControlOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.AddOptions<ClaimsPrincipalResolverOptions>();
        services.TryAddSingleton<IPermissionCatalog>(provider =>
            PermissionCatalog.Create(provider.GetServices<AccessPermissionRegistration>().SelectMany(registration => registration.Definitions)));
        services.TryAddScoped<IAccessEvaluator, AccessEvaluator>();
        services.TryAddScoped<IAccessQueryPlanner, AccessQueryPlanner>();
        services.TryAddScoped<IAccessAdministrator, AccessAdministrator>();
        services.TryAddScoped<IServiceAccountService>(static provider => new ServiceAccountService(
            provider.GetRequiredService<IServiceAccountStore>(),
            provider.GetRequiredService<IApiKeyStore>(),
            provider.GetRequiredService<IAccessGrantStore>(),
            provider.GetRequiredService<IAccessRoleStore>(),
            provider.GetRequiredService<IPermissionCatalog>(),
            provider.GetRequiredService<IAccessAuditSink>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IOptions<AccessControlOptions>>(),
            provider.GetService<IRoleFederationMappingStore>()));
        services.TryAddScoped<IRoleFederationService>(static provider => new RoleFederationService(
            provider.GetRequiredService<IRoleFederationMappingStore>(),
            provider.GetRequiredService<IAccessRoleStore>(),
            provider.GetRequiredService<IAccessGrantStore>(),
            provider.GetRequiredService<IAccessAuditSink>(),
            provider.GetRequiredService<IPermissionCatalog>(),
            provider.GetService<TimeProvider>(),
            provider.GetService<IOptions<AccessControlOptions>>(),
            provider.GetService<IServiceAccountStore>()));
        services.TryAddSingleton<IAccessPrincipalResolver, ClaimsPrincipalResolver>();
        return builder;
    }

    /// <summary>Registers feature-owned permissions in the catalog.</summary>
    public static TicketFlowBuilder AddAccessPermissions(this TicketFlowBuilder builder, params IEnumerable<PermissionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddAccessPermissions(definitions);
        return builder;
    }

    /// <summary>
    /// Registers development-only, non-durable in-memory role, grant, field policy, service account, API key,
    /// role federation mapping and audit stores with TryAdd. State is lost on restart and never shared between processes.
    /// </summary>
    public static TicketFlowBuilder AddInMemoryAccessStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<IAccessRoleStore, InMemoryAccessRoleStore>();
        services.TryAddSingleton<IAccessGrantStore, InMemoryAccessGrantStore>();
        services.TryAddSingleton<IAccessFieldPolicyStore, InMemoryAccessFieldPolicyStore>();
        services.TryAddSingleton<InMemoryAccessAuditSink>();
        services.TryAddSingleton<IAccessAuditSink>(provider => provider.GetRequiredService<InMemoryAccessAuditSink>());
        services.TryAddSingleton<InMemoryServiceAccountStore>();
        services.TryAddSingleton<IServiceAccountStore>(static provider => provider.GetRequiredService<InMemoryServiceAccountStore>());
        services.TryAddSingleton<InMemoryApiKeyStore>();
        services.TryAddSingleton<IApiKeyStore>(static provider => provider.GetRequiredService<InMemoryApiKeyStore>());
        services.TryAddSingleton<InMemoryRoleFederationMappingStore>();
        services.TryAddSingleton<IRoleFederationMappingStore>(static provider => provider.GetRequiredService<InMemoryRoleFederationMappingStore>());
        return builder;
    }
}
