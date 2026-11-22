# TicketFlow.Integrations.EnvelopeFlow.Surveys

Delivers E12 survey invitations over the EnvelopeFlow channel adapter. Kept separate so the EnvelopeFlow adapter does not force a
Surveys dependency on hosts that only need channels.

- `EnvelopeFlowSurveyInvitationSender` (`ISurveyInvitationSender`): sends the survey link on the surveyed conversation's EnvelopeFlow
  channel to the recipient the channel layer recorded; outside the WhatsApp 24-hour window it sends the configured approved template.

```csharp
services.AddTicketFlow()
    .AddEnvelopeFlowIntegration().AddEnvelopeFlowEmailChannel()
    .AddSurveys().AddConversationSurveys()
    .AddEnvelopeFlowSurveySender(options => options.LinkFactory = message => new Uri($"https://support.example.com/s/{message.Token.Value}"));
```
