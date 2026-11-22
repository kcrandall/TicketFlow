using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Sla.Stores.Postgres;

public static class PostgresSlaTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresSlaStore"/> over the application-owned <paramref name="dataSource"/> as <see cref="ISlaPolicyStore"/>
    /// and <see cref="ISlaTrackerStore"/>, using TryAdd so earlier application registrations win. Schema creation is explicit: call
    /// <see cref="PostgresSlaStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresSlaStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(provider => new PostgresSlaStore(dataSource, provider.GetService<TimeProvider>()));
        services.TryAddSingleton<ISlaPolicyStore>(provider => provider.GetRequiredService<PostgresSlaStore>());
        services.TryAddSingleton<ISlaTrackerStore>(provider => provider.GetRequiredService<PostgresSlaStore>());
        return builder;
    }
}
