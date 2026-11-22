using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Events;

namespace TicketFlow.Integrations.WalFlow.Sinks;

public static class WalFlowEventsTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="WalFlowEventSinkOptions"/> and the <see cref="WalFlowEventSink"/> (<c>walflow</c>) as a singleton <see cref="IEventSink"/>.
    /// Add named WalFlow sinks (from <c>WalFlow.Sinks.Kafka</c>, <c>WalFlow.Sinks.RabbitMq</c>, …) with their allowed scopes in
    /// <paramref name="configure"/> (<see cref="WalFlowEventSinkOptions.Add"/>).
    /// </summary>
    public static TicketFlowBuilder AddWalFlowEventSinks(this TicketFlowBuilder builder, Action<WalFlowEventSinkOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<WalFlowEventSinkOptions>().Configure(configure);
        builder.Services.TryAddSingleton<WalFlowEventSink>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventSink, WalFlowEventSink>(provider => provider.GetRequiredService<WalFlowEventSink>()));
        return builder;
    }
}
