using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.HelpCenter;

namespace TicketFlow;

public static class HelpCenterTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the help center: its permissions (<see cref="HelpCenterPermissions.Definitions"/>), the singleton
    /// <see cref="SafeMarkdownRenderer"/> as <see cref="IArticleRenderer"/>, scoped <see cref="IHelpCenterService"/>, and scoped
    /// <see cref="HelpCenterPublicReader"/> as <see cref="IHelpCenterPublicReader"/> and <see cref="IHelpCenterSearch"/>. Requires
    /// <c>AddAccessControl()</c> plus access stores and selects no help center store: call
    /// <see cref="AddInMemoryHelpCenterStores"/> for development or register durable stores. Uses TryAdd; repeated calls are safe.
    /// </summary>
    public static TicketFlowBuilder AddHelpCenter(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(HelpCenterPermissionsMarker)))
        {
            services.AddSingleton<HelpCenterPermissionsMarker>();
            services.AddAccessPermissions(HelpCenterPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IArticleRenderer, SafeMarkdownRenderer>();
        services.TryAddScoped<IHelpCenterService, HelpCenterService>();
        services.TryAddScoped<HelpCenterPublicReader>();
        services.TryAddScoped<IHelpCenterPublicReader>(provider => provider.GetRequiredService<HelpCenterPublicReader>());
        services.TryAddScoped<IHelpCenterSearch>(provider => provider.GetRequiredService<HelpCenterPublicReader>());
        return builder;
    }

    /// <summary>
    /// Registers the development-only <see cref="InMemoryHelpCenterStore"/> (process-local, bounded, not durable, naive search) as
    /// <see cref="IHelpCenterStore"/> and <see cref="IArticleFeedbackStore"/>.
    /// </summary>
    public static TicketFlowBuilder AddInMemoryHelpCenterStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<InMemoryHelpCenterStore>();
        services.TryAddSingleton<IHelpCenterStore>(provider => provider.GetRequiredService<InMemoryHelpCenterStore>());
        services.TryAddSingleton<IArticleFeedbackStore>(provider => provider.GetRequiredService<InMemoryHelpCenterStore>());
        return builder;
    }

    private sealed class HelpCenterPermissionsMarker;
}
