using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TicketFlow.Channels.Mailbox;

public static class MailboxTicketFlowBuilderExtensions
{
    /// <summary>Registers IMAP mailbox polling services (<see cref="MailboxPoller"/>, <see cref="MailboxMimeNormalizer"/>, default MailKit factory and in-memory development checkpoint/attachment stores).</summary>
    public static TicketFlowBuilder AddMailboxChannel(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IMailboxClientFactory, MailKitMailboxClientFactory>();
        builder.Services.TryAddSingleton<IMailboxCheckpointStore, InMemoryMailboxCheckpointStore>();
        builder.Services.TryAddSingleton<IMailboxAttachmentStore, InMemoryMailboxAttachmentStore>();
        builder.Services.TryAddSingleton<MailboxMimeNormalizer>();
        builder.Services.TryAddScoped<MailboxPoller>();
        return builder;
    }
}
