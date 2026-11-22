# T01 — Help center model, permissions and safe Markdown rendering

Epic: [E12 — Help center and surveys](../PLAN.md)

Status: **Completed**

## Goal

`TicketFlow.HelpCenter.Abstractions`: portals (slug, name, custom domain reference, default and supported locales, visibility),
hierarchical ordered collections with bounded depth, articles with per-locale translations (title, slug, bounded Markdown body,
excerpt, tags), Draft/Published/Archived states with immutable published revisions, author/updated-by actors, union results and
`HelpCenterPermissions` (`helpcenter:read|write|publish|manage`). `TicketFlow.HelpCenter`: Markdig + HtmlSanitizer renderer that
turns Markdown into allow-listed HTML.

## Dependencies

E2 (Access), E3 (workspace conventions).

## Acceptance criteria

- Every text input is bounded and validated (`Utf16.IsValid`, no control characters); locales, slugs and tags are validated tokens.
- Raw HTML in Markdown is never passed through; output is sanitized with an allow-list (tags, attributes, `http`/`https`/`mailto`
  schemes); links get `rel="nofollow noopener noreferrer"`.
- XSS regression tests cover script tags, event handlers, `javascript:`/`data:` URLs, encoded schemes, SVG/iframe/style payloads
  and Markdown link/image injection.

## Verification

- `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release`.

## Risks and boundaries

Rendering happens on read so sanitizer updates apply to existing content; hosts may cache rendered output.

## Results

Implemented:

- `TicketFlow.HelpCenter.Abstractions`: `PortalId`, `CollectionId`, `ArticleId`, `HelpCenterSlug`, canonical `HelpCenterLocale`; `Portal`
  (visibility, ≤ 32 locales, custom domain reference), `HelpCollection` (fixed parent, depth ≤ 3), `Article`/`ArticleTranslation`
  (bounded, validated text), `ArticleRecord` (Draft/Published/Archived, live revision, `AccessActor` author/editor), immutable
  `ArticleRevision`, unions `HelpCenterResult<T>`/`HelpCenterWriteResult<T>`/`ArticleFeedbackResult`, versioned `HelpCenterJsonCodec`,
  `HelpCenterPermissions` (`helpcenter:read|write|publish|manage`), `HelpCenterResources`.
- `SafeMarkdownRenderer`: Markdig (`DisableHtml`, pipe tables, emphasis extras, autolinks) + HtmlSanitizer allow-list (tags,
  `href/src/alt/title/start`, `http/https/mailto`, `rel="nofollow noopener noreferrer"`), escaped fallback beyond Markdig's nesting limit.
  Central versions added: Markdig 1.5.1, HtmlSanitizer 10.0.1120.
- XSS tests (`RendererTests`): raw HTML payloads, event handlers, javascript/vbscript/data/file URLs incl. case/entity/percent-encoded
  and reference-link variants, title breakouts, pathological nesting (bounded time), with a tag/attribute/URL allow-list checker.

Verification commands (run from the repository root, 2026-11-22):

- `dotnet build TicketFlow.slnx -c Release` — Build succeeded, 0 Warning(s), 0 Error(s) (includes the concurrent E5 projects).
- `bash scripts/verify-package-metadata.sh` — Validated 22 package projects and central versions.
- `dotnet test tests/HelpCenter/TicketFlow.HelpCenter.Tests -c Release` — 67 passed, 0 failed.

Post-ticket parallel reviews (docs/agents/prompts/post-ticket-*.md) have not been run yet.
