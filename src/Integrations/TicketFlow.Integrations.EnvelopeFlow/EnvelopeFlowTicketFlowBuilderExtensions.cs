using EnvelopeFlow.Chat;
using EnvelopeFlow.Email;
using EnvelopeFlow.Inbound;
using EnvelopeFlow.Sms;
using EnvelopeFlow.WhatsApp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Channels;

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
    public static TicketFlowBuilder AddEnvelopeFlowEmailChannel(this TicketFlowBuilder builder) => Channel(builder, EnvelopeFlowChannelKinds.Email, provider =>
        new EnvelopeFlowEmailChannel(provider.GetRequiredService<IEmailTransport>(), provider.GetRequiredService<IEnvelopeFlowSenderDirectory>(),
            provider.GetRequiredService<IOptions<EnvelopeFlowIntegrationOptions>>()));

    /// <summary>Registers the SMS channel over the host's <see cref="ISmsTransport"/>; requires an <see cref="IEnvelopeFlowSenderDirectory"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowSmsChannel(this TicketFlowBuilder builder) => Channel(builder, EnvelopeFlowChannelKinds.Sms, provider =>
        new EnvelopeFlowSmsChannel(provider.GetRequiredService<ISmsTransport>(), provider.GetRequiredService<IEnvelopeFlowSenderDirectory>()));

    /// <summary>Registers the WhatsApp channel over the host's <see cref="IWhatsAppTransport"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowWhatsAppChannel(this TicketFlowBuilder builder) => Channel(builder, EnvelopeFlowChannelKinds.WhatsApp, provider =>
        new EnvelopeFlowWhatsAppChannel(provider.GetRequiredService<IWhatsAppTransport>(), provider.GetRequiredService<IOptions<EnvelopeFlowIntegrationOptions>>()));

    /// <summary>Registers the Slack channel (kind <c>slack</c>, threaded) over the host's <see cref="IChatTransport"/>.</summary>
    public static TicketFlowBuilder AddEnvelopeFlowSlackChannel(this TicketFlowBuilder builder) => Channel(builder, EnvelopeFlowChannelKinds.Slack, provider =>
        new EnvelopeFlowChatChannel(provider.GetRequiredService<IChatTransport>(), EnvelopeFlowChannelKinds.Slack, threaded: true));

    /// <summary>Registers one channel per kind: repeated calls are no-ops (TryAdd semantics), so the registry never sees duplicates.</summary>
    private static TicketFlowBuilder Channel(TicketFlowBuilder builder, Workspaces.ChannelKind kind, Func<IServiceProvider, ITicketChannel> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<EnvelopeFlowIntegrationOptions>();
        if (services.Any(descriptor => descriptor.ServiceType == typeof(EnvelopeFlowChannelRegistration) && descriptor.ImplementationInstance is EnvelopeFlowChannelRegistration registered
            && registered.Kind == kind))
            return builder;
        services.AddSingleton(new EnvelopeFlowChannelRegistration(kind));
        services.AddSingleton(factory);
        return builder;
    }

    private sealed record EnvelopeFlowChannelRegistration(Workspaces.ChannelKind Kind);
}
