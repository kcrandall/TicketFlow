using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Events.Stores.Postgres;

public static class PostgresEventsTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresEventStore"/> over the application-owned <paramref name="dataSource"/> as <see cref="IEventLog"/> and
    /// <see cref="IEventSubscriptionStore"/> using TryAdd (lease checks use the registered <see cref="TimeProvider"/>). Schema creation is explicit:
    /// call <see cref="PostgresEventStore.InitializeAsync"/> during deployment or startup.
    /// </summary>
    public static TicketFlowBuilder AddPostgresEventStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.Services.TryAddSingleton(provider => new PostgresEventStore(dataSource, provider.GetService<TimeProvider>()));
        builder.Services.TryAddSingleton<IEventLog>(provider => provider.GetRequiredService<PostgresEventStore>());
        builder.Services.TryAddSingleton<IEventSubscriptionStore>(provider => provider.GetRequiredService<PostgresEventStore>());
        return builder;
    }
}
