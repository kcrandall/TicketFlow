using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TicketFlow.Automation;
using TicketFlow.Core;
using TicketFlow.Events;
using TicketFlow.Reporting;
using TicketFlow.Routing;
using TicketFlow.Sla;

namespace TicketFlow.Hosting.Worker;

/// <summary>
/// The API clients TicketFlow's own components act as (SLA tracker, automation, routing engine), collected from the registered feature
/// options. Hosts reserve them so no externally authenticated caller can be mapped onto a worker identity (for example
/// <c>IdentityFlowPrincipalOptions.ReservedApiClientIds</c>).
/// </summary>
public static class TicketFlowServiceIdentities
{
    public static IReadOnlySet<ApiClientId> Collect(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = services.GetRequiredService<IServiceProviderIsService>();
        var ids = new HashSet<ApiClientId>();
        if (registered.IsService(typeof(SlaTimerScheduler)))
            ids.Add(services.GetRequiredService<IOptions<SlaOptions>>().Value.WorkerClientId);
        if (registered.IsService(typeof(AutomationScheduler)))
        {
            var automation = services.GetRequiredService<IOptions<AutomationOptions>>().Value;
            ids.Add(automation.ClientId);
            ids.Add(automation.SlaWorkerClientId);
        }
        if (registered.IsService(typeof(RoutingMaintenanceWorker)))
            ids.Add(services.GetRequiredService<IOptions<RoutingOptions>>().Value.EngineClient);
        if (registered.IsService(typeof(ReportingRetentionWorker)))
            ids.Add(services.GetRequiredService<IOptions<ReportingOptions>>().Value.SlaWorkerClientId);
        if (registered.IsService(typeof(EventDeliveryWorker)))
            ids.Add(services.GetRequiredService<IOptions<EventsOptions>>().Value.SlaWorkerClientId);
        return ids;
    }

    /// <summary>
    /// Throws when the SLA worker identity differs between the SLA tracker (<c>SlaOptions.WorkerClientId</c>, the actor of <c>sla.*</c>
    /// events) and the consumers that trust it (<c>AutomationOptions</c>, <c>ReportingOptions</c>, <c>EventsOptions</c>
    /// <c>SlaWorkerClientId</c>): a mismatch silently drops or misattributes SLA events. Only registered features are compared.
    /// </summary>
    public static void ValidateSlaWorker(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = services.GetRequiredService<IServiceProviderIsService>();
        if (!registered.IsService(typeof(SlaTimerScheduler)))
            return;
        var expected = services.GetRequiredService<IOptions<SlaOptions>>().Value.WorkerClientId;
        var mismatches = new List<string>();
        if (registered.IsService(typeof(AutomationScheduler)) && services.GetRequiredService<IOptions<AutomationOptions>>().Value.SlaWorkerClientId != expected)
            mismatches.Add("AutomationOptions.SlaWorkerClientId");
        if (registered.IsService(typeof(ReportingRetentionWorker)) && services.GetRequiredService<IOptions<ReportingOptions>>().Value.SlaWorkerClientId != expected)
            mismatches.Add("ReportingOptions.SlaWorkerClientId");
        if (registered.IsService(typeof(EventDeliveryWorker)) && services.GetRequiredService<IOptions<EventsOptions>>().Value.SlaWorkerClientId != expected)
            mismatches.Add("EventsOptions.SlaWorkerClientId");
        if (mismatches.Count > 0)
            throw new InvalidOperationException($"{string.Join(", ", mismatches)} must equal SlaOptions.WorkerClientId.");
    }
}

/// <summary>Validates service identities when the host starts (registered by <c>AddTicketFlowWorkers()</c>).</summary>
internal sealed class TicketFlowServiceIdentityValidator(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        TicketFlowServiceIdentities.ValidateSlaWorker(services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
