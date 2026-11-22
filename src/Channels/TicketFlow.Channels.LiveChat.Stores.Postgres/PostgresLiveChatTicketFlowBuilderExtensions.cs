using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Channels.LiveChat.Stores.Postgres;

public static class PostgresLiveChatTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresLiveChatStore"/> over the application-owned <paramref name="dataSource"/> for both live-chat store
    /// contracts, using TryAdd so earlier registrations win. Schema creation is explicit: call <see cref="PostgresLiveChatStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresLiveChatStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(_ => new PostgresLiveChatStore(dataSource));
        services.TryAddSingleton<ILiveChatWidgetStore>(provider => provider.GetRequiredService<PostgresLiveChatStore>());
        services.TryAddSingleton<ILiveChatSessionStore>(provider => provider.GetRequiredService<PostgresLiveChatStore>());
        return builder;
    }
}
