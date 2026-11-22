using Microsoft.Extensions.DependencyInjection;

namespace TicketFlow;

/// <summary>
/// Fluent entry point that feature packages extend with their own registration methods. It lives in Core so feature packages
/// depend only on Core and their abstractions, never on the <c>TicketFlow</c> meta-package; <c>AddTicketFlow()</c> creates it.
/// </summary>
public sealed class TicketFlowBuilder
{
    public TicketFlowBuilder(IServiceCollection services) => Services = services ?? throw new ArgumentNullException(nameof(services));

    public IServiceCollection Services { get; }
}
