# TicketFlow.Integrations.InsightFlow

Wire-format HTTP reporting exporter from TicketFlow (`IReportingStore.ListFactsAsync`) to InsightFlow without taking a package dependency on InsightFlow.

```csharp
services.AddTicketFlow()
    .AddReporting()
    .AddInsightFlowReportingExport(options =>
    {
        options.Endpoint = new Uri("https://insightflow.example.com/api/v1/events/batch");
        options.ApiKey = "if_live_...";
        options.TenantId = "acme";
        options.ProjectId = "support";
        options.Environment = "prod";
        options.AppName = "ticketflow";
    });
```

- Encodes `ReportFact`s in InsightFlow's `ClientTrackBatchRequest` camel-case JSON wire contract (`app`, `sharedProperties`, `events[]` with `eventName`, `eventId`, `timestamp`, `properties`).
- Derives a deterministic RFC 9562 UUIDv8 `eventId` from `(scope, fact.SourceId, fact.Metric)` so retries deduplicate cleanly.
- Carries identifiers and numeric metrics only — never PII or message bodies.
- Populates InsightFlow's `app` scope (`tenantId`, `projectId`, `environment`, `appName`) exclusively from host configuration, never from facts.
- Advances the per-workspace checkpoint (`IInsightFlowExportCheckpointStore`) only after HTTP 2xx acknowledgement and automatically splits batches on oversized payloads or HTTP `413`.
