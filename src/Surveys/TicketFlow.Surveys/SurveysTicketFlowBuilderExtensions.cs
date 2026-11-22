using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Access;
using TicketFlow.Surveys;

namespace TicketFlow;

public static class SurveysTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers surveys: permissions (<see cref="SurveyPermissions.Definitions"/>), <see cref="SurveyOptions"/>, and scoped
    /// <see cref="ISurveyService"/>, <see cref="ISurveyScheduler"/>, <see cref="ISurveyDispatcher"/> and <see cref="ISurveyResponder"/>.
    /// The dispatcher requires an application- or adapter-registered <see cref="ISurveyInvitationSender"/>; TicketFlow ships no
    /// transport. Requires <c>AddAccessControl()</c> and a store (<see cref="AddInMemorySurveyStores"/> for development). Uses TryAdd.
    /// </summary>
    public static TicketFlowBuilder AddSurveys(this TicketFlowBuilder builder, Action<SurveyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<SurveyOptions>();
        if (configure is not null)
            services.Configure(configure);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(SurveyPermissionsMarker)))
        {
            services.AddSingleton<SurveyPermissionsMarker>();
            services.AddAccessPermissions(SurveyPermissions.Definitions);
        }
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ISurveyService, SurveyService>();
        services.TryAddScoped<ISurveyScheduler, SurveyScheduler>();
        services.TryAddScoped<ISurveyDispatcher, SurveyDispatcher>();
        services.TryAddScoped<ISurveyResponder, SurveyResponder>();
        return builder;
    }

    /// <summary>Registers the development-only <see cref="InMemorySurveyStore"/> (process-local, bounded, not durable) as <see cref="ISurveyStore"/>.</summary>
    public static TicketFlowBuilder AddInMemorySurveyStores(this TicketFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton<ISurveyStore, InMemorySurveyStore>();
        return builder;
    }

    private sealed class SurveyPermissionsMarker;
}
