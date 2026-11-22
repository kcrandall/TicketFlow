using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Conversations;

namespace TicketFlow.Automation;

public static class AutomationTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the automation feature: <see cref="AutomationPermissions.Definitions"/>, <see cref="AutomationOptions"/>, scoped
    /// <see cref="IAutomationRuleService"/>, <see cref="IMacroService"/>, <see cref="ICannedResponseService"/>, the engine
    /// (<see cref="AutomationContextBuilder"/>, <see cref="AutomationActionExecutor"/>, <see cref="AutomationRunner"/>),
    /// <see cref="AutomationOutboxHandler"/> (also as an <see cref="IConversationOutboxHandler"/>), <see cref="AutomationScheduler"/>,
    /// <see cref="AutomationContactFollower"/> and the default <see cref="SignedAutomationWebhookSender"/> (it needs an application
    /// <see cref="IAutomationWebhookDestinationResolver"/> to deliver anything). Requires the access engine, workspace, contact and
    /// conversation registrations and automation stores; uses TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddAutomation(this TicketFlowBuilder builder, Action<AutomationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<AutomationOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(AutomationPermissionsMarker)))
        {
            services.AddSingleton<AutomationPermissionsMarker>();
            services.AddAccessPermissions(AutomationPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAutomationWebhookSender, SignedAutomationWebhookSender>();
        services.TryAddSingleton<AutomationRuleCache>();
        services.TryAddScoped<AutomationContextBuilder>();
        services.TryAddScoped<AutomationActionExecutor>();
        services.TryAddScoped<AutomationRunner>();
        services.TryAddScoped<IAutomationRuleService, AutomationRuleService>();
        services.TryAddScoped<IMacroService, MacroService>();
        services.TryAddScoped<ICannedResponseService, CannedResponseService>();
        services.TryAddScoped<AutomationOutboxHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, AutomationOutboxHandler>());
        services.TryAddScoped<AutomationScheduler>();
        services.TryAddScoped<AutomationContactFollower>();
        return builder;
    }

    /// <summary>Registers the development-only in-memory stores (process-local, not durable).</summary>
    public static TicketFlowBuilder AddInMemoryAutomationStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<IAutomationRuleStore>(provider => new InMemoryAutomationRuleStore(provider.GetService<TimeProvider>()));
        services.TryAddSingleton<IAutomationExecutionStore, InMemoryAutomationExecutionStore>();
        services.TryAddSingleton<IMacroStore, InMemoryMacroStore>();
        services.TryAddSingleton<ICannedResponseStore, InMemoryCannedResponseStore>();
        return builder;
    }

    private sealed class AutomationPermissionsMarker;
}
