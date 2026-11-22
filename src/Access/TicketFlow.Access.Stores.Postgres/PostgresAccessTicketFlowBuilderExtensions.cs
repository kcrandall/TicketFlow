using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Access.Stores.Postgres;

public static class PostgresAccessTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresAccessStore"/> over the application-owned <paramref name="dataSource"/> as every access store
    /// contract, using TryAdd so earlier application registrations win. Schema creation is explicit: call
    /// <see cref="PostgresAccessStore.InitializeAsync"/> during deployment or startup.
    /// </summary>
    public static TicketFlowBuilder AddPostgresAccessStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(new PostgresAccessStore(dataSource));
        services.TryAddSingleton<IAccessRoleStore>(provider => provider.GetRequiredService<PostgresAccessStore>());
        services.TryAddSingleton<IAccessGrantStore>(provider => provider.GetRequiredService<PostgresAccessStore>());
        services.TryAddSingleton<IAccessFieldPolicyStore>(provider => provider.GetRequiredService<PostgresAccessStore>());
        services.TryAddSingleton<IAccessAuditSink>(provider => provider.GetRequiredService<PostgresAccessStore>());
        return builder;
    }
}
