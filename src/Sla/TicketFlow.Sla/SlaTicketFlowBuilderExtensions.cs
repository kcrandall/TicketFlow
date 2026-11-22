using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Conversations;

namespace TicketFlow.Sla;

public static class SlaTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the SLA feature: <see cref="SlaPermissions.Definitions"/>, <see cref="SlaOptions"/>, scoped
    /// <see cref="ISlaPolicyService"/>, <see cref="ISlaService"/>, <see cref="SlaConversationTracker"/> (also as an
    /// <see cref="IConversationOutboxHandler"/>, so <c>ConversationOutboxDispatcher</c> drives it) and <see cref="SlaTimerScheduler"/>.
    /// Requires the access engine, workspace and conversation registrations and SLA stores; uses TryAdd so application
    /// registrations win.
    /// </summary>
    public static TicketFlowBuilder AddSla(this TicketFlowBuilder builder, Action<SlaOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<SlaOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(SlaPermissionsMarker)))
        {
            services.AddSingleton<SlaPermissionsMarker>();
            services.AddAccessPermissions(SlaPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISlaPolicyService, SlaPolicyService>();
        services.TryAddScoped<ISlaService, SlaService>();
        services.TryAddScoped<SlaConversationTracker>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, SlaConversationTracker>());
        services.TryAddScoped<SlaTimerScheduler>();
        return builder;
    }

    /// <summary>
    /// Registers the development-only in-memory stores (process-local, not durable) as <see cref="ISlaPolicyStore"/> and
    /// <see cref="ISlaTrackerStore"/>.
    /// </summary>
    public static TicketFlowBuilder AddInMemorySlaStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<ISlaPolicyStore, InMemorySlaPolicyStore>();
        services.TryAddSingleton<ISlaTrackerStore>(provider => new InMemorySlaTrackerStore(provider.GetService<TimeProvider>()));
        return builder;
    }

    private sealed class SlaPermissionsMarker;
}
