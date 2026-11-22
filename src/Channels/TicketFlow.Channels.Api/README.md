# TicketFlow.Channels.Api

A generic API channel for TicketFlow.

- Inbound: `ApiChannelInbound.ReceiveAsync(actor, configurationId, ApiInboundMessage)` and `ReportDeliveryAsync` — called by the
  application from its own authenticated endpoints; runs the standard channel pipeline.
- Outbound: `ApiChannel` posts a versioned JSON payload (`"v":1`, `"type":"message.outgoing"`) signed with HMAC-SHA256 over
  `v1\n{timestamp}\n{deliveryId}\n{body}` (`TicketFlow-Webhook-Id`, `-Timestamp`, `-Signature`, `-Key-Id`). Receivers verify with
  `ApiWebhookSignature.Verify` (5-minute replay window, 1-minute skew) or `ApiWebhookReplayGuard` and deduplicate the delivery id.
- SSRF-safe destinations (`ApiWebhookDestinationPolicy`): HTTPS on allowed ports, no user info, optional exact host allow-list,
  public addresses only (validated at connect time and dialled directly), no redirects or proxies.
- The application supplies `IApiChannelDestinationResolver` (endpoint + signing key per configuration); keys are never stored.

```csharp
services.AddSingleton<IApiChannelDestinationResolver, MyDestinations>();
services.AddTicketFlow().AddChannels(...).AddApiChannel(options => options.AllowedHosts.Add("hooks.example.com"));
```
