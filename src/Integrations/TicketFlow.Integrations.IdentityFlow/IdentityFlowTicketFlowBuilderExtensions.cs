using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Realtime.SignalR;

namespace TicketFlow.Integrations.IdentityFlow;

public static class IdentityFlowTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="IdentityFlowPrincipalResolver"/> (scoped) as <see cref="ITicketFlowPrincipalResolver"/> and
    /// <see cref="IRealtimePrincipalResolver"/>, replacing only the options-based fallbacks of <c>AddTicketFlowEndpoints()</c> and
    /// <c>AddTicketFlowSignalR()</c> (application-registered resolvers win). The host still configures IdentityFlow authentication
    /// (cookies, JWT bearer, API keys, PATs) and supplies an <see cref="ITicketFlowPartitionResolver"/> (or endpoint options).
    /// </summary>
    public static TicketFlowBuilder AddIdentityFlowPrincipals(this TicketFlowBuilder builder, Action<IdentityFlowPrincipalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<IdentityFlowPrincipalOptions>();
        if (configure is not null)
            services.Configure(configure);
        RemoveFallback(services, typeof(ITicketFlowPrincipalResolver), "TicketFlow.Hosting.AspNetCore.OptionsPrincipalResolver");
        RemoveFallback(services, typeof(IRealtimePrincipalResolver), "TicketFlow.Realtime.SignalR.OptionsPrincipalResolver");
        services.TryAddScoped<IdentityFlowPrincipalResolver>();
        services.TryAddScoped<ITicketFlowPrincipalResolver>(provider => provider.GetRequiredService<IdentityFlowPrincipalResolver>());
        services.TryAddScoped<IRealtimePrincipalResolver>(provider => provider.GetRequiredService<IdentityFlowPrincipalResolver>());
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

    private static void RemoveFallback(IServiceCollection services, Type service, string implementation)
    {
        foreach (var descriptor in services.Where(item => item.ServiceType == service && item.ImplementationType?.FullName == implementation).ToList())
            services.Remove(descriptor);
    }
}
