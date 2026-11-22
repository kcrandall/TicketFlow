using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TicketFlow.Events.Webhooks;

public static class EventWebhooksTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="EventWebhookOptions"/> and the <see cref="WebhookEventSink"/> (<c>webhook</c>) as a singleton <see cref="IEventSink"/>.
    /// Register an <see cref="IEventWebhookKeyResolver"/> over your secret store; without one deliveries fail with <c>key-missing</c>.
    /// </summary>
    public static TicketFlowBuilder AddEventWebhooks(this TicketFlowBuilder builder, Action<EventWebhookOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<EventWebhookOptions>();
        if (configure is not null)
            builder.Services.Configure(configure);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<WebhookEventSink>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventSink, WebhookEventSink>(provider => provider.GetRequiredService<WebhookEventSink>()));
        return builder;
    }
}
