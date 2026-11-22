using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TicketFlow.Channels.Mailbox.Stores.Postgres;

public static class PostgresMailboxTicketFlowBuilderExtensions
{
    /// <summary>Replaces <see cref="IMailboxCheckpointStore"/> with <see cref="PostgresMailboxCheckpointStore"/>.</summary>
    public static TicketFlowBuilder UsePostgresMailboxCheckpointStore(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<PostgresMailboxCheckpointStore>();
        builder.Services.Replace(ServiceDescriptor.Singleton<IMailboxCheckpointStore>(provider => provider.GetRequiredService<PostgresMailboxCheckpointStore>()));
        return builder;
    }
}
