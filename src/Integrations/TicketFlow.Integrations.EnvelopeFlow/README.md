# TicketFlow.Integrations.EnvelopeFlow

EnvelopeFlow adapter for TicketFlow channels. References only EnvelopeFlow abstraction packages (pinned central version
`0.1.0-alpha.3.1`); hosts add the EnvelopeFlow provider packages and webhook routes they need.

- `EnvelopeFlowInboundEventHandler` (`IInboundEventHandler`): maps verified `InboundEvent` messages to `InboundChannelMessage`
  (email In-Reply-To, SMS/WhatsApp sender threads, Slack channel + thread root) and delivery receipts to delivery reports; feedback
  is ignored. Endpoints are found by `{provider}:{account}[:{local}]` addresses within the event's scope partition.
- Channels: `EnvelopeFlowEmailChannel` (In-Reply-To/References, `Re:` subject, submission context), `EnvelopeFlowSmsChannel`,
  `EnvelopeFlowWhatsAppChannel` (24-hour window, templates, optional quoted reply) and `EnvelopeFlowChatChannel` (Slack thread ts;
  Teams unthreaded). WhatsApp/chat destinations use `EnvelopeFlowDestinations`; host binding delegates call
  `EnvelopeFlowDestinationContextResolver` for the recipient and `LastCustomerMessageAtUtc`.
- `EnvelopeFlowSurveyInvitationSender` (`ISurveyInvitationSender`) sends survey links over the conversation's channel.

```csharp
services.AddSingleton<IEmailTransport>(smtpTransport);
services.AddSingleton<IEnvelopeFlowSenderDirectory, MySenders>();
services.AddTicketFlow()
    .AddChannels(options => options.WorkerPrincipal = scope => new ApiClientPrincipal(scope, new ApiClientId("channels")))
    .AddEnvelopeFlowIntegration()
    .AddEnvelopeFlowEmailChannel()
    .AddEnvelopeFlowSurveySender(options => options.LinkFactory = message => new Uri($"https://support.example.com/s/{message.Token}"));
```
