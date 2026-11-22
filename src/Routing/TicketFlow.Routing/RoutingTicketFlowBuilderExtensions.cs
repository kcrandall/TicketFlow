using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Conversations;

namespace TicketFlow.Routing;

public static class RoutingTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers routing: permission definitions (identical to the Access built-ins, so idempotent), <see cref="RoutingOptions"/>, scoped
    /// <see cref="IRoutingPolicyService"/>, <see cref="IAgentAvailabilityService"/>, <see cref="IConversationCollisionService"/>,
    /// <see cref="IRoutingQueueService"/>, <see cref="IRoutingEngine"/>, <see cref="RoutingMaintenanceWorker"/> and the
    /// <see cref="RoutingOutboxHandler"/> as an <see cref="IConversationOutboxHandler"/> (consumer <c>routing</c>). Requires the access
    /// engine, workspace and conversation registrations and routing stores; uses TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddRouting(this TicketFlowBuilder builder, Action<RoutingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<RoutingOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(RoutingPermissionsMarker)))
        {
            services.AddSingleton<RoutingPermissionsMarker>();
            services.AddAccessPermissions(RoutingPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IRoutingPolicyService, RoutingPolicyService>();
        services.TryAddScoped<IAgentAvailabilityService, AgentAvailabilityService>();
        services.TryAddScoped<IConversationCollisionService, ConversationCollisionService>();
        services.TryAddScoped<IRoutingQueueService, RoutingQueueService>();
        services.TryAddScoped<IRoutingEngine, RoutingEngine>();
        services.TryAddScoped<RoutingMaintenanceWorker>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, RoutingOutboxHandler>());
        return builder;
    }

    /// <summary>
    /// Registers the development-only <see cref="InMemoryRoutingStore"/> (process-local, bounded, not durable) as every routing store
    /// contract.
    /// </summary>
    public static TicketFlowBuilder AddInMemoryRoutingStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton(provider => new InMemoryRoutingStore(provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IRoutingPolicyStore>(provider => provider.GetRequiredService<InMemoryRoutingStore>());
        services.TryAddSingleton<IAgentAvailabilityStore>(provider => provider.GetRequiredService<InMemoryRoutingStore>());
        services.TryAddSingleton<IRoutingAssignmentStore>(provider => provider.GetRequiredService<InMemoryRoutingStore>());
        services.TryAddSingleton<IRoutingQueueStore>(provider => provider.GetRequiredService<InMemoryRoutingStore>());
        services.TryAddSingleton<IConversationPresenceStore>(provider => provider.GetRequiredService<InMemoryRoutingStore>());
        return builder;
    }

    private sealed class RoutingPermissionsMarker;
}
