# Post-ticket performance and API review

Role: performance/API/package reviewer.

Input: ticket, diff, public APIs, hot paths, stores, serializers, DI, and tests run.

Inspect:
- Unbounded fan-out/buffers, N+1 store calls, blocking async, and connection/session leaks.
- Public APIs that force application schema, auth framework, endpoints, or conversation storage.
- Ambiguous capability/result semantics and hard-to-replace persistence.
- Provider SDK dependency creep and misplaced feature code.
- Serialization/trimming/AOT claims, payload limits, and excessive reflection.
- Package graph, lockstep versions, validation, and release boundaries.

Output only:
- Findings ordered P0–P3 with file/line, impact, and minimal fix.
- Naming/API suggestions only if they materially improve application composition.
- `No findings` if nothing high-confidence.
