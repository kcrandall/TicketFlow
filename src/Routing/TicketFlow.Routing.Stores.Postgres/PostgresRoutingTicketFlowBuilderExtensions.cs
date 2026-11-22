using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Routing.Stores.Postgres;

public static class PostgresRoutingTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresRoutingStore"/> over the application-owned <paramref name="dataSource"/> as every routing store
    /// contract, using TryAdd so earlier application registrations win. Schema creation is explicit: call
    /// <see cref="PostgresRoutingStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresRoutingStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(provider => new PostgresRoutingStore(dataSource, provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IRoutingPolicyStore>(provider => provider.GetRequiredService<PostgresRoutingStore>());
        services.TryAddSingleton<IAgentAvailabilityStore>(provider => provider.GetRequiredService<PostgresRoutingStore>());
        services.TryAddSingleton<IRoutingAssignmentStore>(provider => provider.GetRequiredService<PostgresRoutingStore>());
        services.TryAddSingleton<IRoutingQueueStore>(provider => provider.GetRequiredService<PostgresRoutingStore>());
        services.TryAddSingleton<IConversationPresenceStore>(provider => provider.GetRequiredService<PostgresRoutingStore>());
        return builder;
    }
}
