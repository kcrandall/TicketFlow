using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Realtime;

namespace TicketFlow.Channels.LiveChat;

public static class LiveChatTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers live chat: <see cref="LiveChatPermissions"/>, options, <see cref="LiveChatChannel"/> as an <see cref="ITicketChannel"/>,
    /// scoped <see cref="ILiveChatVisitorService"/>, <see cref="ILiveChatWidgetService"/> and <see cref="ILiveChatAgentService"/>, and
    /// <see cref="LiveChatSessionDirectory"/> as an <see cref="IVisitorSessionDirectory"/>. Requires <c>AddChannels()</c> (with a worker
    /// principal), an <see cref="IRealtimePublisher"/> (for example <c>AddRealtime()</c>) and live-chat stores. Optional application
    /// services: <see cref="IAttachmentStore"/>, <see cref="ILiveChatIdentitySecretResolver"/>, <see cref="IVisitorTokenValidator"/> and
    /// the help center's <c>IHelpCenterSearch</c>. Uses TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddLiveChat(this TicketFlowBuilder builder, Action<LiveChatOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<LiveChatOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(LiveChatPermissionsMarker)))
        {
            services.AddSingleton<LiveChatPermissionsMarker>();
            services.AddAccessPermissions(LiveChatPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new LiveChatChannel(provider.GetRequiredService<ILiveChatSessionStore>(), provider.GetRequiredService<TicketFlow.Conversations.IConversationStore>(),
            provider.GetRequiredService<IRealtimePublisher>(), provider.GetRequiredService<IOptions<LiveChatOptions>>(), provider.GetRequiredService<TimeProvider>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITicketChannel, LiveChatChannel>(provider => provider.GetRequiredService<LiveChatChannel>()));
        services.TryAddScoped<ILiveChatVisitorService, LiveChatVisitorService>();
        services.TryAddScoped<ILiveChatWidgetService, LiveChatWidgetService>();
        services.TryAddScoped<ILiveChatAgentService, LiveChatAgentService>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IVisitorSessionDirectory, LiveChatSessionDirectory>());
        return builder;
    }

    /// <summary>Registers <see cref="InMemoryLiveChatStore"/> for both live-chat store contracts (development only: process-local, bounded, not durable).</summary>
    public static TicketFlowBuilder AddInMemoryLiveChatStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<InMemoryLiveChatStore>();
        services.TryAddSingleton<ILiveChatWidgetStore>(provider => provider.GetRequiredService<InMemoryLiveChatStore>());
        services.TryAddSingleton<ILiveChatSessionStore>(provider => provider.GetRequiredService<InMemoryLiveChatStore>());
        return builder;
    }

    private sealed class LiveChatPermissionsMarker;
}
