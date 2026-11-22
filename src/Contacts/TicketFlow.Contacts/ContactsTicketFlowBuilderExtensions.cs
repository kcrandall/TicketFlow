using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;

namespace TicketFlow.Contacts;

public static class ContactsTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the contact feature: its permissions (<see cref="ContactPermissions.Definitions"/>), options and scoped
    /// <see cref="IContactService"/> and <see cref="IContactAttributeDefinitionService"/>. Requires the access engine
    /// (<c>AddAccessControl()</c>) and a store; uses TryAdd so application registrations win. Register
    /// <see cref="IContactChangeObserver"/> implementations to follow merges and other changes in process.
    /// </summary>
    public static TicketFlowBuilder AddContacts(this TicketFlowBuilder builder, Action<ContactsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ContactsOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ContactPermissionsMarker)))
        {
            services.AddSingleton<ContactPermissionsMarker>();
            services.AddAccessPermissions(ContactPermissions.Definitions);
        }
        services.TryAddScoped<IContactService, ContactService>();
        services.TryAddScoped<IContactAttributeDefinitionService, ContactAttributeDefinitionService>();
        return builder;
    }

    /// <summary>
    /// Registers the development-only in-memory contact stores (process-local, bounded, not durable) as <see cref="IContactStore"/>,
    /// <see cref="IContactAuditSink"/> and <see cref="IContactAttributeDefinitionStore"/>.
    /// </summary>
    public static TicketFlowBuilder AddInMemoryContactStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<InMemoryContactStore>();
        services.TryAddSingleton<IContactStore>(provider => provider.GetRequiredService<InMemoryContactStore>());
        services.TryAddSingleton<IContactAuditSink>(provider => provider.GetRequiredService<InMemoryContactStore>());
        services.TryAddSingleton<IContactAttributeDefinitionStore, InMemoryContactAttributeDefinitionStore>();
        return builder;
    }

    private sealed class ContactPermissionsMarker;
}
