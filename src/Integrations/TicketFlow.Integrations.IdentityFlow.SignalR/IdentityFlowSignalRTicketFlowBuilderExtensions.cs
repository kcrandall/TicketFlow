using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Realtime.SignalR;

namespace TicketFlow.Integrations.IdentityFlow.SignalR;

public static class IdentityFlowSignalRTicketFlowBuilderExtensions
{
    /// <summary>
    /// Calls <c>AddIdentityFlowPrincipals()</c> and registers <see cref="IdentityFlowRealtimePrincipalResolver"/> (scoped) as
    /// <see cref="IRealtimePrincipalResolver"/>, replacing only the <see cref="OptionsRealtimePrincipalResolver"/> fallback of
    /// <c>AddTicketFlowSignalR()</c> (application-registered resolvers win). Agent connections pass <c>?workspace={workspaceId}</c>.
    /// </summary>
    public static TicketFlowBuilder AddIdentityFlowRealtimePrincipals(this TicketFlowBuilder builder, Action<IdentityFlowPrincipalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddIdentityFlowPrincipals(configure);
        var services = builder.Services;
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(IRealtimePrincipalResolver) && item.ImplementationType == typeof(OptionsRealtimePrincipalResolver)).ToList())
            services.Remove(descriptor);
        services.TryAddScoped<IRealtimePrincipalResolver, IdentityFlowRealtimePrincipalResolver>();
        return builder;
    }

    /// <summary>
    /// Registers <see cref="IdentityFlowVisitorTokenValidator"/> (singleton) as the live-chat <see cref="IVisitorTokenValidator"/> (TryAdd),
    /// so widgets can identify visitors with IdentityFlow-issued tokens (<c>identify("token", jwt)</c>).
    /// </summary>
    public static TicketFlowBuilder AddIdentityFlowVisitorTokens(this TicketFlowBuilder builder, Action<IdentityFlowVisitorTokenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<IdentityFlowVisitorTokenOptions>().Configure(configure);
        builder.Services.TryAddSingleton<IVisitorTokenValidator, IdentityFlowVisitorTokenValidator>();
        return builder;
    }
}
