using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TicketFlow.Hosting.Worker;

/// <summary>Outcome of one loop run: items processed and per-item (for example per-workspace) failures.</summary>
public readonly record struct TicketFlowLoopRun(int Items, int Failures = 0)
{
    public static TicketFlowLoopRun Idle => default;
}

/// <summary>
/// A hosted loop. <see cref="Run"/> is invoked in a fresh DI scope per run; every type in <see cref="RequiredServices"/> must be registered or
/// the loop disables itself (reported as <see cref="TicketFlowLoopStatus.NotConfigured"/>).
/// </summary>
public sealed record TicketFlowLoopDefinition(string Name, IReadOnlyList<Type> RequiredServices, Func<IServiceProvider, TicketFlowLoopOptions, CancellationToken, Task<TicketFlowLoopRun>> Run);

/// <summary>
/// Runs one <see cref="TicketFlowLoopDefinition"/>: a run per interval (immediately again, up to <see cref="TicketFlowLoopOptions.MaximumDrainRuns"/>
/// times, while runs report work), exponential backoff with jitter after failures, cancellation on host shutdown. Exceptions are logged by
/// type only (messages may carry customer data); telemetry carries loop names and outcomes only.
/// </summary>
internal sealed class TicketFlowLoopService(TicketFlowLoopDefinition definition, IServiceScopeFactory scopes, IServiceProviderIsService services, IOptions<TicketFlowWorkerOptions> options,
    TicketFlowWorkerHealth health, TimeProvider time, ILogger<TicketFlowLoopService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        settings.Validate();
        var loop = settings.For(definition.Name);
        loop.Validate(definition.Name);
        if (!loop.Enabled)
        {
            health.Set(definition.Name, TicketFlowLoopStatus.Disabled);
            return;
        }
        if (!definition.RequiredServices.All(services.IsService))
        {
            health.Set(definition.Name, TicketFlowLoopStatus.NotConfigured);
            logger.LogInformation("TicketFlow worker loop {Loop} is not configured (its service is not registered) and will not run.", definition.Name);
            return;
        }
        health.Started(definition.Name, loop.Interval);
        // Yield so host startup is not blocked by the first run.
        await Task.Yield();
        var failures = 0;
        var drained = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan delay;
                var started = Stopwatch.GetTimestamp();
                using var activity = TicketFlowWorkerTelemetry.Source.StartActivity("TicketFlow.Worker.Run", ActivityKind.Internal);
                activity?.SetTag("ticketflow.worker.loop", definition.Name);
                try
                {
                    TicketFlowLoopRun run;
                    await using (var scope = scopes.CreateAsyncScope())
                        run = await definition.Run(scope.ServiceProvider, loop, stoppingToken).ConfigureAwait(false);
                    TicketFlowWorkerTelemetry.Record(definition.Name, run.Failures > 0 ? "partial" : "success", run.Items, started);
                    activity?.SetTag("ticketflow.worker.items", run.Items);
                    // Item failures (for example one failing workspace) are isolated by the loop itself: the loop keeps its cadence and
                    // reports Degraded health instead of backing off every other item.
                    failures = 0;
                    health.Succeeded(definition.Name, run.Items, run.Failures);
                    if (run.Failures > 0)
                        logger.LogWarning("TicketFlow worker loop {Loop} completed with {Failures} item failures.", definition.Name, run.Failures);
                    if (run.Items > 0 && run.Failures == 0 && drained < loop.MaximumDrainRuns)
                    {
                        drained++;
                        continue;
                    }
                    drained = 0;
                    delay = Jittered(loop.Interval, settings.Jitter);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    failures++;
                    drained = 0;
                    activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
                    TicketFlowWorkerTelemetry.Record(definition.Name, "failure", 0, started);
                    health.Failed(definition.Name, failures);
                    logger.LogWarning("TicketFlow worker loop {Loop} failed ({Failures} consecutive) with {Error}.", definition.Name, failures, error.GetType().Name);
                    delay = Backoff(loop, failures, settings.Jitter);
                }
                await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
        finally
        {
            health.Set(definition.Name, TicketFlowLoopStatus.Stopped);
        }
    }

    internal static TimeSpan Backoff(TicketFlowLoopOptions loop, int failures, double jitter)
    {
        var factor = Math.Pow(2, Math.Min(failures - 1, 30));
        var milliseconds = Math.Min(loop.InitialBackoff.TotalMilliseconds * factor, loop.MaximumBackoff.TotalMilliseconds);
        return Jittered(TimeSpan.FromMilliseconds(milliseconds), jitter);
    }

    internal static TimeSpan Jittered(TimeSpan delay, double jitter) =>
        jitter <= 0 ? delay : TimeSpan.FromMilliseconds(delay.TotalMilliseconds * (1 + (Random.Shared.NextDouble() * 2 - 1) * jitter));
}
