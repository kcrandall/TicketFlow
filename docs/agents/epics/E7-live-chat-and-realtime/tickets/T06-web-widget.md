# T06 — Web widget client

Epic: [E7 — Live chat and realtime](../PLAN.md)

Status: **Completed**

## Goal

`clients/widget`: a small TypeScript widget (esbuild, `@microsoft/signalr` pinned) rendering in a shadow DOM with pre-chat form,
deflection results, typing indicators, attachments and reconnect-with-cursor; README and HTML demo page; unit tests with vitest.

## Dependencies

E7 T05.

## Acceptance criteria

- `npm test` and `npm run build` pass with exact, at-least-two-week-old dependency versions.

## Verification

- `cd clients/widget && npm ci && npm test && npm run build`.

## Risks and boundaries

Modest UI; hosts theme it through CSS custom properties referenced by the widget's theme ref.

## Results

Implemented `clients/widget` (TypeScript, `@microsoft/signalr` 10.0.11; dev: esbuild 0.28.2, typescript 6.0.3, vitest 5.1.0, happy-dom
21.0.4 — all published before 2026-11-08): `ChatClient` (handshake, token storage, send with client ids, typing, read receipts, uploads,
deflection, reconnect + catch-up), `ChatWidget` (shadow DOM, pre-chat form, deflection results, typing indicator, text-only rendering),
README and `demo/index.html`.

- `cd clients/widget && npm install && npx tsc --noEmit && npx vitest run && npm run build` — typecheck clean, 3 files / 12 tests passed,
  bundle `dist/ticketflow-chat.js` 66.8 KB.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
