# TicketFlow.Stores.InMemory

Bounded, development-only building blocks for in-memory TicketFlow stores. `ScopedRevisionStore<TKey, TValue>` keys every entry
by the full `TicketScope` plus a key, so reads and writes never cross tenant, project, environment, app or workspace boundaries.
Writes are revision-fenced under a single lock: adds start at revision 1, and replace/remove require the current revision.
Outcomes are a `RevisionWriteResult` union (`RevisionApplied`, `RevisionNotFound`, `RevisionConflict`). Capacity defaults to
10,000 entries and exceeding it throws.

State is process-local, lost on restart, and never shared across workers or nodes. These helpers are examples for development and
tests, not durable storage; use an application-owned or PostgreSQL store in production. Store values should be immutable.
