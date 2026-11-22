using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Events;

namespace TicketFlow.Integrations.Segment;

public static class SegmentTicketFlowBuilderExtensions
{
    /// <summary>Registers <see cref="SegmentOptions"/> and the <see cref="SegmentEventSink"/> (<c>segment</c>) as a singleton <see cref="IEventSink"/>.</summary>
    public static TicketFlowBuilder AddSegmentEvents(this TicketFlowBuilder builder, Action<SegmentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<SegmentOptions>().Configure(configure);
        builder.Services.TryAddSingleton<SegmentEventSink>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventSink, SegmentEventSink>(provider => provider.GetRequiredService<SegmentEventSink>()));
        return builder;
    }
}
