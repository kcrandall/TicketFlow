# TicketFlow.HelpCenter

Help center behavior for TicketFlow.

- `HelpCenterService` (`IHelpCenterService`) authorizes every call through `IAccessEvaluator` (`helpcenter:read|write|publish|manage`)
  before touching a store; lists go through `IAccessQueryPlanner`. Publishing snapshots the draft into an immutable revision,
  rollback republishes an earlier revision as a new one, unpublish/archive hide the article — each in one atomic store write that
  also rebuilds the published read model.
- `HelpCenterPublicReader` (`IHelpCenterPublicReader`, `IHelpCenterSearch`): anonymous reads of published articles in public portals
  only (private portals, unsupported locales and drafts look missing), ranked search for widget deflection and assistant grounding,
  and abuse-resistant feedback (visitor keys are hashed; one view per visitor per UTC day, one vote per visitor).
- `SafeMarkdownRenderer` (`IArticleRenderer`): Markdig with raw HTML disabled, then an HtmlSanitizer allow-list (structural tags,
  `href`/`src`/`alt`/`title`/`start`, `http`/`https`/`mailto` URLs, no styles/classes/ids/handlers) and
  `rel="nofollow noopener noreferrer"` on links; input beyond Markdig's nesting limit renders as escaped preformatted text.
- `InMemoryHelpCenterStore`: bounded, process-local development store with naive term search.

```csharp
services.AddTicketFlow()
    .AddAccessControl().AddInMemoryAccessStores()
    .AddHelpCenter()
    .AddInMemoryHelpCenterStores(); // development only
```

Registrations use TryAdd; services are scoped. Use `TicketFlow.HelpCenter.Stores.Postgres` for durable storage and PostgreSQL
full-text search.
