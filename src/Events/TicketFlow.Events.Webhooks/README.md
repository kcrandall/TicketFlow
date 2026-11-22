# TicketFlow.Events.Webhooks

Signed HTTPS webhook sink (`webhook`) for the TicketFlow integration event stream.

```csharp
services.AddTicketFlow().AddEvents().AddEventWebhooks(options => options.AllowedHosts.Add("hooks.example.com"));
services.AddSingleton<IEventWebhookKeyResolver, MyWebhookKeys>();   // keys from your secret store
```

- Subscription target: an https URL, validated by the E6 `ApiWebhookDestinationPolicy` SSRF guard (public addresses only, no redirects
  or proxies). One POST per delivery: a CloudEvent (`application/cloudevents+json`) or an array (`application/cloudevents-batch+json`).
- Signed with `ApiWebhookSignature` v1 (HMAC-SHA256 over `v1\n<timestamp>\n<delivery id>\n<body>`; headers `TicketFlow-Webhook-Id`,
  `-Timestamp`, `-Signature`, `-Key-Id`); receivers verify with `ApiWebhookSignature.Verify` or `ApiWebhookReplayGuard` and deduplicate the
  delivery id (stable across retries) and CloudEvent ids.
- Key rotation: while `EventWebhookKeys.Previous` is set the previous key also signs (`TicketFlow-Webhook-Signature-Previous`,
  `-Previous-Key-Id`), so receivers can switch keys without dropped deliveries. `InMemoryEventWebhookKeyRing` shows the flow.
- 2xx delivered; 408/425/429/5xx retried (honoring `Retry-After`); other statuses and blocked destinations are permanent (dead-lettered).
