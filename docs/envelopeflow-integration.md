# EnvelopeFlow integration notes

Audit of EnvelopeFlow's public surface for `TicketFlow.Integrations.EnvelopeFlow` (epic E6; E7 live chat does not use EnvelopeFlow). Audited at EnvelopeFlow `main` on 2026-11-22.

- Minimum version: **`0.1.0-alpha.3.1`**. All EnvelopeFlow packages share one lockstep version, so pin a single `EnvelopeFlowVersion` property.
- Alphas make no API-compatibility promise. List current versions with `curl -s https://api.nuget.org/v3-flatcontainer/envelopeflow.core/index.json`.
- EnvelopeFlow targets `net11.0`.

## Packages to reference

Reference abstractions first. Add implementation packages only for the providers and stores a host enables. The verifiers and transports live in provider packages, so those must stay out of the core adapter.

| Need | Package ID |
| --- | --- |
| Scope, recipient and identity primitives (`EnvelopeScope`, `RecipientBinding`) | `EnvelopeFlow.Core` |
| Normalized inbound event, handler and receipt-store contracts | `EnvelopeFlow.Inbound.Abstractions` |
| Inbound intake/dedup (`InboundProcessor`), Twilio and Mailgun verifiers | `EnvelopeFlow.Inbound` |
| Durable inbound receipts | `EnvelopeFlow.Inbound.Stores.Postgres` |
| Email message and transport contracts | `EnvelopeFlow.Email.Abstractions` |
| SMS message and transport contracts | `EnvelopeFlow.Sms.Abstractions` |
| WhatsApp message, template and transport contracts | `EnvelopeFlow.WhatsApp.Abstractions` |
| Slack/Teams message and transport contracts | `EnvelopeFlow.Chat.Abstractions` |
| Durable outbound delivery contracts (optional) | `EnvelopeFlow.Delivery.Abstractions` |
| Durable delivery bridges for email/SMS (optional) | `EnvelopeFlow.Integrations.Transports` |
| Durable delivery bridges for chat/WhatsApp (optional) | `EnvelopeFlow.Integrations.Messaging` |
| Complaint/unsubscribe suppression (optional) | `EnvelopeFlow.Integrations.Feedback` |
| Email providers (host choice) | `EnvelopeFlow.Providers.Email.Smtp`, `.Api` (Mailgun/SES), `.SendGrid`, `.Postmark`, `.Resend`, `.AzureCommunicationServices` |
| SMS providers (host choice) | `EnvelopeFlow.Providers.Sms.Twilio`, `.Telnyx`, `.AzureCommunicationServices` |
| WhatsApp | `EnvelopeFlow.Providers.WhatsApp.Meta` |
| Slack / Teams | `EnvelopeFlow.Providers.Chat.Slack`, `EnvelopeFlow.Providers.Chat.Teams` |

Do not take a dependency on `EnvelopeFlow.Conversations*`. Its threading model would duplicate TicketFlow's own conversations. TicketFlow keeps its own `provider + account + thread key → conversation` index.

## 1. Receiving verified inbound messages

EnvelopeFlow supplies verifiers but no HTTP routes. TicketFlow (or the host) owns each webhook endpoint. On each request:

1. Read at most 131,072 raw bytes.
2. Resolve credentials and the trusted `EnvelopeScope` from route configuration, never from callback fields.
3. Call the provider verifier. It returns the closed `InboundVerificationResult`: either `VerifiedInboundEvent(Event)` or `RejectedInboundEvent(FailureCode)`.
4. Pass the event to `InboundProcessor.AcceptDurablyAsync(event)` (needs a durable `IInboundReceiptStore`) or `HandleDirectAsync`.
5. Return HTTP 2xx only when `InboundIntakeResult.CanAcknowledge` is true.
6. Implement `IInboundEventHandler.HandleAsync(InboundEvent)` as the adapter's single entry point. Delivery is at-least-once, so use `event.Key` (or `Key.Digest`) as the idempotency key in the same transaction that writes the TicketFlow message.

