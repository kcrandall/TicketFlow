using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace TicketFlow.Hosting.Worker;

public static class TicketFlowWorkerExtensions
{
    /// <summary>
    /// Registers a hosted service per built-in loop (<see cref="TicketFlowLoops"/>), <see cref="TicketFlowWorkerOptions"/>,
    /// <see cref="TicketFlowWorkerHealth"/> and the development <see cref="InMemoryWorkerCheckpointStore"/> (TryAdd: register a durable
    /// <see cref="IWorkerCheckpointStore"/>, for example <c>AddPostgresWorkerCheckpoints()</c>, for production). Loops whose feature is not
    /// registered disable themselves. Add the health check with <c>services.AddHealthChecks().AddTicketFlowWorkers()</c>. Calling this more
    /// than once is safe.
    /// </summary>
    public static TicketFlowBuilder AddTicketFlowWorkers(this TicketFlowBuilder builder, Action<TicketFlowWorkerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<TicketFlowWorkerOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(WorkersMarker)))
            return builder;
        services.AddSingleton<WorkersMarker>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TicketFlowWorkerHealth>();
        services.TryAddSingleton<OutboxRegistration>();
        services.TryAddSingleton<IWorkerCheckpointStore, InMemoryWorkerCheckpointStore>();
        foreach (var definition in TicketFlowBuiltInLoops.All())
            AddLoop(services, definition);
        return builder;
    }

    /// <summary>
    /// Hosts an additional loop (for example <c>ReportingRetentionWorker.RunOnceAsync</c>) with the same scheduling, backoff, health and
    /// telemetry as the built-in loops. Its options live in <see cref="TicketFlowWorkerOptions.Custom"/> under <paramref name="name"/>.
    /// </summary>
    public static TicketFlowBuilder AddTicketFlowWorkerLoop<TService>(this TicketFlowBuilder builder, string name,
        Func<TService, TicketFlowLoopOptions, CancellationToken, Task<int>> run) where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(run);
        WorkerCheckpointNames.Require(name, nameof(name));
        builder.AddTicketFlowWorkers();
        AddLoop(builder.Services, new TicketFlowLoopDefinition(name, [typeof(TService)], async (services, options, cancellationToken) =>
            new TicketFlowLoopRun(await run(services.GetRequiredService<TService>(), options, cancellationToken).ConfigureAwait(false))));
        return builder;
    }

    /// <summary>Adds <see cref="TicketFlowWorkerHealthCheck"/> (tags <c>ticketflow</c>, <c>workers</c>).</summary>
    public static IHealthChecksBuilder AddTicketFlowWorkers(this IHealthChecksBuilder builder, string name = "ticketflow-workers", HealthStatus? failureStatus = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<TicketFlowWorkerHealthCheck>(name, failureStatus, ["ticketflow", "workers"]);
    }

    private static void AddLoop(IServiceCollection services, TicketFlowLoopDefinition definition) =>
        services.AddSingleton<IHostedService>(provider =>
        {
            provider.GetRequiredService<TicketFlowWorkerHealth>().Register(definition.Name);
            return ActivatorUtilities.CreateInstance<TicketFlowLoopService>(provider, definition);
        });

    private sealed class WorkersMarker;
}
