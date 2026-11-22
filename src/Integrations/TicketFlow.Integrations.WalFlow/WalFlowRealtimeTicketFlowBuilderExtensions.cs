using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Realtime;

namespace TicketFlow.Integrations.WalFlow;

public static class WalFlowRealtimeTicketFlowBuilderExtensions
{
    /// <summary>Registers <see cref="WalFlowRealtimeOptions"/> and <see cref="WalFlowRealtimeSink"/>.</summary>
    public static TicketFlowBuilder AddWalFlowRealtime(this TicketFlowBuilder builder, Action<WalFlowRealtimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.Services.AddOptions<WalFlowRealtimeOptions>();
        if (configure is not null)
            options.Configure(configure);
        builder.Services.TryAddSingleton<WalFlowRealtimeSink>();
        return builder;
    }

    /// <summary>Replaces the default in-process <see cref="IRealtimeBus"/> with <see cref="CrossNodeRealtimeBus"/> over the registered <see cref="IRealtimeBackplane"/>.</summary>
    public static TicketFlowBuilder UseCrossNodeRealtimeBus(this TicketFlowBuilder builder, Action<WalFlowRealtimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddWalFlowRealtime(configure);
        builder.Services.TryAddSingleton<CrossNodeRealtimeBus>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IRealtimeBus>(provider => provider.GetRequiredService<CrossNodeRealtimeBus>()));
        return builder;
    }

    /// <summary>Registers <see cref="PostgresRealtimeBackplane"/> as the <see cref="IRealtimeBackplane"/> and switches <see cref="IRealtimeBus"/> to <see cref="CrossNodeRealtimeBus"/>.</summary>
    public static TicketFlowBuilder UsePostgresRealtimeBackplane(this TicketFlowBuilder builder, Action<WalFlowRealtimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseCrossNodeRealtimeBus(configure);
        builder.Services.TryAddSingleton<PostgresRealtimeBackplane>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IRealtimeBackplane>(provider => provider.GetRequiredService<PostgresRealtimeBackplane>()));
        return builder;
    }
}
