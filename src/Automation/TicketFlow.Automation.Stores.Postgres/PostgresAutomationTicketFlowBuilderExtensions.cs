using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Automation.Stores.Postgres;

public static class PostgresAutomationTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAutomationStore"/> over the application-owned <paramref name="dataSource"/> as every automation store
    /// contract, using TryAdd so earlier application registrations win. Schema creation is explicit: call
    /// <see cref="PostgresAutomationStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresAutomationStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(provider => new PostgresAutomationStore(dataSource, provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IAutomationRuleStore>(provider => provider.GetRequiredService<PostgresAutomationStore>());
        services.TryAddSingleton<IAutomationExecutionStore>(provider => provider.GetRequiredService<PostgresAutomationStore>());
        services.TryAddSingleton<IMacroStore>(provider => provider.GetRequiredService<PostgresAutomationStore>());
        services.TryAddSingleton<ICannedResponseStore>(provider => provider.GetRequiredService<PostgresAutomationStore>());
        return builder;
    }
}
