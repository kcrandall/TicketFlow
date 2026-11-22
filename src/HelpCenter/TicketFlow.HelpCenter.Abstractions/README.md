# TicketFlow.HelpCenter.Abstractions

Provider-neutral model and contracts for TicketFlow's help center:

- `Portal` (slug, name, default and ≤ 32 supported locales, `PortalVisibility` Private/Public, optional custom domain reference),
  `HelpCollection` (hierarchical, ≤ 3 levels, parent fixed at creation, ordered by position) and `Article` with one
  `ArticleTranslation` per locale (title, slug, Markdown body ≤ 100,000 chars, excerpt, ≤ 16 tags).
- `ArticleRecord` (draft content, `ArticleState` Draft/Published/Archived, live revision, author/updated-by `AccessActor`s) and
  immutable `ArticleRevision`s (published snapshots with plain text for search).
- Unions `HelpCenterResult<T>`, `HelpCenterWriteResult<T>` and `ArticleFeedbackResult`. Switch over every case without a discard arm.
- Store contracts `IHelpCenterStore` (scope-confined, revision-fenced, capacity-bounded, atomic published read model and search)
  and `IArticleFeedbackStore` (one view per hashed visitor per UTC day, one vote per visitor).
- Services: `IHelpCenterService` (authorized administration), `IHelpCenterPublicReader` (anonymous, published content of public
  portals only), `IHelpCenterSearch` (deflection and assistant grounding) and `IArticleRenderer` (Markdown → sanitized HTML).
- `HelpCenterPermissions`: `helpcenter:read|write|publish|manage`; `HelpCenterResources` access snapshots;
  `HelpCenterSearchConfiguration` (locale → PostgreSQL text search configuration, fallback `simple`); `HelpCenterJsonCodec`
  (versioned persisted translations).

Behavior and development stores live in `TicketFlow.HelpCenter`; durable storage in `TicketFlow.HelpCenter.Stores.Postgres`.
