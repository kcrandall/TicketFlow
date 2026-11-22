# TicketFlow.Hosting.Worker

Optional `BackgroundService` hosting for every TicketFlow library loop (outbox, routing, SLA, automation, contact merges, surveys, live
chat, assist, integration events, reporting retention), with per-loop options, jittered backoff, per-workspace failure isolation and a
persistent directory position, health checks (including staleness), telemetry and SLA worker identity validation.

The package references the feature implementation packages whose loops it hosts, so those assemblies come along even when a host
registers only some features (unregistered ones report `NotConfigured`). See docs/hosting.md.
