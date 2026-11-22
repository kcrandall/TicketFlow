# E5 — Conversations and timeline

Status: **Completed**

Access-checked, revision-fenced conversations with filtered list queries.

## Completion gate

Public APIs implement the outcome below with focused tests, Release solution build, package validation and parallel post-ticket reviews passing.

Outcome: Revisioned conversation timeline, messages, notes, labels, custom attributes, status/priority.

## Execution order

| Ticket | Status | Dependencies |
| --- | --- | --- |
| [T01 — Conversation model, timeline events, codec and permissions](tickets/T01-conversation-model-timeline-and-codec.md) | Completed | E2, E4 |
| [T02 — Store, outbox contracts and in-memory store](tickets/T02-store-outbox-contracts-and-in-memory-store.md) | Completed | E5 T01 |
| [T03 — Conversation service: commands, timeline reads, masking and filtered lists](tickets/T03-conversation-service.md) | Completed | E5 T02, E3 |
| [T04 — Outbox dispatcher and contact-merge follower](tickets/T04-outbox-dispatcher-and-merge-follower.md) | Completed | E5 T03, E4 |
| [T05 — PostgreSQL conversation store](tickets/T05-postgres-conversation-store.md) | Completed | E5 T02 |

## Verification record

T01–T05 (2026-11-22): `dotnet build TicketFlow.slnx -c Release` (0 warnings, 0 errors); `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` (36 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` against an isolated `postgres:18` container (15 passed; without PostgreSQL these 15 are inconclusive); Access (95), Contacts (72), Workspaces (42), composition (6) and Access PostgreSQL (15) suites re-run green after adding the `conversation:manage` built-in. `bash scripts/verify-package-metadata.sh` currently fails only on E12's in-progress HelpCenter project. Parallel post-ticket reviews are still outstanding before commit.

### Post-review fixes (2026-11-22, after 18d5fce)

Review findings fixed (uncommitted), each with regression tests:

1. P1 masked values in command results: `ConversationApplied.Events` (including replays) are projected through masking for the actor's `conversation:read` decision on the resulting conversation (`ReviewRegressionTests.CommandResultsDoNotLeakMaskedAttributeValues`, `ReplayedInboundMessagesHideChannelIdentifiersFromActorsWithoutRead`).
2. P2 merge follower: shared `ContactRepointer` walks repeat full passes until a pass changes nothing, reuse listed snapshots and re-read only on conflict, honor cancellation; the in-process observer is best-effort (logged, swallowed) with `CatchUpAsync` as the durable path; `StartAsync` re-checks the contact after its write and re-points along the merge chain (`MergeRepointsConversationsAcrossManyPages`, `ActivityDuringTheWalkCannotHideAConversation`, `ObserverFailuresAreSwallowedAndCatchUpRepairs`, `StartRacingAMergeIsRepointed`).
3. P3 dispatcher: claims with `Attempt > MaximumAttempts` are dead-lettered without handling; claims whose lease ends within `LeaseSafetyMargin` are skipped (`DispatcherDeadLettersPoisonEntriesWithoutHandlingThem`, `DispatcherSkipsClaimsWhoseLeaseIsAboutToExpire`).
4. P3 inbound replay precedes the Closed check (`RedeliveredMessageAfterCloseIsReplayedNotRefused`).
5. P1 perf: Access batch API `IAccessEvaluator.LoadDecisionContextAsync(PrincipalRef, TicketScope, IEnumerable<Permission>, CancellationToken) -> Task<IAccessDecisionContext>` with `ValueTask<AccessDecision> DecideAsync(Permission, AccessResource, CancellationToken)` (default interface implementation `PointAccessDecisionContext`; `AccessEvaluator` loads grants/roles/policies once; sensitive decisions audited per resource; break-glass once per permission per context). Used by conversation lists and timelines (`ListPagesLoadAccessDataOncePerPage`; Access `DecisionContextTests`, 3 tests). Surveys list left to the agent fixing Surveys.
6. PostgreSQL migration 2: `tf_conversation_labels` side table (maintained in the write transaction, backfilled) driving label lists; compound `(inbox|assignee|team, status, activity)` indexes; outbox `(consumer, available_at, sequence) WHERE state = 0` index. Head tracking for claims was not added (the per-conversation `NOT EXISTS` probe is index-backed).
7. `PostgresReviewRegressionTests`: EXPLAIN of the exact list SQL over 10,000 seeded rows in 10 workspaces for every criterion with a cursor, scope-only and OR access filter (no sequential scans; criterion index used); label side-table maintenance; 10 conversations × 5 entries claimed by 8 claimers on two data sources with per-conversation ordering and one live claim.
8. Write path batched with `NpgsqlBatch` (events, label sync, message rows, outbox entry); the outbox append is one statement with its deliveries.

Verification: `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` (46 passed); `TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` against an isolated `postgres:18` container (18 passed); `tests/Access/TicketFlow.Access.Tests` (102 passed). Because other agents were mid-edit in Channels/Routing/SLA/EnvelopeFlow, consumers were verified in a detached worktree of HEAD (ee963dc) with only the E5/Access changes overlaid: Channels 35 + Api 10 + Postgres 6, Routing 41 + Postgres 11, SLA 26 + Postgres 6 — all passed. In the shared tree, `dotnet build TicketFlow.slnx -c Release` currently fails only in the in-progress `tests/Integrations/TicketFlow.Integrations.EnvelopeFlow.Tests` (another agent).
