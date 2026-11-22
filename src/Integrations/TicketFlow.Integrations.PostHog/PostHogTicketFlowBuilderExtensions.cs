using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Events;

namespace TicketFlow.Integrations.PostHog;

public static class PostHogTicketFlowBuilderExtensions
{
    /// <summary>Registers <see cref="PostHogOptions"/> and the <see cref="PostHogEventSink"/> (<c>posthog</c>) as a singleton <see cref="IEventSink"/>.</summary>
    public static TicketFlowBuilder AddPostHogEvents(this TicketFlowBuilder builder, Action<PostHogOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<PostHogOptions>().Configure(configure);
        builder.Services.TryAddSingleton<PostHogEventSink>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventSink, PostHogEventSink>(provider => provider.GetRequiredService<PostHogEventSink>()));
        return builder;
    }
}
