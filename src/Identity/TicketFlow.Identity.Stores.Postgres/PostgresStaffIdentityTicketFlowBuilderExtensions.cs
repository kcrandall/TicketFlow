using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Identity.Stores.Postgres;

public static class PostgresStaffIdentityTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresStaffIdentityStore"/> over the application-owned <paramref name="dataSource"/> as
    /// <see cref="IStaffIdentityStore"/> (TryAdd). Schema creation is explicit: call <see cref="PostgresStaffIdentityStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresStaffIdentityStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.Services.TryAddSingleton(new PostgresStaffIdentityStore(dataSource));
        builder.Services.TryAddSingleton<IStaffIdentityStore>(provider => provider.GetRequiredService<PostgresStaffIdentityStore>());
        return builder;
    }
}
