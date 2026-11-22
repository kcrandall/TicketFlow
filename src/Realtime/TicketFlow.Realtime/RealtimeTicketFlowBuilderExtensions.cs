using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Conversations;
using TicketFlow.Routing;

namespace TicketFlow.Realtime;

public static class RealtimeTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers realtime fan-out: <see cref="InProcessRealtimeBus"/> (replace <see cref="IRealtimeBus"/> for multi-node fan-out),
    /// <see cref="BusRealtimePublisher"/>, the singleton <see cref="RealtimeSubscriptionRegistry"/> as <see cref="IRealtimeSubscriptions"/>,
    /// scoped <see cref="IRealtimeAgentActions"/>, <see cref="RealtimeOutboxHandler"/> as an <see cref="IConversationOutboxHandler"/>
    /// (consumer <c>realtime</c>) and <see cref="RealtimeRoutingEventObserver"/> as an <see cref="IRoutingEventObserver"/>. Requires Access,
    /// conversations and their stores. Uses TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddRealtime(this TicketFlowBuilder builder, Action<RealtimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<RealtimeOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRealtimeBus, InProcessRealtimeBus>();
        services.TryAddSingleton<IRealtimePublisher, BusRealtimePublisher>();
        services.TryAddSingleton<RealtimeSubscriptionRegistry>();
        services.TryAddSingleton<IRealtimeSubscriptions>(provider => provider.GetRequiredService<RealtimeSubscriptionRegistry>());
        services.TryAddScoped<IRealtimeAgentActions, RealtimeAgentActions>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, RealtimeOutboxHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRoutingEventObserver, RealtimeRoutingEventObserver>());
        return builder;
    }
}
