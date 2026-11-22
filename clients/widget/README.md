# TicketFlow live-chat widget

A small TypeScript web widget for TicketFlow live chat. It connects to the visitor hub of `TicketFlow.Realtime.SignalR`
(`@microsoft/signalr` 10.0.11 is its only runtime dependency) and renders in a shadow DOM so host-page CSS cannot leak in or out.

Features: session token handshake (stored in `localStorage` per widget key; rejected tokens are dropped and a new session starts),
pre-chat form for required fields, help-center deflection before the first message, typing indicators both ways, read receipts,
attachments through the upload endpoint, automatic reconnect that re-authenticates and fetches every message after the highest
sequence it has seen, business-hours status and away message. Message bodies are always rendered as text, never HTML.

## Build and test

```bash
npm ci
npm test          # vitest (happy-dom)
npm run typecheck # tsc --noEmit
npm run build     # esbuild → dist/ticketflow-chat.js (IIFE, global TicketFlowChat)
```

`npm test` also builds the bundle in memory and enforces an 80 KB size budget and a source map without embedded sources
(`--sources-content=false`, so publishing the map does not publish the source text).

Dependencies are pinned to exact versions that were at least two weeks old when added (esbuild 0.28.2, typescript 6.0.3,
vitest 5.1.0, happy-dom 21.0.4).

## Embed

```html
<div id="ticketflow-chat"></div>
<script src="/static/ticketflow-chat.js"></script>
<script>
  TicketFlowChat.mount({
    container: document.getElementById("ticketflow-chat"),
    hubUrl: "https://support.example.com/ticketflow/realtime/visitor",
    uploadUrl: "https://support.example.com/ticketflow/livechat/attachments",
    widgetKey: "wk_live_123",
  });
</script>
```

The page's origin must be one of the widget's allowed origins; the hub checks the browser `Origin` header. When the widget is on
another origin than the hub, the host must enable CORS with credentials disabled for the hub and upload endpoints.

### Token storage and XSS

By default the session token is kept in `localStorage` (per widget key) so a reload resumes the conversation. Any script that runs
on the page — including injected script through an XSS flaw on the host site — can read it and act as that visitor (read the
visitor's own chats, send messages) until it expires or is revoked; it grants nothing beyond that session. Pass
`tokenStorage: "memory"` to keep the token in memory only (a reload starts a new session), and keep a strict Content Security
Policy on pages that embed the widget.

Theme with CSS custom properties on the container: `--tf-accent`, `--tf-accent-text`, `--tf-surface`, `--tf-text`, `--tf-muted`,
`--tf-border`, `--tf-font`. The widget configuration's `themeRef` is passed through to the page (`client.widget.themeRef`) for hosts
that map it to a stylesheet.

`demo/index.html` mounts the built bundle against a local host.

## Identity verification

Logged-in sites can verify visitors: the site's backend computes `hex(HMAC-SHA256(inboxSecret, userId))` and the page calls
`widget.client.identify("hmac", userId, signature, displayName)` (or `identify("token", signedToken)` when the host registers an
`IVisitorTokenValidator`). The secret never reaches the browser.
