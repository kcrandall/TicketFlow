using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TicketFlow.Integrations.InsightFlow;

public static class InsightFlowTicketFlowBuilderExtensions
{
    /// <summary>Registers <see cref="InsightFlowExportOptions"/>, an in-memory default <see cref="IInsightFlowExportCheckpointStore"/> and <see cref="InsightFlowReportingExporter"/>.</summary>
    public static TicketFlowBuilder AddInsightFlowReportingExport(this TicketFlowBuilder builder, Action<InsightFlowExportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<InsightFlowExportOptions>().Configure(configure);
        builder.Services.TryAddSingleton<IInsightFlowExportCheckpointStore, InMemoryInsightFlowExportCheckpointStore>();
        builder.Services.TryAddSingleton<InsightFlowReportingExporter>();
        return builder;
    }
}
