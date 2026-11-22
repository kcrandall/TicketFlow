# Help center

The help center ships as three packages:

| Package | Contents |
| --- | --- |
| `TicketFlow.HelpCenter.Abstractions` | Portals, collections, articles, revisions, published read model, search and feedback types, unions, `IHelpCenterStore`, `IArticleFeedbackStore`, service contracts, `HelpCenterPermissions`. |
| `TicketFlow.HelpCenter` | `HelpCenterService`, `HelpCenterPublicReader` (public reads + `IHelpCenterSearch`), `SafeMarkdownRenderer`, `InMemoryHelpCenterStore`, DI. |
| `TicketFlow.HelpCenter.Stores.Postgres` | `PostgresHelpCenterStore`, `PostgresHelpCenterMigrations` (component `ticketflow.helpcenter`), per-locale full-text search. |

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddPostgresAccessStores(dataSource)
    .AddHelpCenter()
    .AddPostgresHelpCenterStores(dataSource);      // or .AddInMemoryHelpCenterStores() for development
await new PostgresHelpCenterStore(dataSource).InitializeAsync();
```

## Model

- **Portal** — per workspace: `PortalId`, `HelpCenterSlug` (unique per workspace), name, default locale plus up to 32 supported
  locales, `PortalVisibility` (Private/Public) and an optional custom domain *reference* (a lowercase host name the application routes;
  TicketFlow does not verify or serve domains). At most 32 portals per workspace.
- **Collection** — `HelpCollection` inside a portal with one `CollectionTranslation` (name, slug, description) per locale, an
  optional parent fixed at creation (so cycles cannot form), depth ≤ 3 and ordering by (position, identifier). At most 1000 per
  portal. Collections with children or articles cannot be deleted; nor can portals with collections or articles.
- **Article** — `Article` is the editable draft: portal (fixed), optional collection of the same portal, position and one
  `ArticleTranslation` per locale (title ≤ 256, slug, Markdown body ≤ 100,000 UTF-16 units, excerpt ≤ 512, ≤ 16 tags). Every text
  input is validated (well-formed UTF-16, no control characters; bodies allow line breaks and tabs). At most 10,000 per portal.
- **Lifecycle** — `ArticleRecord` adds `ArticleState` (Draft/Published/Archived), the live revision number and author / last editor
  (`AccessActor`, never credentials). Publishing snapshots the draft into an immutable `ArticleRevision` (numbers 1…1000, with
  Markdown-stripped plain text for search) and serves it. Editing a published article changes only the draft; the live revision
  keeps serving until the next publish. Rollback republishes an earlier revision's content as a *new* revision and resets the draft,
  so history is append-only. Unpublish returns to Draft (revisions kept); archive hides the article; restore returns an archived
  article to Draft. Publishing requires a translation in the portal's default locale, and every translation must use a supported
  locale.
- **Published read model** — `PublishedArticle` rows (one per translation of the live revision, with the article's current
  collection and position). Stores rebuild them atomically in the same write that changes the article; published slugs are unique
  per (portal, locale), so a conflicting publish is rejected (`SlugConflict`) and nothing is written.

Timestamps are UTC with whole-microsecond precision. Stored translations use the versioned `HelpCenterJsonCodec` shape
(`{"v":1,"items":[...]}`); undecodable rows throw.

## Access

`AddHelpCenter()` registers `helpcenter:read`, `helpcenter:write`, `helpcenter:publish` and `helpcenter:manage` (sensitive) in the
permission catalog. The built-in Owner holds them; other built-in roles do not, so grant them through custom roles.

| Operation | Permission |
| --- | --- |
| Read portals, collections, articles, revisions, previews, feedback counts; agent search | `helpcenter:read` |
| Create/update/delete collections; create/update article drafts | `helpcenter:write` |
| Publish, unpublish, archive, restore, roll back | `helpcenter:publish` |
| Create/update/delete portals (including visibility); delete articles | `helpcenter:manage` |

Help center records carry no team, inbox, assignee, labels or attributes (`HelpCenterResources`), so every record in a workspace has
the same access shape: point checks use it, and list calls plan an `AccessFilter` through `IAccessQueryPlanner` and evaluate it
once against that shape before reading (exact, never a post-filter). Label and attribute conditions fail closed.

## Public reads

`IHelpCenterPublicReader` serves anonymous visitors within a host-resolved `TicketScope`, addressing portals by slug. Only published
articles of **public** portals in **supported** locales are visible; private portals, unsupported locales, drafts and archived
articles are indistinguishable from missing (`null` / empty). Collections fall back to the portal's default-locale translation.
Article HTML is always rendered from the live revision through `IArticleRenderer`.

## Safe rendering

`SafeMarkdownRenderer` converts Markdown with Markdig (raw HTML disabled — HTML in the source is shown as text — plus pipe tables,
emphasis extras and autolinks) and then sanitizes with HtmlSanitizer using an allow-list:

- tags: `p br hr h1–h6 strong em del code pre blockquote ul ol li a img table thead tbody tr th td sup sub`;
- attributes: `href src alt title start` (no `style`, `class`, `id`, `data-*` or event handlers);
- URLs: relative, `http`, `https` or `mailto` (`mailto` not allowed for images);
- every link gets `rel="nofollow noopener noreferrer"`.

Input beyond Markdig's nesting limit renders as escaped preformatted text. Rendering happens on read so sanitizer fixes apply to
existing content; hosts may cache output. Regression tests cover script/iframe/SVG/style/form/meta payloads, event handlers,
`javascript:`/`vbscript:`/`data:`/`file:` URLs including mixed-case, entity- and percent-encoded variants, reference links, title
attribute breakouts and pathological nesting.

## Search

`IHelpCenterSearch` is the seam for widget deflection (E7) and assistant grounding (E13):

```csharp
Task<HelpCenterResult<IReadOnlyList<ArticleSnippet>>> SearchAsync(PrincipalRef actor, HelpCenterSearchQuery query, CancellationToken ct = default); // helpcenter:read, any portal
Task<IReadOnlyList<ArticleSnippet>> SearchPublicAsync(TicketScope scope, HelpCenterSearchQuery query, CancellationToken ct = default);           // public portals only
```

`HelpCenterSearchQuery(portal, locale, text, limit = 10, offset = 0)` bounds text to 256 characters, limit to 50 and offset to 200.
`ArticleSnippet` returns identifiers, title, slug, excerpt, a plain-text snippet (≤ 320 characters, no markup — HTML-encode when
rendering), rank and revision number. Search reads only the published read model.

- **PostgreSQL**: each published row has a stored weighted `tsvector` (title A; tags and excerpt B; body plain text C) computed with
  the row's `regconfig`. `HelpCenterSearchConfiguration` maps the locale's language to a built-in configuration (`english`,
  `german`, `portuguese`, … 30 languages) with fallback `simple`; names come from that fixed allow-list and are bound as parameters.
  Queries use `websearch_to_tsquery` (quoted phrases, `-` negation, AND by default), `ts_rank_cd` ranking, a GIN index, bounded
  `LIMIT`/`OFFSET` and `ts_headline` plain-text snippets.
- **In-memory**: a naive all-terms match (title > tags/excerpt > body) without stemming or stop words, for development only.

## Feedback

`RecordViewAsync` counts one view per visitor per UTC day; `RecordFeedbackAsync` records one helpful / not-helpful vote per visitor
(repeating it is `FeedbackUnchanged`, changing it moves the count). Visitor keys (8–256 characters, application-supplied, e.g. a
cookie or widget visitor ID) are hashed per scope (`VisitorHash`, SHA-256) before reaching a store. Counters change atomically with
the dedupe record and cascade with the article. `PurgeViewMarkersAsync(scope, before, limit)` removes old daily view markers in
bounded batches. Hosts should still rate-limit anonymous endpoints.

## Store contract

`IHelpCenterStore` and `IArticleFeedbackStore` document the required guarantees (scope confinement, creates at revision 1,
compare-and-set updates/deletes, capacity limits, reference/depth/emptiness checks, append-only revisions, atomic read-model rebuild,
unique slugs, cascading deletes, bounded ranked search, idempotent feedback). The shared fixture
`tests/HelpCenter/TicketFlow.HelpCenter.StoreContracts` verifies them against the in-memory store and PostgreSQL (two independent
data sources). The PostgreSQL store serializes structural writes per workspace on a transaction-scoped advisory lock, keys every
row with `tf_key_v1` SHA-256 keys and compares raw columns.