| Channel | Verifier (package) |
| --- | --- |
| Email (inbound route) | `MailgunWebhookTranslator.TranslateMessage` (`EnvelopeFlow.Inbound`). It needs mTLS-authenticated Mailgun and gives the plain body only; raw MIME and attachments are not parsed. Other inbound mail goes through TicketFlow's own mailbox channel. |
| SMS | `TwilioWebhookTranslator.Translate` (`EnvelopeFlow.Inbound`), `TelnyxMessageVerifier.Verify`, `AzureSmsMessageTranslator.Translate` |
| WhatsApp | `MetaWhatsAppMessageVerifier.Verify` (messages); `MetaWhatsAppReceiptVerifier.Verify` for statuses (see section 3) |
| Slack | `SlackCallbackTranslator.VerifyMessage` (messages), `VerifyAction` (button/menu interactions) |
| Teams | Outbound only (Workflows webhook). There is no inbound path. |

`InboundEvent` fields used for a `Message`:

- `Provider` (`mailgun`, `twilio`, `telnyx`, `azure-sms`, `whatsapp-meta`, `slack`) plus `ProviderAccountId`: the channel identity. Map them to a TicketFlow inbox.
- `ProviderMessageId`: the stable provider message id. Formats:
  - email: the Message-Id, exactly as Mailgun sent it (usually `<…>`)
  - Twilio: `SM…`/`MM…`
  - WhatsApp: `wamid.…`
  - Slack: `T…:C…:ts`
- `EventId`: the receipt id. It differs from `ProviderMessageId` for Mailgun (a hash of message-id and recipient) and Slack (`Ev…`).
- `InReplyTo`: thread correlation. Email gives the `In-Reply-To` header, WhatsApp the quoted `context.id`, and Slack the parent as `T…:C…:thread_ts`.
- `Subject` (email), `Text`, `ObservedAt` (Unix epoch when Twilio/Mailgun give no time), `Interaction` (button or menu selection; untrusted input).
- `From` / `To`: sender address evidence, authenticated only to the extent the provider is. That means E.164 for SMS and WhatsApp, a Slack user id `U…`/`W…` with the trusted channel id in `To`, and the Mailgun `sender` (envelope) for email. Treat email `From` as a claim, not an identity proof. Contact matching and verification stay in TicketFlow.
- `MediaCount`, plus the opt-in `Attachments` described next.

**Attachments (added for TicketFlow in `0.1.0-alpha.3.1`, EnvelopeFlow E4 T09).** Call the explicit overloads with `includeAttachments: true`:

- `TwilioWebhookTranslator.Translate(..., trustedScope, true)`
- `MetaWhatsAppMessageVerifier.Verify(..., now, true)`
- `TelnyxMessageVerifier.Verify(..., now, true)`
- `SlackCallbackTranslator.VerifyMessage(..., now, true)`

`InboundEvent.Attachments` then holds one `InboundAttachment(ProviderReference, ContentType?, FileName?)` per media item:

| Provider | `ProviderReference` |
| --- | --- |
| Twilio, Telnyx | HTTPS media URL |
| WhatsApp | Graph media id. Download needs the Graph API and the access token. |
| Slack | File id `F…`. Download needs `files.info` with a bot token and the `files:read` scope. |

EnvelopeFlow never downloads anything. Fetch through an allow-listed, size-limited client. Enable the flag from day one: switching it on later changes receipt bytes, so existing receipt ids would `Conflict`.

**Thread key derivation in the adapter:**

| Channel | Thread key |
| --- | --- |
| Email | Strip `<>` from `InReplyTo` and look it up among stored outbound/inbound Message-Ids. Fall back to the subject/contact heuristics in TicketFlow. `References` is **not** exposed (gap 2). |
| SMS | No thread. Use (`ProviderAccountId`, `To`, `From`) and the open-conversation policy. |
| WhatsApp | Use (`ProviderAccountId`, `From`). `InReplyTo` only marks a quoted reply. |
| Slack | The thread root `ts` is the last `:` segment of `InReplyTo`. When `InReplyTo` is null, use the last segment of `ProviderMessageId` (the message starts a thread). Key: (`ProviderAccountId`, `To`, root ts). |

## 2. Sending agent replies on the same channel/thread

