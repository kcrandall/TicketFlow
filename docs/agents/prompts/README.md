# Post-ticket review prompts

Use these after ticket implementation and focused tests pass, before an authorized commit.

Assign separate review slices in parallel when tooling permits:

- [Security](post-ticket-security-review.md)
- [Correctness](post-ticket-correctness-review.md)
- [Tests](post-ticket-test-review.md)
- [Performance/API](post-ticket-performance-api-review.md)

Give each reviewer the ticket, current diff, touched files, relevant contracts, and verification commands. Request only high-confidence actionable findings with severity, file/line, impact, and smallest safe fix. `No findings` is a valid result. Do not ask for speculative rewrites.

Adapted from the WalFlow post-ticket workflow.
