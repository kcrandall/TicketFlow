using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TicketFlow;

public static class TicketFlowServiceCollectionExtensions
{
    /// <summary>
    /// Register provider-neutral TicketFlow defaults. Application registrations are preserved; the host explicitly chooses
    /// stores, channels and hosting packages. Calling this more than once is safe.
    /// </summary>
    public static TicketFlowBuilder AddTicketFlow(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        return new TicketFlowBuilder(services);
    }
}
