using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace TicketFlow.Surveys.Stores.Postgres;

public static class PostgresSurveyTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresSurveyStore"/> over the application-owned <paramref name="dataSource"/> as <see cref="ISurveyStore"/>
    /// using TryAdd. Schema creation is explicit: call <see cref="PostgresSurveyStore.InitializeAsync"/> during deployment or startup.
    /// </summary>
    public static TicketFlowBuilder AddPostgresSurveyStores(this TicketFlowBuilder builder, NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dataSource);
        builder.Services.TryAddSingleton(new PostgresSurveyStore(dataSource));
        builder.Services.TryAddSingleton<ISurveyStore>(provider => provider.GetRequiredService<PostgresSurveyStore>());
        return builder;
    }
}
