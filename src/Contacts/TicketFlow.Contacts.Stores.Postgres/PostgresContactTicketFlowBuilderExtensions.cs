using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Contacts.Stores.Postgres;

public static class PostgresContactTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresContactStore"/> over the application-owned <paramref name="dataSource"/> as
    /// <see cref="IContactStore"/>, <see cref="IContactAttributeDefinitionStore"/>, <see cref="IContactAuditSink"/> and <see cref="IContactLinkingStore"/>, using TryAdd so
    /// earlier application registrations win. Schema creation is explicit: call <see cref="PostgresContactStore.InitializeAsync"/>.
    /// </summary>
    public static TicketFlowBuilder AddPostgresContactStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        var services = builder.Services;
        services.TryAddSingleton(new PostgresContactStore(dataSource));
        services.TryAddSingleton<IContactStore>(provider => provider.GetRequiredService<PostgresContactStore>());
        services.TryAddSingleton<IContactAttributeDefinitionStore>(provider => provider.GetRequiredService<PostgresContactStore>());
        services.TryAddSingleton<IContactAuditSink>(provider => provider.GetRequiredService<PostgresContactStore>());
        services.TryAddSingleton<IContactLinkingStore>(provider => provider.GetRequiredService<PostgresContactStore>());
        return builder;
    }
}
