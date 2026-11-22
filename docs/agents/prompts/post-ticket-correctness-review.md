# Post-ticket correctness review

Role: correctness/regression reviewer.

Input: ticket, diff, touched files, neighboring contracts, and tests run.

Inspect:
- State transitions, revisions, scope keys, cancellation, expiry, and idempotency.
- Message/registration payload validation and provider outcome classification.
- Required store atomicity, stale claims, out-of-order receipts, and unknown submissions.
- Optional feature boundaries; endpoint/auth ownership and application-schema independence.
- Resolver paging stability, live suppression, and unsupported capability errors.
- DI overrides, disposal, and configuration validation.

Output only:
- Findings ordered P0–P3 with file/line, repro/reasoning, and fix idea.
- Small contract/test gaps that could mask a regression.
- `No findings` if nothing high-confidence.
