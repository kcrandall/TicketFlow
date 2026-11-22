# TicketFlow.Integrations.PostHog

PostHog analytics sink (`posthog`) for the TicketFlow integration event stream.

```csharp
services.AddTicketFlow().AddEvents().AddPostHogEvents(options =>
    options.Projects["default"] = new PostHogProject(new Uri("https://us.i.posthog.com/"), "phc_..."));
// subscription: new EventSinkRef("posthog", "default"), privacy IdsOnly or Metadata
```

- Posts deliveries to the PostHog `/batch/` capture endpoint: `event` = CloudEvents type, `uuid` = event id (deduplication),
  `timestamp`, `distinct_id` and `properties` = the rendered `data` plus `$groups` (`workspace` group analytics) and
  `ticketflow_sequence`/`ticketflow_subject`/`ticketflow_privacy`.
- `distinct_id` strategy: `ContactId` (default) or `ContactExternalId` (first unmasked `external-user` identity from `Content`
  subscriptions, else the contact id). Events without a contact use `workspace:<id>` with `$process_person_profile: false`.
- Properties contain only what the subscription's privacy level rendered; prefer `IdsOnly`/`Metadata` for analytics.
