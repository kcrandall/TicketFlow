# TicketFlow.Integrations.PostHog

PostHog analytics sink (`posthog`) for the TicketFlow integration event stream.

```csharp
services.AddTicketFlow().AddEvents().AddPostHogEvents(options =>
    options.Projects["default"] = new PostHogProject(new Uri("https://us.i.posthog.com/"), "phc_...", [new EventScopeRule("tenant-a")]));
// subscription: new EventSinkRef("posthog", "default"), privacy IdsOnly or Metadata
```

- Posts deliveries to the PostHog `/batch/` capture endpoint: `event` = CloudEvents type, `uuid` = event id (deduplication),
  `timestamp`, `distinct_id` and `properties` = the rendered `data` plus `$groups` (`workspace` group analytics) and
  `ticketflow_sequence`/`ticketflow_subject`/`ticketflow_privacy`.
- `distinct_id` is namespaced: `contact:<id>` (default `ContactId` strategy) or, with `ContactExternalId`, `external:<issuer>:<value>`
  from the first unmasked `external-user` identity in `Content` subscriptions (else `contact:<id>`). Conversation events (messages, SLA,
  routing, delivery status) use the conversation's contact, so a contact's events share one id. Events without a contact use
  `workspace:<id>` with `$process_person_profile: false`.
- Projects are allow-listed per scope (`EventScopeRule`, at least one); other tenants or workspaces cannot target them.
- Properties contain only what the subscription's privacy level rendered; prefer `IdsOnly`/`Metadata` for analytics.
