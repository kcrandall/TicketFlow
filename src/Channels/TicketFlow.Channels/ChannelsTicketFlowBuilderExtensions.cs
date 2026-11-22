using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Conversations;

namespace TicketFlow.Channels;

public static class ChannelsTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the channel runtime: <see cref="ChannelPermissions"/>, options, <see cref="IChannelRegistry"/> over registered
    /// <see cref="ITicketChannel"/>s, the scoped <see cref="IInboundChannelPipeline"/>, <see cref="IChannelReplyService"/>,
    /// <see cref="IChannelDeliveryIngestor"/> and <see cref="IChannelEndpointService"/>, and <see cref="ChannelOutboundDispatcher"/> as an
    /// <see cref="IConversationOutboxHandler"/>. Requires Access, workspaces, contacts, conversations and channel state stores. Uses
    /// TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddChannels(this TicketFlowBuilder builder, Action<ChannelsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ChannelsOptions>();
        services.AddOptions<ConversationOutboxDispatcherOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ChannelPermissionsMarker)))
        {
            services.AddSingleton<ChannelPermissionsMarker>();
            services.AddAccessPermissions(ChannelPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IChannelRegistry, ChannelRegistry>();
        services.TryAddSingleton<IChannelWorkerPrincipalResolver, OptionsChannelWorkerPrincipalResolver>();
        services.TryAddScoped<IChannelAddressOwnershipValidator, CrossScopeAddressOwnershipValidator>();
        services.TryAddScoped<IInboundChannelPipeline, InboundChannelPipeline>();
        services.TryAddScoped<IChannelReplyService, ChannelReplyService>();
        services.TryAddScoped<IChannelDeliveryIngestor, ChannelDeliveryIngestor>();
        services.TryAddScoped<IChannelEndpointService, ChannelEndpointService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, ChannelOutboundDispatcher>());
        return builder;
    }

    /// <summary>Registers <see cref="InMemoryChannelStore"/> (development only: process-local, bounded, not durable) for all four channel store contracts.</summary>
    public static TicketFlowBuilder AddInMemoryChannelStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<InMemoryChannelStore>();
        services.TryAddSingleton<IChannelEndpointStore>(provider => provider.GetRequiredService<InMemoryChannelStore>());
        services.TryAddSingleton<IChannelConversationStateStore>(provider => provider.GetRequiredService<InMemoryChannelStore>());
        services.TryAddSingleton<IChannelThreadStore>(provider => provider.GetRequiredService<InMemoryChannelStore>());
        services.TryAddSingleton<IChannelDispatchStore>(provider => provider.GetRequiredService<InMemoryChannelStore>());
        return builder;
    }

    private sealed class ChannelPermissionsMarker;
}
