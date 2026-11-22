using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Assist.Stores.Postgres;

public static class PostgresAssistTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAssistStore"/> over the application-owned <paramref name="dataSource"/> as <see cref="IAssistStore"/>
    /// using TryAdd. Schema creation is explicit: call <see cref="PostgresAssistStore.InitializeAsync"/> during deployment or startup.
    /// </summary>
    public static TicketFlowBuilder AddPostgresAssistStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.Services.TryAddSingleton(new PostgresAssistStore(dataSource));
        builder.Services.TryAddSingleton<IAssistStore>(provider => provider.GetRequiredService<PostgresAssistStore>());
        return builder;
    }
}
