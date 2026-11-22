using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TicketFlow.Access;

public static class AccessTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the access engine: permission catalog (built-ins plus every <see cref="AccessPermissionRegistration"/>),
    /// and scoped <see cref="IAccessEvaluator"/>, <see cref="IAccessQueryPlanner"/> and <see cref="IAccessAdministrator"/> (so they may
    /// depend on scoped application stores). Uses TryAdd, so
    /// application registrations are preserved and repeated calls are safe. No store is selected; call
    /// <see cref="AddInMemoryAccessStores"/> for development or register durable stores.
    /// </summary>
    public static TicketFlowBuilder AddAccessControl(this TicketFlowBuilder builder, Action<AccessControlOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<AccessControlOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton<IPermissionCatalog>(provider =>
            PermissionCatalog.Create(provider.GetServices<AccessPermissionRegistration>().SelectMany(registration => registration.Definitions)));
        services.TryAddScoped<IAccessEvaluator, AccessEvaluator>();
        services.TryAddScoped<IAccessQueryPlanner, AccessQueryPlanner>();
        services.TryAddScoped<IAccessAdministrator, AccessAdministrator>();
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
    /// Registers development-only, non-durable in-memory role, grant, field policy and audit stores with TryAdd. State is lost on
    /// restart and never shared between processes.
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
        return builder;
    }
}
