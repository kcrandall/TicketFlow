using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Assist;

namespace TicketFlow;

public static class AssistTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the assistant: permissions (<see cref="AssistPermissions.Definitions"/>), <see cref="AssistOptions"/>, the singleton
    /// <see cref="IAssistModelRegistry"/> and <see cref="IAssistChatClientFactory"/>, and scoped <see cref="AssistContextBuilder"/>,
    /// <see cref="IAssistService"/> and <see cref="IAssistAdministration"/>. Requires access control, conversations, contacts, the help
    /// center, an <see cref="IAssistStore"/> (<see cref="AddInMemoryAssistStores"/> for development) and at least one
    /// <see cref="IAssistChatProvider"/> (for example <c>AddClaudeAssist()</c>). Uses TryAdd; repeated calls are safe.
    /// </summary>
    public static TicketFlowBuilder AddAssist(this TicketFlowBuilder builder, Action<AssistOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<AssistOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(AssistPermissionsMarker)))
        {
            services.AddSingleton<AssistPermissionsMarker>();
            services.AddAccessPermissions(AssistPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAssistModelRegistry, AssistModelRegistry>();
        services.TryAddSingleton<IAssistChatClientFactory, AssistChatClientFactory>();
        services.TryAddScoped<AssistContextBuilder>();
        services.TryAddScoped<IAssistService, AssistService>();
        services.TryAddScoped<IAssistAdministration, AssistAdministration>();
        return builder;
    }

    /// <summary>Registers the development-only <see cref="InMemoryAssistStore"/> (process-local, bounded, not durable) as <see cref="IAssistStore"/>.</summary>
    public static TicketFlowBuilder AddInMemoryAssistStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IAssistStore, InMemoryAssistStore>();
        return builder;
    }

    private sealed class AssistPermissionsMarker;
}
