# Architecture

## Flow family placement

```
IdentityFlow  -> agent/API identities, signed widget tokens          (Integrations.IdentityFlow)
EnvelopeFlow  -> channel transports, inbound verification, delivery  (Integrations.EnvelopeFlow)
WalFlow       -> change capture for realtime fan-out and outbox      (Integrations.WalFlow)
InsightFlow   -> reporting events and queries                        (Integrations.InsightFlow)
RevenueFlow   -> optional seat/plan entitlements                     (Integrations.RevenueFlow)
```

TicketFlow Core and feature packages never reference those repositories. Adapters reference only their published abstraction packages.

## Scope

`TicketScope` = tenant → project → environment → app (identical to the other Flow repositories) plus `WorkspaceId`. Every stored record carries the full scope; stores reject cross-scope reads and writes.

## Domain

| Concept | Notes |
| --- | --- |
| Workspace | Support organisation within a scope. Owns settings, business calendars, roles. |
| Inbox | A channel binding plus routing, business hours and SLA policy. |
| Team | Group of agents; grant target and routing target. |
| Agent / Principal | Application-owned identity resolved to a `PrincipalRef`; may be human, API key or automation. |
| Contact | Customer with an identity graph of channel identifiers (verified/claimed). |
| Conversation | Revisioned timeline of events with status, priority, assignee, team, labels and custom attributes. |
| Message / Note | Timeline events; notes are never delivered to contacts. |
| Label, Custom attribute | Typed, access-annotated metadata; attributes can be PII and maskable. |
| Canned response, Macro | Reusable agent content and multi-action shortcuts. |
| SLA policy, Business calendar | First/next response and resolution targets with pause states. |
| Automation rule | Trigger → conditions → actions, executed durably and replayably. |
| Help center | Articles/collections/locales for deflection and assistant grounding. |

## Access control

See [access-control.md](access-control.md). Decisions are C# unions (`AccessDecision = Allowed | Denied | Masked`) and features must handle every case.

## Channels

`TicketFlow.Channels.Abstractions` defines inbound normalisation, outbound replies, delivery status and capabilities (attachments, reply windows, rich content). EnvelopeFlow-backed channels arrive through `Integrations.EnvelopeFlow`; live chat, API and mailbox channels are TicketFlow packages.

## Hosting

Optional `Hosting.AspNetCore` endpoints and `Realtime.SignalR` hubs authorize through Access. Libraries do not require them.
