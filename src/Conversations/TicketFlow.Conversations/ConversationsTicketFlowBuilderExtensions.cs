using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Contacts;

namespace TicketFlow.Conversations;

public static class ConversationsTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the conversation feature: its permissions (<see cref="ConversationPermissions.Definitions"/>), options, scoped
    /// <see cref="IConversationService"/> and <see cref="IConversationAttributeDefinitionService"/>, the
    /// <see cref="ConversationContactMergeFollower"/> (also as an <see cref="IContactChangeObserver"/>, so in-process contact merges
    /// re-point conversations) and the <see cref="ConversationOutboxDispatcher"/> over registered
    /// <see cref="IConversationOutboxHandler"/>s. Requires the access engine, workspace and contact stores and a conversation store;
    /// uses TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddConversations(this TicketFlowBuilder builder, Action<ConversationsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ConversationsOptions>();
        services.AddOptions<ConversationOutboxDispatcherOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ConversationPermissionsMarker)))
        {
            services.AddSingleton<ConversationPermissionsMarker>();
            services.AddAccessPermissions(ConversationPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IConversationService, ConversationService>();
        services.TryAddScoped<IConversationAttributeDefinitionService, ConversationAttributeDefinitionService>();
        services.TryAddScoped<ConversationContactMergeFollower>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IContactChangeObserver, ConversationContactMergeFollowerObserver>());
        services.TryAddScoped<ConversationOutboxDispatcher>();
        return builder;
    }

    /// <summary>
    /// Registers the development-only in-memory stores (process-local, bounded, not durable) as <see cref="IConversationStore"/>,
    /// <see cref="IConversationOutbox"/> and <see cref="IConversationAttributeDefinitionStore"/>.
    /// </summary>
    public static TicketFlowBuilder AddInMemoryConversationStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton(provider => new InMemoryConversationStore(provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IConversationStore>(provider => provider.GetRequiredService<InMemoryConversationStore>());
        services.TryAddSingleton<IConversationOutbox>(provider => provider.GetRequiredService<InMemoryConversationStore>());
        services.TryAddSingleton<IConversationAttributeDefinitionStore, InMemoryConversationAttributeDefinitionStore>();
        return builder;
    }

    private sealed class ConversationPermissionsMarker;

    /// <summary>Forwards contact changes to the registered follower (a distinct type so TryAddEnumerable deduplicates).</summary>
    private sealed class ConversationContactMergeFollowerObserver(ConversationContactMergeFollower follower) : IContactChangeObserver
    {
        public Task OnChangedAsync(IReadOnlyList<ContactChange> changes, CancellationToken cancellationToken = default) =>
            follower.OnChangedAsync(changes, cancellationToken);
    }
}
