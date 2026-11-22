using EnvelopeFlow.Chat;
using EnvelopeFlow.Email;
using EnvelopeFlow.Inbound;
using EnvelopeFlow.Sms;
using EnvelopeFlow.WhatsApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Channels;
using TicketFlow.Surveys;

namespace TicketFlow.Integrations.EnvelopeFlow;

public static class EnvelopeFlowTicketFlowBuilderExtensions
{
    /// <summary>
    /// Registers the EnvelopeFlow inbound adapter (<see cref="EnvelopeFlowInboundEventHandler"/> as the scoped
    /// <see cref="IInboundEventHandler"/>) and <see cref="EnvelopeFlowDestinationContextResolver"/>. Requires <c>AddChannels()</c>.
    /// </summary>
    public static TicketFlowBuilder AddEnvelopeFlowIntegration(this TicketFlowBuilder builder, Action<EnvelopeFlowIntegrationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<EnvelopeFlowIntegrationOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IInboundEventHandler, EnvelopeFlowInboundEventHandler>();
        services.TryAddScoped<EnvelopeFlowDestinationContextResolver>();
        return builder;
    }

    /// <summary>Registers the email channel over the host's <see cref="IEmailTransport"/>; requires an <see cref="IEnvelopeFlowSenderDirectory"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowEmailChannel(this TicketFlowBuilder builder) => Channel(builder, provider =>
        new EnvelopeFlowEmailChannel(provider.GetRequiredService<IEmailTransport>(), provider.GetRequiredService<IEnvelopeFlowSenderDirectory>(),
            provider.GetRequiredService<IOptions<EnvelopeFlowIntegrationOptions>>()));

    /// <summary>Registers the SMS channel over the host's <see cref="ISmsTransport"/>; requires an <see cref="IEnvelopeFlowSenderDirectory"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowSmsChannel(this TicketFlowBuilder builder) => Channel(builder, provider =>
        new EnvelopeFlowSmsChannel(provider.GetRequiredService<ISmsTransport>(), provider.GetRequiredService<IEnvelopeFlowSenderDirectory>()));

    /// <summary>Registers the WhatsApp channel over the host's <see cref="IWhatsAppTransport"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowWhatsAppChannel(this TicketFlowBuilder builder) => Channel(builder, provider =>
        new EnvelopeFlowWhatsAppChannel(provider.GetRequiredService<IWhatsAppTransport>(), provider.GetRequiredService<IOptions<EnvelopeFlowIntegrationOptions>>()));

    /// <summary>Registers the Slack channel (kind <c>slack</c>, threaded) over the host's <see cref="IChatTransport"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowSlackChannel(this TicketFlowBuilder builder) => Channel(builder, provider =>
        new EnvelopeFlowChatChannel(provider.GetRequiredService<IChatTransport>(), EnvelopeFlowChannelKinds.Slack, threaded: true));

    /// <summary>Registers <see cref="EnvelopeFlowSurveyInvitationSender"/> as the <see cref="ISurveyInvitationSender"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowSurveySender(this TicketFlowBuilder builder, Action<EnvelopeFlowSurveyOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<EnvelopeFlowSurveyOptions>().Configure(configure);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddScoped<ISurveyInvitationSender, EnvelopeFlowSurveyInvitationSender>();
        return builder;
    }

    private static TicketFlowBuilder Channel(TicketFlowBuilder builder, Func<IServiceProvider, ITicketChannel> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<EnvelopeFlowIntegrationOptions>();
        builder.Services.AddSingleton(factory);
        return builder;
    }
}