| Channel | Contract (abstractions package) | Threading |
| --- | --- | --- |
| Email | `IEmailTransport.SendAsync(EmailMessage)` | Add `EmailHeader("In-Reply-To", "<id>")` and `EmailHeader("References", "<id1> <id2> …")` to `EmailMessage.Headers`. Both are allowed; `Message-Id` is reserved. Keep a `Re:` subject. Set `SubmissionContext` to a stable key for API-provider idempotency. |
| SMS | `ISmsTransport.SendAsync(SmsMessage { To = event.From, From = event.To, Text })` | None. `SmsCapabilities` reports replies, media and receipts. |
| WhatsApp | `IWhatsAppTransport.SendAsync(WhatsAppSendRequest(WhatsAppDestination, WhatsAppMessage))` | `WhatsAppMessage.ReplyToMessageId = "wamid.…"` (optional quote). Text, media, interactive or template. |
| Slack | `IChatTransport.SendAsync(ChatSendRequest(ChatDestination, new ChatMessage(text, ThreadId: rootTs)))` | `ThreadId` is the bare Slack `ts` (`^\d+\.\d+$`), *not* the `T:C:ts` id |
| Teams | Same chat contract | `ThreadId` must be null (Workflows cannot thread) |

`WhatsAppDestination`/`ChatDestination` take a `RecipientBinding` owner, an application destination id and a version. The provider re-resolves live credentials and binding through a delegate the application supplies:

- `MetaWhatsAppTransport` takes `Func<WhatsAppDestination, CancellationToken, Task<MetaWhatsAppBinding?>>`.
- The Slack transport takes an equivalent binding delegate.

TicketFlow implements these against its channel configuration.

Record the outbound correlation id from the result's `ProviderMessageId`:

| Result type | Id |
| --- | --- |
| `EmailSubmissionResult` | API provider id. **SMTP now returns the generated Message-Id (E4 T08).** |
| `SmsSubmissionResult` | Provider message id |
| `WhatsAppSubmissionResult` | `wamid` |
| `ChatSubmissionResult` | Slack message id |

Treat `Unknown` as "maybe sent": never auto-retry it.

Email caveat: API providers return their own ids, which are not always the RFC Message-Id customers will cite.

- Mailgun returns the Message-Id.
- SES returns an id whose Message-Id is `<id@region.amazonses.com>`.
- SendGrid, Postmark, Resend and ACS ids differ from the Message-Id.

To thread reliably with those providers, also store the outbound `References` chain you sent, and match inbound `InReplyTo` against both. With SMTP, the returned Message-Id is exact.

For durable sends with retries, quarantine and audit, the following packages are optional:

- `IDeliveryStore.AdmitAsync(new DeliveryRequest(scope, idempotencyKey, [target], …))`
- Targets come from `DeliveryPayloads.Email/Sms` (`EnvelopeFlow.Integrations.Transports`) or `MessagingDeliveryPayloads.Chat/WhatsApp` (`EnvelopeFlow.Integrations.Messaging`).
- `IDeliveryEventHandler` receives state changes. `IDeliveryStore.GetAsync` then yields `DeliverySnapshot.ProviderId`.

## 3. Delivery status and feedback

Delivery receipts and feedback are ordinary `InboundEvent`s through the same `IInboundEventHandler`. Correlate `event.ProviderMessageId` (plus `Provider`/`ProviderAccountId`) with the stored outbound id.

| Event kind | Fields |
| --- | --- |
| `Kind == DeliveryReceipt` | `DeliveryState` is `Accepted`, `Sent`, `Delivered`, `Read`, `Failed`, `Undelivered` or `Deferred`. Also `ProviderErrorCode`, and with opt-in bounce classification `BounceKind` (`Hard`/`Soft`/`Undetermined`). |
| `Kind == Feedback` | `FeedbackKind` is `Complaint`, `Unsubscribe`, `Open` or `Click`. `To` is the affected address. |

Sources:

