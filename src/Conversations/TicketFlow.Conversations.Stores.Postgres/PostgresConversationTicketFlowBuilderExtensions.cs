using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Conversations.Stores.Postgres;

public static class PostgresConversationTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresConversationStore"/> over the application-owned <paramref name="dataSource"/> as
    /// <see cref="IConversationStore"/>, <see cref="IConversationOutbox"/> and <see cref="IConversationAttributeDefinitionStore"/>,
    /// using TryAdd so earlier application registrations win. Schema creation is explicit: call
    /// <see cref="PostgresConversationStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresConversationStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(provider => new PostgresConversationStore(dataSource, provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IConversationStore>(provider => provider.GetRequiredService<PostgresConversationStore>());
        services.TryAddSingleton<IConversationOutbox>(provider => provider.GetRequiredService<PostgresConversationStore>());
        services.TryAddSingleton<IConversationAttributeDefinitionStore>(provider => provider.GetRequiredService<PostgresConversationStore>());
        return builder;
    }
}
