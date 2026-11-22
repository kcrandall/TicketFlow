# Post-ticket security review

Role: security reviewer.

Input: ticket, diff, touched files, nearby identity/store/provider code, and tests run.

Inspect:
- Full-scope isolation; wrong recipient/identity/install binding; unauthorized reassignment.
- Webhook authenticity, replay, acknowledgment, raw-body handling, and credential selection.
- SMTP TLS/auth defaults; push endpoint SSRF, redirects, and DNS changes.
- Logging of tokens, subscription keys, addresses, bodies, secrets, or raw provider exceptions.
- Concurrency races weakening revocation, ownership, fencing, or deduplication.
- Unsafe defaults and undocumented in-memory/non-durable behavior.

Output only:
- Findings ordered P0–P3 with file/line, concrete impact, and smallest safe fix.
- Directly relevant missing security tests.
- `No findings` if nothing high-confidence.