| Provider | Receipt / feedback source |
| --- | --- |
| Twilio | `TwilioWebhookTranslator` |
| Mailgun | `MailgunWebhookTranslator.TranslateEvent` |
| SES | `SesSnsWebhookTranslator` |
| SendGrid, Postmark, Resend, ACS email | `SendGridEventWebhook`, `PostmarkEventWebhook`, `ResendEventWebhook`, `AzureEmailEventWebhook` |
| Telnyx, ACS SMS | `TelnyxReceiptVerifier.Verify`, `AzureSmsReceiptTranslator.Translate` |
| WhatsApp | `MetaWhatsAppReceiptVerifier.Verify` |

Optional pieces:

- `DeliveryEvidence` (`Accepted`/`Delivered`/`Read`) can be pushed into the durable store with `IDeliveryStore.RecordEvidenceAsync`. Failures after acceptance are not stored there, so TicketFlow should keep its own message status from the receipt events.
- `FeedbackPolicyHandler` (`EnvelopeFlow.Integrations.Feedback`) can apply complaint/unsubscribe suppression.

## 4. WhatsApp 24-hour window

EnvelopeFlow enforces the window but does not track it. Every `MetaWhatsAppTransport.SendAsync` call resolves a `MetaWhatsAppBinding`, whose `LastCustomerMessageAtUtc` must be supplied by TicketFlow: the latest inbound `ObservedAt` from that contact on that number. The binding's `ApprovedTemplates` and `Consented` are also checked.

- A free-form (non-template) message sent 24 hours or more after the last customer message returns `Rejected` with `FailureCode = "customer_service_window_closed"`.
- A template not in `ApprovedTemplates` returns `template_not_approved`.

TicketFlow should store `LastCustomerMessageAtUtc` per conversation and expose a "reply window open until" capability in the composer. The window can only be extended by a customer message; EnvelopeFlow does not read Meta's conversation-expiry metadata.

## 5. CSAT survey notifications

- **WhatsApp:** outside the window, use an approved template, `new WhatsAppMessage(Template: new WhatsAppTemplate(name, locale, [new WhatsAppTextComponent(WhatsAppComponentKind.Body, [link])]))`. Inside the window, interactive buttons/lists (`WhatsAppInteractive`) work, and ratings arrive back as `InboundEvent.Interaction`.
- **Slack:** a `ChatCard` with buttons; responses come through `SlackCallbackTranslator.VerifyAction`.
- **Email/SMS:** render body text with `EnvelopeFlow.Templates` (`MessageTemplate`, literal or `TemplateSyntax.Localized`, safe `{variable}` substitution) and send through the normal transport or durable delivery. `EnvelopeFlow.Templates.Delivery` freezes rendered targets for durable sends.
- **Rules for all channels:** CSAT is a notification, so check `EnvelopeFlow.Preferences` consent/suppression or TicketFlow's own consent before sending. Interaction values are untrusted, so map them to a signed survey token in TicketFlow.

## Gaps and decisions

1. **Fixed in EnvelopeFlow for this integration (`0.1.0-alpha.3.1`):**
   - SMTP now returns the Message-Id (E4 T08).
   - Inbound attachment references are available as an opt-in (E4 T09).
2. **Email `References` header is not normalized.** Only `In-Reply-To` is exposed. This is enough for normal replies; clients that drop `In-Reply-To` fall back to TicketFlow heuristics. It is a candidate EnvelopeFlow ticket and needs an opt-in, because it changes receipt bytes.
3. **Inbound email through EnvelopeFlow is Mailgun-route only, with no MIME/attachments.** TicketFlow's mailbox/IMAP channel or a raw-MIME webhook should remain the primary email intake. SES/SendGrid/Postmark inbound parse is not provided.
4. **There is no stable Message-Id for API email providers.** The application cannot set `Message-Id` (reserved header). See the caveat in section 2.
5. **Teams is outbound-only** (Workflows notifications, no thread selection, no inbound). Two-way Teams support needs a future Bot Framework provider.
6. **The WhatsApp window is application-supplied** (section 4). There is no automatic tracking.
7. **Delivery status is per provider callback.** EnvelopeFlow has no cross-channel "message status" aggregate, so TicketFlow keeps its own status per outbound message from `DeliveryReceipt` events.
