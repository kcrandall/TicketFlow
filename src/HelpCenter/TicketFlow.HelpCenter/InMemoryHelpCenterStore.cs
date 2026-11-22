using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>
/// Development-only help center store implementing <see cref="IHelpCenterStore"/> and <see cref="IArticleFeedbackStore"/>. State is
/// process-local, bounded, lost on restart and never shared between processes; every operation runs under one lock. Search is a
/// naive all-terms match (title > tags/excerpt > body) without stemming or stop words, not linguistic full-text search.
/// </summary>
public sealed class InMemoryHelpCenterStore : IHelpCenterStore, IArticleFeedbackStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<TicketScope, ScopeState> scopes = [];
    private readonly int maximumRecords;
    private readonly int maximumFeedbackMarkers;
    private int records;
    private int markers;

    /// <param name="maximumRecords">Global cap on portals, collections, articles and revisions together.</param>
    /// <param name="maximumFeedbackMarkers">Global cap on view and vote dedupe markers; beyond it new views and votes are not counted.</param>
    public InMemoryHelpCenterStore(int maximumRecords = 100_000, int maximumFeedbackMarkers = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRecords, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFeedbackMarkers, 1);
        this.maximumRecords = maximumRecords;
        this.maximumFeedbackMarkers = maximumFeedbackMarkers;
    }

    private sealed class ScopeState
    {
        public SortedDictionary<PortalId, StoredPortal> Portals { get; } = [];
        public Dictionary<CollectionId, StoredCollection> Collections { get; } = [];
        public SortedDictionary<ArticleId, StoredArticle> Articles { get; } = [];
        public Dictionary<ArticleId, List<ArticleRevision>> Revisions { get; } = [];
        public Dictionary<ArticleId, List<PublishedRow>> Published { get; } = [];
        public Dictionary<ArticleId, Feedback> Feedback { get; } = [];
    }

    private sealed record PublishedRow(PublishedArticle Article, string PlainText);

    private sealed class Feedback
    {
        public long Views { get; set; }
        public long Helpful { get; set; }
        public long NotHelpful { get; set; }
        public Dictionary<VisitorHash, bool> Votes { get; } = [];
        public HashSet<(VisitorHash Visitor, DateOnly Day)> ViewMarkers { get; } = [];
        public ArticleFeedbackCounts Counts => new(Views, Helpful, NotHelpful);
    }

    // ---------------------------------------------------------------- portals

    public Task<StoredPortal?> GetPortalAsync(TicketScope scope, PortalId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        lock (gate)
            return Task.FromResult(Find(scope)?.Portals.GetValueOrDefault(id));
    }

    public Task<StoredPortal?> FindPortalBySlugAsync(TicketScope scope, HelpCenterSlug slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(slug.IsInitialized, nameof(slug));
        lock (gate)
            return Task.FromResult(Find(scope)?.Portals.Values.FirstOrDefault(portal => portal.Portal.Slug == slug));
    }

    public Task<IReadOnlyList<StoredPortal>> ListPortalsAsync(TicketScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (gate)
            return Task.FromResult<IReadOnlyList<StoredPortal>>(Find(scope) is { } state ? [.. state.Portals.Values] : []);
    }

    public Task<HelpCenterWriteResult<StoredPortal>> CreatePortalAsync(TicketScope scope, Portal portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(portal);
        lock (gate)
        {
            var state = State(scope);
            try
            {
                if (state.Portals.TryGetValue(portal.Id, out var existing))
                    return Result<StoredPortal>(new HelpCenterWriteConflict(existing.Revision));
                if (records >= maximumRecords || state.Portals.Count >= HelpCenterLimits.MaximumPortalsPerWorkspace)
                    return Result<StoredPortal>(new HelpCenterWriteRejected(HelpCenterWriteRejection.CapacityExceeded));
                if (state.Portals.Values.Any(other => other.Portal.Slug == portal.Slug))
                    return Result<StoredPortal>(new HelpCenterWriteRejected(HelpCenterWriteRejection.SlugConflict));
                var stored = new StoredPortal(scope, portal, 1);
                state.Portals.Add(portal.Id, stored);
                records++;
                return Result<StoredPortal>(new HelpCenterWriteApplied<StoredPortal>(stored));
            }
            finally
            {
                Prune(scope, state);
            }
        }
    }

    public Task<HelpCenterWriteResult<StoredPortal>> UpdatePortalAsync(TicketScope scope, Portal portal, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(portal);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Portals.TryGetValue(portal.Id, out var existing))
                return Result<StoredPortal>(HelpCenterWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredPortal>(new HelpCenterWriteConflict(existing.Revision));
            if (state.Portals.Values.Any(other => other.Portal.Id != portal.Id && other.Portal.Slug == portal.Slug))
                return Result<StoredPortal>(new HelpCenterWriteRejected(HelpCenterWriteRejection.SlugConflict));
            var stored = new StoredPortal(scope, portal, checked(existing.Revision + 1));
            state.Portals[portal.Id] = stored;
            return Result<StoredPortal>(new HelpCenterWriteApplied<StoredPortal>(stored));
        }
    }

    public Task<HelpCenterWriteResult<StoredPortal>> DeletePortalAsync(TicketScope scope, PortalId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Portals.TryGetValue(id, out var existing))
                return Result<StoredPortal>(HelpCenterWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredPortal>(new HelpCenterWriteConflict(existing.Revision));
            if (state.Collections.Values.Any(collection => collection.Collection.Portal == id) || state.Articles.Values.Any(article => article.Record.Article.Portal == id))
                return Result<StoredPortal>(new HelpCenterWriteRejected(HelpCenterWriteRejection.NotEmpty));
            state.Portals.Remove(id);
            records--;
            Prune(scope, state);
            return Result<StoredPortal>(new HelpCenterWriteApplied<StoredPortal>(existing));
        }
    }

    // ---------------------------------------------------------------- collections

    public Task<StoredCollection?> GetCollectionAsync(TicketScope scope, CollectionId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        lock (gate)
            return Task.FromResult(Find(scope)?.Collections.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<StoredCollection>> ListCollectionsAsync(TicketScope scope, PortalId portal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(portal.IsInitialized, nameof(portal));
        lock (gate)
        {
            if (Find(scope) is not { } state)
                return Task.FromResult<IReadOnlyList<StoredCollection>>([]);
            return Task.FromResult<IReadOnlyList<StoredCollection>>([.. state.Collections.Values
                .Where(collection => collection.Collection.Portal == portal)
                .OrderBy(collection => collection.Depth).ThenBy(collection => collection.Collection.Position).ThenBy(collection => collection.Collection.Id)]);
        }
    }

    public Task<HelpCenterWriteResult<StoredCollection>> CreateCollectionAsync(TicketScope scope, HelpCollection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(collection);
        lock (gate)
        {
            var state = State(scope);
            try
            {
                if (state.Collections.TryGetValue(collection.Id, out var existing))
                    return Result<StoredCollection>(new HelpCenterWriteConflict(existing.Revision));
                if (!state.Portals.ContainsKey(collection.Portal))
                    return Result<StoredCollection>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference));
                var depth = 1;
                if (collection.Parent is { } parentId)
                {
                    if (!state.Collections.TryGetValue(parentId, out var parent) || parent.Collection.Portal != collection.Portal)
                        return Result<StoredCollection>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference));
                    depth = parent.Depth + 1;
                    if (depth > HelpCenterLimits.MaximumCollectionDepth)
                        return Result<StoredCollection>(new HelpCenterWriteRejected(HelpCenterWriteRejection.DepthExceeded));
                }
                if (records >= maximumRecords || state.Collections.Values.Count(other => other.Collection.Portal == collection.Portal) >= HelpCenterLimits.MaximumCollectionsPerPortal)
                    return Result<StoredCollection>(new HelpCenterWriteRejected(HelpCenterWriteRejection.CapacityExceeded));
                var stored = new StoredCollection(scope, collection, depth, 1);
                state.Collections.Add(collection.Id, stored);
                records++;
                return Result<StoredCollection>(new HelpCenterWriteApplied<StoredCollection>(stored));
            }
            finally
            {
                Prune(scope, state);
            }
        }
    }

    public Task<HelpCenterWriteResult<StoredCollection>> UpdateCollectionAsync(TicketScope scope, HelpCollection collection, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Collections.TryGetValue(collection.Id, out var existing))
                return Result<StoredCollection>(HelpCenterWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredCollection>(new HelpCenterWriteConflict(existing.Revision));
            if (existing.Collection.Portal != collection.Portal || existing.Collection.Parent != collection.Parent)
                return Result<StoredCollection>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference));
            var stored = existing with { Collection = collection, Revision = checked(existing.Revision + 1) };
            state.Collections[collection.Id] = stored;
            return Result<StoredCollection>(new HelpCenterWriteApplied<StoredCollection>(stored));
        }
    }

    public Task<HelpCenterWriteResult<StoredCollection>> DeleteCollectionAsync(TicketScope scope, CollectionId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Collections.TryGetValue(id, out var existing))
                return Result<StoredCollection>(HelpCenterWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredCollection>(new HelpCenterWriteConflict(existing.Revision));
            if (state.Collections.Values.Any(other => other.Collection.Parent == id) || state.Articles.Values.Any(article => article.Record.Article.Collection == id))
                return Result<StoredCollection>(new HelpCenterWriteRejected(HelpCenterWriteRejection.NotEmpty));
            state.Collections.Remove(id);
            records--;
            return Result<StoredCollection>(new HelpCenterWriteApplied<StoredCollection>(existing));
        }
    }

    // ---------------------------------------------------------------- articles

    public Task<StoredArticle?> GetArticleAsync(TicketScope scope, ArticleId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        lock (gate)
            return Task.FromResult(Find(scope)?.Articles.GetValueOrDefault(id));
    }

    public Task<HelpCenterPage<StoredArticle>> ListArticlesAsync(TicketScope scope, ArticleListQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        lock (gate)
        {
            var items = new List<StoredArticle>();
            foreach (var (id, article) in Find(scope)?.Articles ?? [])
            {
                var record = article.Record;
                if (query.After is { } after && id.CompareTo(after) <= 0)
                    continue;
                if (record.Article.Portal != query.Portal || (query.Collection is { } collection && record.Article.Collection != collection) || (query.State is { } wanted && record.State != wanted))
                    continue;
                if (items.Count == query.Limit)
                    return Task.FromResult(new HelpCenterPage<StoredArticle>(items, true));
                items.Add(article);
            }
            return Task.FromResult(new HelpCenterPage<StoredArticle>(items, false));
        }
    }

    public Task<HelpCenterWriteResult<StoredArticle>> CreateArticleAsync(TicketScope scope, ArticleRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(record);
        lock (gate)
        {
            var state = State(scope);
            try
            {
                var article = record.Article;
                if (state.Articles.TryGetValue(article.Id, out var existing))
                    return Result<StoredArticle>(new HelpCenterWriteConflict(existing.Revision));
                if (record.State == ArticleState.Published || record.PublishedRevision is not null)
                    return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidRevision));
                if (InvalidReferences(state, article))
                    return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference));
                if (records >= maximumRecords || state.Articles.Values.Count(other => other.Record.Article.Portal == article.Portal) >= HelpCenterLimits.MaximumArticlesPerPortal)
                    return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.CapacityExceeded));
                var stored = new StoredArticle(scope, record, 1);
                state.Articles.Add(article.Id, stored);
                state.Revisions.Add(article.Id, []);
                records++;
                return Result<StoredArticle>(new HelpCenterWriteApplied<StoredArticle>(stored));
            }
            finally
            {
                Prune(scope, state);
            }
        }
    }

    public Task<HelpCenterWriteResult<StoredArticle>> UpdateArticleAsync(TicketScope scope, ArticleRecord record, long expectedRevision, ArticleRevision? appendRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            var article = record.Article;
            if (Find(scope) is not { } state || !state.Articles.TryGetValue(article.Id, out var existing))
                return Result<StoredArticle>(HelpCenterWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredArticle>(new HelpCenterWriteConflict(existing.Revision));
            if (existing.Record.Article.Portal != article.Portal || InvalidReferences(state, article))
                return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidReference));
            var revisions = state.Revisions[article.Id];
            if (appendRevision is not null)
            {
                if (appendRevision.Article != article.Id || appendRevision.Number != revisions.Count + 1)
                    return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidRevision));
                if (records >= maximumRecords)
                    return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.CapacityExceeded));
            }
            var available = revisions.Count + (appendRevision is null ? 0 : 1);
            if (record.PublishedRevision is { } live && live > available)
                return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.InvalidRevision));

            List<PublishedRow> rows = [];
            if (record.State == ArticleState.Published)
            {
                var revision = record.PublishedRevision == revisions.Count + 1 ? appendRevision! : revisions[record.PublishedRevision!.Value - 1];
                foreach (var translation in revision.Translations)
                {
                    var content = translation.Content;
                    var taken = state.Published.Where(pair => pair.Key != article.Id).SelectMany(pair => pair.Value)
                        .Any(row => row.Article.Portal == article.Portal && row.Article.Content.Locale == content.Locale && row.Article.Content.Slug == content.Slug);
                    if (taken)
                        return Result<StoredArticle>(new HelpCenterWriteRejected(HelpCenterWriteRejection.SlugConflict));
                    rows.Add(new(new PublishedArticle(scope, article.Id, article.Portal, article.Collection, article.Position, revision.Number, revision.PublishedAt, content), translation.PlainText));
                }
            }

            if (appendRevision is not null)
            {
                revisions.Add(appendRevision);
                records++;
            }
            if (rows.Count == 0)
                state.Published.Remove(article.Id);
            else
                state.Published[article.Id] = rows;
            var stored = new StoredArticle(scope, record, checked(existing.Revision + 1));
            state.Articles[article.Id] = stored;
            return Result<StoredArticle>(new HelpCenterWriteApplied<StoredArticle>(stored));
        }
    }

    public Task<HelpCenterWriteResult<StoredArticle>> DeleteArticleAsync(TicketScope scope, ArticleId id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(id.IsInitialized, nameof(id));
        ArgumentOutOfRangeException.ThrowIfLessThan(expectedRevision, 1);
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Articles.TryGetValue(id, out var existing))
                return Result<StoredArticle>(HelpCenterWriteNotFound.Instance);
            if (existing.Revision != expectedRevision)
                return Result<StoredArticle>(new HelpCenterWriteConflict(existing.Revision));
            state.Articles.Remove(id);
            records -= 1 + state.Revisions[id].Count;
            state.Revisions.Remove(id);
            state.Published.Remove(id);
            if (state.Feedback.Remove(id, out var feedback))
                markers -= feedback.Votes.Count + feedback.ViewMarkers.Count;
            return Result<StoredArticle>(new HelpCenterWriteApplied<StoredArticle>(existing));
        }
    }

    public Task<ArticleRevision?> GetRevisionAsync(TicketScope scope, ArticleId article, int number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized, nameof(article));
        lock (gate)
        {
            var revisions = Find(scope)?.Revisions.GetValueOrDefault(article);
            return Task.FromResult(revisions is not null && number >= 1 && number <= revisions.Count ? revisions[number - 1] : null);
        }
    }

    public Task<HelpCenterPage<ArticleRevision>> ListRevisionsAsync(TicketScope scope, ArticleId article, int limit, int? before = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized, nameof(article));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, HelpCenterLimits.MaximumPageSize);
        lock (gate)
        {
            var revisions = Find(scope)?.Revisions.GetValueOrDefault(article) ?? [];
            var candidates = revisions.Where(revision => before is null || revision.Number < before).OrderByDescending(revision => revision.Number).ToList();
            return Task.FromResult(new HelpCenterPage<ArticleRevision>([.. candidates.Take(limit)], candidates.Count > limit));
        }
    }

    // ---------------------------------------------------------------- published read model

    public Task<PublishedArticle?> GetPublishedAsync(TicketScope scope, PortalId portal, HelpCenterLocale locale, HelpCenterSlug slug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(portal.IsInitialized && locale.IsInitialized && slug.IsInitialized, nameof(portal));
        lock (gate)
            return Task.FromResult(Rows(scope).FirstOrDefault(row => row.Article.Portal == portal && row.Article.Content.Locale == locale && row.Article.Content.Slug == slug)?.Article);
    }

    public Task<PublishedArticle?> GetPublishedByIdAsync(TicketScope scope, ArticleId article, HelpCenterLocale locale, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized && locale.IsInitialized, nameof(article));
        lock (gate)
            return Task.FromResult(Find(scope)?.Published.GetValueOrDefault(article)?.FirstOrDefault(row => row.Article.Content.Locale == locale)?.Article);
    }

    public Task<PublishedArticlePage> ListPublishedAsync(TicketScope scope, PublishedArticleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        lock (gate)
        {
            var ordered = Rows(scope)
                .Select(row => row.Article)
                .Where(row => row.Portal == query.Portal && row.Content.Locale == query.Locale && (query.Collection is null || row.Collection == query.Collection))
                .Where(row => query.After is not { } after || row.Position > after.Position || (row.Position == after.Position && row.Article.CompareTo(after.Article) > 0))
                .OrderBy(row => row.Position).ThenBy(row => row.Article)
                .Take(query.Limit + 1)
                .ToList();
            if (ordered.Count <= query.Limit)
                return Task.FromResult(new PublishedArticlePage(ordered, null));
            var page = ordered[..query.Limit];
            return Task.FromResult(new PublishedArticlePage(page, new PublishedArticleCursor(page[^1].Position, page[^1].Article)));
        }
    }

    public Task<IReadOnlyList<ArticleSnippet>> SearchAsync(TicketScope scope, HelpCenterSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        var terms = HelpCenterSearchText.Terms(query.Text);
        if (terms.Count == 0)
            return Task.FromResult<IReadOnlyList<ArticleSnippet>>([]);
        lock (gate)
        {
            var hits = new List<(ArticleSnippet Snippet, double Score)>();
            foreach (var row in Rows(scope))
            {
                var article = row.Article;
                if (article.Portal != query.Portal || article.Content.Locale != query.Locale)
                    continue;
                var title = HelpCenterSearchText.Terms(article.Content.Title, int.MaxValue).ToHashSet(StringComparer.Ordinal);
                var tags = HelpCenterSearchText.Terms(string.Join(' ', article.Content.Tags), int.MaxValue).ToHashSet(StringComparer.Ordinal);
                var excerpt = HelpCenterSearchText.Terms(article.Content.Excerpt ?? string.Empty, int.MaxValue).ToHashSet(StringComparer.Ordinal);
                var body = HelpCenterSearchText.Terms(row.PlainText, int.MaxValue).ToHashSet(StringComparer.Ordinal);
                // Every term must match somewhere (AND, like websearch_to_tsquery); weights favour title, then tags/excerpt, then body.
                double score = 0;
                var all = true;
                foreach (var term in terms)
                {
                    var weight = (title.Contains(term) ? 1.0 : 0) + (tags.Contains(term) || excerpt.Contains(term) ? 0.4 : 0) + (body.Contains(term) ? 0.1 : 0);
                    all &= weight > 0;
                    score += weight;
                }
                if (all)
                    hits.Add((new ArticleSnippet(article.Article, article.Portal, article.Content.Locale, article.Content.Title, article.Content.Slug, article.Content.Excerpt,
                        HelpCenterSearchText.Snippet(row.PlainText, terms), score, article.RevisionNumber), score));
            }
            return Task.FromResult<IReadOnlyList<ArticleSnippet>>([.. hits
                .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Snippet.Article)
                .Skip(query.Offset).Take(query.Limit).Select(hit => hit.Snippet)]);
        }
    }

    // ---------------------------------------------------------------- feedback

    public Task<ArticleFeedbackCounts?> GetCountsAsync(TicketScope scope, ArticleId article, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized, nameof(article));
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Articles.ContainsKey(article))
                return Task.FromResult<ArticleFeedbackCounts?>(null);
            return Task.FromResult<ArticleFeedbackCounts?>(state.Feedback.GetValueOrDefault(article)?.Counts ?? ArticleFeedbackCounts.Empty);
        }
    }

    public Task<ArticleFeedbackResult> RecordViewAsync(TicketScope scope, ArticleId article, VisitorHash visitor, DateOnly day, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized && visitor.IsInitialized, nameof(article));
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Articles.ContainsKey(article))
                return Task.FromResult<ArticleFeedbackResult>(FeedbackArticleNotFound.Instance);
            var feedback = FeedbackFor(state, article);
            if (feedback.ViewMarkers.Contains((visitor, day)) || markers >= maximumFeedbackMarkers)
                return Task.FromResult<ArticleFeedbackResult>(new FeedbackUnchanged(feedback.Counts));
            feedback.ViewMarkers.Add((visitor, day));
            markers++;
            feedback.Views++;
            return Task.FromResult<ArticleFeedbackResult>(new FeedbackCounted(feedback.Counts));
        }
    }

    public Task<ArticleFeedbackResult> RecordVoteAsync(TicketScope scope, ArticleId article, VisitorHash visitor, bool helpful, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Require(article.IsInitialized && visitor.IsInitialized, nameof(article));
        lock (gate)
        {
            if (Find(scope) is not { } state || !state.Articles.ContainsKey(article))
                return Task.FromResult<ArticleFeedbackResult>(FeedbackArticleNotFound.Instance);
            var feedback = FeedbackFor(state, article);
            if (feedback.Votes.TryGetValue(visitor, out var previous))
            {
                if (previous == helpful)
                    return Task.FromResult<ArticleFeedbackResult>(new FeedbackUnchanged(feedback.Counts));
                if (previous)
                    feedback.Helpful--;
                else
                    feedback.NotHelpful--;
            }
            else if (markers >= maximumFeedbackMarkers)
            {
                return Task.FromResult<ArticleFeedbackResult>(new FeedbackUnchanged(feedback.Counts));
            }
            else
            {
                markers++;
            }
            feedback.Votes[visitor] = helpful;
            if (helpful)
                feedback.Helpful++;
            else
                feedback.NotHelpful++;
            return Task.FromResult<ArticleFeedbackResult>(new FeedbackCounted(feedback.Counts));
        }
    }

    public Task<int> PurgeViewMarkersAsync(TicketScope scope, DateOnly before, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 10_000);
        lock (gate)
        {
            var removed = 0;
            foreach (var feedback in (IEnumerable<Feedback>?)Find(scope)?.Feedback.Values ?? [])
            {
                foreach (var marker in feedback.ViewMarkers.Where(marker => marker.Day < before).Take(limit - removed).ToList())
                {
                    feedback.ViewMarkers.Remove(marker);
                    removed++;
                }
                if (removed == limit)
                    break;
            }
            markers -= removed;
            return Task.FromResult(removed);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Task<HelpCenterWriteResult<T>> Result<T>(HelpCenterWriteResult<T> result) => Task.FromResult(result);

    private static void Require(bool initialized, string parameterName)
    {
        if (!initialized)
            throw new ArgumentException("Identifiers must be initialized.", parameterName);
    }

    private static bool InvalidReferences(ScopeState state, Article article) =>
        !state.Portals.ContainsKey(article.Portal)
        || (article.Collection is { } collection && (!state.Collections.TryGetValue(collection, out var stored) || stored.Collection.Portal != article.Portal));

    private static Feedback FeedbackFor(ScopeState state, ArticleId article)
    {
        if (!state.Feedback.TryGetValue(article, out var feedback))
        {
            feedback = new Feedback();
            state.Feedback.Add(article, feedback);
        }
        return feedback;
    }

    private IEnumerable<PublishedRow> Rows(TicketScope scope) => Find(scope)?.Published.Values.SelectMany(rows => rows) ?? [];

    private ScopeState? Find(TicketScope scope) => scopes.GetValueOrDefault(scope);

    private ScopeState State(TicketScope scope)
    {
        if (!scopes.TryGetValue(scope, out var state))
        {
            state = new ScopeState();
            scopes.Add(scope, state);
        }
        return state;
    }

    private void Prune(TicketScope scope, ScopeState state)
    {
        if (state.Portals.Count == 0 && state.Collections.Count == 0 && state.Articles.Count == 0)
            scopes.Remove(scope);
    }
}
