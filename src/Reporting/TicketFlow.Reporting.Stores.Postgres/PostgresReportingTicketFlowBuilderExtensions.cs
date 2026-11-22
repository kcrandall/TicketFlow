using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Reporting.Stores.Postgres;

public static class PostgresReportingTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresReportingStore"/> over the application-owned <paramref name="dataSource"/> as <see cref="IReportingStore"/>
    /// using TryAdd. Schema creation is explicit: call <see cref="PostgresReportingStore.InitializeAsync"/> during deployment or startup.
    /// </summary>
    public static TicketFlowBuilder AddPostgresReportingStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.Services.TryAddSingleton(new PostgresReportingStore(dataSource));
        builder.Services.TryAddSingleton<IReportingStore>(provider => provider.GetRequiredService<PostgresReportingStore>());
        return builder;
    }
}
