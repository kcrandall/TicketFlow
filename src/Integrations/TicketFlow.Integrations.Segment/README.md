# TicketFlow.Integrations.Segment

Segment HTTP Tracking API sink (`segment`) for the TicketFlow integration event stream.

```csharp
services.AddTicketFlow().AddEvents().AddSegmentEvents(options =>
    options.Sources["default"] = new SegmentSource(new Uri("https://api.segment.io/"), "write-key", [new EventScopeRule("tenant-a")]));
// subscription: new EventSinkRef("segment", "default"), privacy IdsOnly or Metadata
```

- Posts deliveries to `{host}/v1/batch` with HTTP Basic auth (`writeKey:`), mapping each event to a Segment `track` call: `event` = CloudEvents type, `messageId` = event id (deduplication), `timestamp`, `userId` (or `anonymousId` for events without a contact), `properties` (the rendered `data` plus `ticketflow_sequence`, `ticketflow_subject`, `ticketflow_privacy`, `ticketflow_workspace`) and `context.groupId` = workspace id.
- `userId` is namespaced: `contact:<id>` (default `ContactId`) or, with `ContactExternalId`, `external:<issuer>:<value>` from the first unmasked `external-user` identity in `Content` subscriptions (else `contact:<id>`). Events without a contact set `anonymousId` to `workspace:<id>`.
- Batches larger than `MaximumPayloadBytes` (default 500 KiB, Segment's batch limit) or rejected with HTTP `413` split automatically when more than one event is present.
- Sources are allow-listed per scope (`EventScopeRule`, at least one); other tenants or workspaces cannot target them.
