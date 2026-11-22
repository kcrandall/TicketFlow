# T07 — Documentation and WalFlow evaluation

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`docs/live-chat.md` and `docs/realtime.md` (payload choice, authorization, backplanes); evaluate
`WalFlow.Abstractions 0.1.0-alpha.alpha.20260708090623` for change-capture-driven fan-out and either add
`TicketFlow.Integrations.WalFlow` or record a follow-up ticket.

## Dependencies

E7 T01–T06.

## Acceptance criteria

- Docs describe implemented behavior only and list what is not implemented.

## Verification

- Review against the code; `dotnet build TicketFlow.slnx -c Release`.

## Risks and boundaries

None.

## Results

Wrote `docs/live-chat.md` and `docs/realtime.md` (payload choice, authorization, delivery filtering, wire format, scale-out). Evaluated
`WalFlow.Abstractions 0.1.0-alpha.alpha.20260708090623`: not a clean fit (no published outbox row contract; loosely typed row values;
still needs a cross-node transport) — recorded as follow-up [T08](T08-walflow-change-capture-fanout.md); no WalFlow package added.

- `dotnet build TicketFlow.slnx -c Release` — 0 warnings, 0 errors (with concurrent E10/E13 and fix-up work in the tree).
- `bash scripts/verify-package-metadata.sh` — 47 package projects validated.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
