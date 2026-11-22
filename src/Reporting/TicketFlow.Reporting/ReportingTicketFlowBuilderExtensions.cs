using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Conversations;
using TicketFlow.Surveys;

namespace TicketFlow.Reporting;

public static class ReportingTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers reporting: <see cref="ReportPermissions.Definitions"/> (<c>report:export</c>, <c>report:view-own</c>),
    /// <see cref="ReportingOptions"/>, scoped <see cref="IReportingService"/>, <see cref="ReportingProjector"/> (also as an
    /// <see cref="IConversationOutboxHandler"/>, consumer <c>reporting</c>), <see cref="ReportingRetentionWorker"/> and an
    /// <see cref="IReportSatisfactionSource"/> over the registered <see cref="ISurveyStore"/> (empty without one). Requires the access
    /// engine, workspace and conversation registrations and a reporting store; uses TryAdd so application registrations win.
    /// </summary>
    public static TicketFlowBuilder AddReporting(this TicketFlowBuilder builder, Action<ReportingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ReportingOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(ReportingPermissionsMarker)))
        {
            services.AddSingleton<ReportingPermissionsMarker>();
            services.AddAccessPermissions(ReportPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IReportSatisfactionSource>(provider => provider.GetService<ISurveyStore>() is { } surveys
            ? new SurveyReportSatisfactionSource(surveys)
            : NoReportSatisfactionSource.Instance);
        services.TryAddScoped<IReportingService, ReportingService>();
        services.TryAddScoped<ReportingProjector>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IConversationOutboxHandler, ReportingProjector>());
        services.TryAddScoped<ReportingRetentionWorker>();
        return builder;
    }

    /// <summary>Registers the development-only <see cref="InMemoryReportingStore"/> (process-local, bounded, not durable) as <see cref="IReportingStore"/>.</summary>
    public static TicketFlowBuilder AddInMemoryReportingStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<IReportingStore, InMemoryReportingStore>();
        return builder;
    }

    private sealed class ReportingPermissionsMarker;
}
