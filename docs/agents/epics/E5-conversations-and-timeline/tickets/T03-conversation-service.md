# T03 — Conversation service: commands, timeline reads, masking and filtered lists

Epic: [E5 — Conversations and timeline](../PLAN.md)

Status: **Completed**

## Goal

`IConversationService`: start, incoming message (idempotent), reply (pending outbound message + outbox dispatch entry), note,
assign/unassign, team, status/snooze, priority, labels, attributes, participants, delivery status (idempotent, monotonic),
extension events and delete — each returning `ConversationCommandResult = Applied | NotFound | Conflict | Refused`; get, timeline
and list reads with masking; `IConversationAttributeDefinitionService`.

## Dependencies

E5 T02, E3 (inbox/team existence), E4 (contact existence).

## Acceptance criteria

- Every read and mutation authorizes through `IAccessEvaluator`; lists use `IAccessQueryPlanner` filters inside the store query.
- Notes are omitted from timelines unless `note:read`; LightAgent can note but not reply.
- Sensitive attributes and channel identifiers are masked in views and timeline events; a sensitive attribute with no policy fails
  closed.
- Commands without an expected revision retry store conflicts, so concurrent replies all apply with gapless sequences.

## Verification

- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release`.

## Risks and boundaries

Automation and system principals cannot hold grants; channel ingestion, SLA and automation workers act through grant-holding API
client principals (or trusted store access for internal followers).

## Results

Implemented `ConversationService` (all commands with the read → fence → authorize → decide → authorize-result → atomic write pipeline and conflict retry; reads with masking; filtered keyset lists with query-bound cursors), `ConversationMasking`, `ConversationAttributeDefinitionService`, `ConversationsOptions`, `AddConversations`/`AddInMemoryConversationStores`. Tests: `ConversationServiceTests` (13), including 24 concurrent replies with gapless sequences, LightAgent note-not-reply, notes hidden without `note:read`, sensitive attribute and channel masking, team-scoped list filtering.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes concurrent E12 work in the tree).
- `dotnet test tests/Conversations/TicketFlow.Conversations.Tests -c Release` — 36 passed, 0 failed.
- `TICKETFLOW_TEST_POSTGRES=Host=127.0.0.1;Port=55505;... TICKETFLOW_REQUIRE_POSTGRES=1 dotnet test tests/Conversations/TicketFlow.Conversations.Stores.Postgres.Tests -c Release` (isolated `postgres:18` container) — 15 passed, 0 failed.
- Regression after the Access built-in change: `dotnet test tests/Access/TicketFlow.Access.Tests -c Release` (95 passed), `tests/Contacts/TicketFlow.Contacts.Tests` (72 passed), `tests/Workspaces/TicketFlow.Workspaces.Tests` (42 passed), `tests/TicketFlow.Tests` (6 passed), `tests/Access/TicketFlow.Access.Stores.Postgres.Tests` against PostgreSQL (15 passed).
- `bash scripts/verify-package-metadata.sh` — fails only on E12's in-progress `src/HelpCenter/TicketFlow.HelpCenter` project not yet in the solution (not an E5 project).

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.

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
