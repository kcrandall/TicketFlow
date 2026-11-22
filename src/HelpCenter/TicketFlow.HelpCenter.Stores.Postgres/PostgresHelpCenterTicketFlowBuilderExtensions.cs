using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.HelpCenter.Stores.Postgres;

public static class PostgresHelpCenterTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresHelpCenterStore"/> over the application-owned <paramref name="dataSource"/> as
    /// <see cref="IHelpCenterStore"/> and <see cref="IArticleFeedbackStore"/>, using TryAdd so earlier application registrations win.
    /// Schema creation is explicit: call <see cref="PostgresHelpCenterStore.InitializeAsync"/> during deployment or startup.
    /// </summary>
    public static TicketFlowBuilder AddPostgresHelpCenterStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(new PostgresHelpCenterStore(dataSource));
        services.TryAddSingleton<IHelpCenterStore>(provider => provider.GetRequiredService<PostgresHelpCenterStore>());
        services.TryAddSingleton<IArticleFeedbackStore>(provider => provider.GetRequiredService<PostgresHelpCenterStore>());
        return builder;
    }
}
