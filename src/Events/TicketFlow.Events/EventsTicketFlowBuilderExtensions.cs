using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Routing;
using TicketFlow.Surveys;

namespace TicketFlow.Events;

public static class EventsTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the integration event stream: <see cref="EventPermissions.Definitions"/>, <see cref="EventsOptions"/>, scoped
    /// <see cref="IEventPublisher"/>, <see cref="EventProjector"/> (also as an <see cref="IConversationOutboxHandler"/>, consumer
    /// <c>events</c>), <see cref="EventContactFollower"/>, <see cref="EventSurveyFollower"/> (needs an <see cref="ISurveyStore"/> when
    /// resolved), <see cref="EventRoutingObserver"/> (as an <see cref="IRoutingEventObserver"/>), <see cref="IEventSubscriptionService"/>,
    /// <see cref="EventDeliveryWorker"/>, <see cref="EventContentHydrator"/>, <see cref="EventRetentionWorker"/> and the
    /// <see cref="InProcessEventSink"/> (<c>in-process</c>) for registered <see cref="IEventSubscriber"/>s. Requires the access engine,
    /// conversation and contact registrations and an event store; uses TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddEvents(this TicketFlowBuilder builder, Action<EventsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<EventsOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(EventsPermissionsMarker)))
        {
            services.AddSingleton<EventsPermissionsMarker>();
            services.AddAccessPermissions(EventPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IEventPublisher, EventPublisher>();
        services.TryAddScoped<EventProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, EventProjector>());
        services.TryAddScoped<EventContactFollower>();
        services.TryAddScoped(provider => ActivatorUtilities.CreateInstance<EventSurveyFollower>(provider,
            provider.GetService<ISurveyStore>() ?? throw new InvalidOperationException("EventSurveyFollower requires an ISurveyStore registration.")));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IRoutingEventObserver, EventRoutingObserver>());
        services.TryAddScoped(provider => new EventContentHydrator(provider.GetService<IConversationService>(), provider.GetService<IContactService>(),
            provider.GetService<ISurveyStore>()));
        services.TryAddScoped<IEventSubscriptionService, EventSubscriptionService>();
        services.TryAddScoped<EventDeliveryWorker>();
        services.TryAddScoped<EventRetentionWorker>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IEventSink, InProcessEventSink>());
        return builder;
    }

    /// <summary>Registers a host subscriber for the <c>in-process</c> sink (subscription target = its name).</summary>
    public static TicketFlowBuilder AddEventSubscriber<TSubscriber>(this TicketFlowBuilder builder)
        where TSubscriber : class, IEventSubscriber
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IEventSubscriber, TSubscriber>());
        return builder;
    }

    /// <summary>Registers the development-only <see cref="InMemoryEventStore"/> (process-local, bounded, not durable) as <see cref="IEventLog"/> and <see cref="IEventSubscriptionStore"/>.</summary>
    public static TicketFlowBuilder AddInMemoryEventStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton(provider => new InMemoryEventStore(provider.GetService<TimeProvider>()));
        builder.Services.TryAddSingleton<IEventLog>(provider => provider.GetRequiredService<InMemoryEventStore>());
        builder.Services.TryAddSingleton<IEventSubscriptionStore>(provider => provider.GetRequiredService<InMemoryEventStore>());
        return builder;
    }

    private sealed class EventsPermissionsMarker;
}
