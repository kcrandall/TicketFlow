using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TicketFlow.Workspaces;
using TicketFlow.Workspaces.Stores.Postgres;

namespace TicketFlow;

public static class PostgresWorkspaceTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresWorkspaceStore"/> over the application-owned <paramref name="dataSource"/> as every workspace store
    /// contract, using TryAdd so earlier application registrations win. Schema creation is explicit: call
    /// <see cref="PostgresWorkspaceStore.InitializeAsync"/> during deployment or startup.
    /// </summary>
    public static TicketFlowBuilder AddPostgresWorkspaceStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(new PostgresWorkspaceStore(dataSource));
        services.TryAddSingleton<IWorkspaceSettingsStore>(provider => provider.GetRequiredService<PostgresWorkspaceStore>());
        services.TryAddSingleton<IInboxStore>(provider => provider.GetRequiredService<PostgresWorkspaceStore>());
        services.TryAddSingleton<ITeamStore>(provider => provider.GetRequiredService<PostgresWorkspaceStore>());
        services.TryAddSingleton<IBusinessCalendarStore>(provider => provider.GetRequiredService<PostgresWorkspaceStore>());
        return builder;
    }
}
