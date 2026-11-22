# T06 — InsightFlow event export (deferred)

Epic: [E14 — Reporting](../PLAN.md)

Status: **Deferred**

## Goal

`src/Integrations/TicketFlow.Integrations.InsightFlow`: export TicketFlow reporting facts to InsightFlow as server events, without
referencing InsightFlow packages (wire format over HTTP), checkpointed over `IReportingStore.ListFactsAsync`.

## Dependencies

E14 T04; a stable InsightFlow HTTP ingestion contract.

## Acceptance criteria

- Events carry a stable `eventId` derived from the fact key so retries deduplicate; no PII (identifiers only).
- Scope comes from host configuration, never from facts.

## Verification

- Integration tests against a recorded InsightFlow ingestion contract.

## Risks and boundaries

Deferred (2026-11-22): InsightFlow (`/Users/kcrandall/git/InsightFlow`) is not published to NuGet and exposes no HTTP event
ingestion route. Its `ClientTrackBatchRequest` contract (docs/client-event-contract.md) is admitted in-process by
`ClientAnalyticsIngestionService`, and its hosting maps only operations, schema, taxonomy, identity and bulk-sink administration
routes. Revisit when InsightFlow ships an authenticated server ingestion endpoint or publishes its ingestion abstractions.

## Results

Not started.
