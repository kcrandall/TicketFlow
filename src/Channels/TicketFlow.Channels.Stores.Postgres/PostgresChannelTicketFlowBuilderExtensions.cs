using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Channels.Stores.Postgres;

public static class PostgresChannelTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresChannelStore"/> over the application-owned <paramref name="dataSource"/> for all four channel store
    /// contracts, using TryAdd so earlier registrations win. Schema creation is explicit: call <see cref="PostgresChannelStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresChannelStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(_ => new PostgresChannelStore(dataSource));
        services.TryAddSingleton<IChannelEndpointStore>(provider => provider.GetRequiredService<PostgresChannelStore>());
        services.TryAddSingleton<IChannelConversationStateStore>(provider => provider.GetRequiredService<PostgresChannelStore>());
        services.TryAddSingleton<IChannelThreadStore>(provider => provider.GetRequiredService<PostgresChannelStore>());
        services.TryAddSingleton<IChannelDispatchStore>(provider => provider.GetRequiredService<PostgresChannelStore>());
        return builder;
    }
}
