using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Identity;
using TicketFlow.Workspaces;

namespace TicketFlow.Integrations.IdentityFlow;

public static class IdentityFlowTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="IdentityFlowPrincipalResolver"/> (scoped) as <see cref="ITicketFlowPrincipalResolver"/>, replacing only the
    /// <see cref="OptionsTicketFlowPrincipalResolver"/> fallback of <c>AddTicketFlowEndpoints()</c> (application-registered resolvers win).
    /// Uses a registered <see cref="IStaffIdentityResolver"/> (for example <c>AddStaffIdentities()</c>) when present. The host still
    /// configures IdentityFlow authentication and supplies an <see cref="ITicketFlowPartitionResolver"/> (or endpoint options).
    /// SignalR agent connections: <c>AddIdentityFlowRealtimePrincipals()</c> in TicketFlow.Integrations.IdentityFlow.SignalR.
    /// </summary>
    public static TicketFlowBuilder AddIdentityFlowPrincipals(this TicketFlowBuilder builder, Action<IdentityFlowPrincipalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<IdentityFlowPrincipalOptions>();
        if (configure is not null)
            services.Configure(configure);
        foreach (var descriptor in services.Where(item => item.ServiceType == typeof(ITicketFlowPrincipalResolver) && item.ImplementationType == typeof(OptionsTicketFlowPrincipalResolver)).ToList())
            services.Remove(descriptor);
        services.TryAddScoped(provider => new IdentityFlowPrincipalResolver(
            provider.GetRequiredService<ITeamMembershipResolver>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityFlowPrincipalOptions>>(),
            provider.GetService<IStaffIdentityResolver>()));
        services.TryAddScoped<ITicketFlowPrincipalResolver>(provider => provider.GetRequiredService<IdentityFlowPrincipalResolver>());
        return builder;
    }
}
