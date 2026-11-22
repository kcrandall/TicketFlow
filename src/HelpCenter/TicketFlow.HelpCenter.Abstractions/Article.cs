using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>
/// An article's content in one locale: title, slug (unique among published articles per portal and locale), Markdown body of at
/// most <see cref="MaximumBodyLength"/> UTF-16 units, optional excerpt and up to <see cref="MaximumTags"/> tags. Bodies are
/// customer-facing content and are never logged.
/// </summary>
public sealed record ArticleTranslation
{
    public const int MaximumTitleLength = 256;
    public const int MaximumBodyLength = 100_000;
    public const int MaximumExcerptLength = 512;
    public const int MaximumTags = 16;
    public const int MaximumTagLength = 64;

    public ArticleTranslation(HelpCenterLocale locale, string title, HelpCenterSlug slug, string body, string? excerpt = null, IEnumerable<string>? tags = null)
    {
        if (!locale.IsInitialized || !slug.IsInitialized)
            throw new ArgumentException("Translation locale and slug are required.");
        ArgumentNullException.ThrowIfNull(body);
        if (!HelpCenterText.IsMultiline(body, MaximumBodyLength))
            throw new ArgumentException($"Bodies must contain at most {MaximumBodyLength} characters, no control characters other than line breaks and tabs, and well-formed Unicode.", nameof(body));
        Locale = locale;
        Title = HelpCenterText.RequireLine(title, MaximumTitleLength, nameof(title));
        Slug = slug;
        Body = body;
        Excerpt = excerpt is null ? null : HelpCenterText.RequireLine(excerpt, MaximumExcerptLength, nameof(excerpt));
        var snapshot = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags ?? [])
        {
            if (tag is null || !HelpCenterText.IsTag(tag, MaximumTagLength))
                throw new ArgumentException("Tags must be 1-64 lowercase ASCII letters, digits, '-', '_' or '.', starting with a letter or digit.", nameof(tags));
            if (snapshot.Add(tag) && snapshot.Count > MaximumTags)
                throw new ArgumentException($"At most {MaximumTags} tags are allowed.", nameof(tags));
        }
        Tags = [.. snapshot];
    }

    public HelpCenterLocale Locale { get; }
    public string Title { get; }
    public HelpCenterSlug Slug { get; }

    /// <summary>Markdown source. Rendered to sanitized HTML on read; raw HTML in the source is never passed through.</summary>
    public string Body { get; }

    public string? Excerpt { get; }

    /// <summary>Distinct tags ordered ordinally.</summary>
    public IReadOnlyList<string> Tags { get; }

    public bool Equals(ArticleTranslation? other) =>
        other is not null && Locale == other.Locale && Title == other.Title && Slug == other.Slug && Body == other.Body
        && Excerpt == other.Excerpt && Tags.SequenceEqual(other.Tags);

    public override int GetHashCode() => HashCode.Combine(Locale, Title, Slug, Body.Length, Excerpt, Tags.Count);

    public override string ToString() => "ArticleTranslation { <redacted> }";
}

/// <summary>
/// The editable (draft) content of an article: portal (fixed at creation), optional collection in the same portal, position among
/// siblings and one translation per supported locale.
/// </summary>
public sealed record Article
{
    public Article(ArticleId id, PortalId portal, IEnumerable<ArticleTranslation> translations, CollectionId? collection = null, int position = 0)
    {
        if (!id.IsInitialized || !portal.IsInitialized || collection is { IsInitialized: false })
            throw new ArgumentException("Article, portal and collection identifiers must be initialized.");
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, HelpCenterLimits.MaximumPosition);
        Id = id;
        Portal = portal;
        Collection = collection;
        Position = position;
        Translations = HelpCenterTranslations.Snapshot(translations, translation => translation.Locale, nameof(translations));
    }

    public ArticleId Id { get; }
    public PortalId Portal { get; }
    public CollectionId? Collection { get; }
    public int Position { get; }

    /// <summary>One translation per locale (at least one), ordered by locale.</summary>
    public IReadOnlyList<ArticleTranslation> Translations { get; }

    public ArticleTranslation? Translation(HelpCenterLocale locale) => Translations.FirstOrDefault(translation => translation.Locale == locale);

    public bool Equals(Article? other) =>
        other is not null && Id == other.Id && Portal == other.Portal && Collection == other.Collection && Position == other.Position
        && Translations.SequenceEqual(other.Translations);

    public override int GetHashCode() => HashCode.Combine(Id, Portal, Collection, Position, Translations.Count);

    public override string ToString() => "Article { <redacted> }";
}

public enum ArticleState
{
    /// <summary>Not visible to visitors. May still reference an earlier published revision (after unpublish).</summary>
    Draft = 1,

    /// <summary>The revision <see cref="ArticleRecord.PublishedRevision"/> is served to visitors.</summary>
    Published = 2,

    /// <summary>Retired: hidden from visitors and search, kept for history.</summary>
    Archived = 3,
}

/// <summary>
/// The persisted state of an article: draft content, lifecycle state, the live revision number and the author/last editor
/// (application principals as <see cref="AccessActor"/>, never credentials). A <see cref="ArticleState.Published"/> record must
/// reference a revision.
/// </summary>
public sealed record ArticleRecord
{
    public ArticleRecord(Article article, ArticleState state, int? publishedRevision, AccessActor createdBy, DateTimeOffset createdAt, AccessActor updatedBy, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(article);
        ArgumentNullException.ThrowIfNull(createdBy);
        ArgumentNullException.ThrowIfNull(updatedBy);
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (publishedRevision is < 1 or > HelpCenterLimits.MaximumRevisionsPerArticle)
            throw new ArgumentOutOfRangeException(nameof(publishedRevision));
        if (state == ArticleState.Published && publishedRevision is null)
            throw new ArgumentException("A published article must reference a revision.", nameof(publishedRevision));
        Article = article;
        State = state;
        PublishedRevision = publishedRevision;
        CreatedBy = createdBy;
        CreatedAt = HelpCenterLimits.Truncate(createdAt);
        UpdatedBy = updatedBy;
        UpdatedAt = HelpCenterLimits.Truncate(updatedAt);
    }

    public Article Article { get; }
    public ArticleState State { get; }

    /// <summary>The live (or, when not published, last live) revision number; <see langword="null"/> if never published.</summary>
    public int? PublishedRevision { get; }

    public AccessActor CreatedBy { get; }
    public DateTimeOffset CreatedAt { get; }
    public AccessActor UpdatedBy { get; }
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>An article record with its scope and optimistic-concurrency revision (first revision is 1).</summary>
public sealed record StoredArticle(TicketScope Scope, ArticleRecord Record, long Revision)
{
    public ArticleId Id => Record.Article.Id;
}

/// <summary>A published translation: the content plus its Markdown-stripped plain text used for search and snippets.</summary>
public sealed record PublishedTranslation
{
    public PublishedTranslation(ArticleTranslation content, string plainText)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(plainText);
        if (!HelpCenterText.IsMultiline(plainText, ArticleTranslation.MaximumBodyLength))
            throw new ArgumentException("Plain text must be bounded multi-line text.", nameof(plainText));
        Content = content;
        PlainText = plainText;
    }

    public ArticleTranslation Content { get; }
    public string PlainText { get; }

    public override string ToString() => "PublishedTranslation { <redacted> }";
}

/// <summary>
/// An immutable published revision. Numbers start at 1 and grow by one per publish or rollback; stores never modify or remove a
/// revision except when the article itself is deleted.
/// </summary>
public sealed record ArticleRevision
{
    public ArticleRevision(ArticleId article, int number, IEnumerable<PublishedTranslation> translations, AccessActor publishedBy, DateTimeOffset publishedAt)
    {
        if (!article.IsInitialized)
            throw new ArgumentException("An article identifier is required.", nameof(article));
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, HelpCenterLimits.MaximumRevisionsPerArticle);
        ArgumentNullException.ThrowIfNull(publishedBy);
        Article = article;
        Number = number;
        Translations = HelpCenterTranslations.Snapshot(translations, translation => translation.Content.Locale, nameof(translations));
        PublishedBy = publishedBy;
        PublishedAt = HelpCenterLimits.Truncate(publishedAt);
    }

    public ArticleId Article { get; }
    public int Number { get; }

    /// <summary>One translation per locale, ordered by locale.</summary>
    public IReadOnlyList<PublishedTranslation> Translations { get; }

    public AccessActor PublishedBy { get; }
    public DateTimeOffset PublishedAt { get; }

    public PublishedTranslation? Translation(HelpCenterLocale locale) => Translations.FirstOrDefault(translation => translation.Content.Locale == locale);

    public bool Equals(ArticleRevision? other) =>
        other is not null && Article == other.Article && Number == other.Number && PublishedBy == other.PublishedBy && PublishedAt == other.PublishedAt
        && Translations.SequenceEqual(other.Translations);

    public override int GetHashCode() => HashCode.Combine(Article, Number, PublishedAt, Translations.Count);
}

/// <summary>
/// One row of the public read model: a published translation of a published article, with the article's current collection and
/// position. Stores maintain these rows atomically with every article write.
/// </summary>
public sealed record PublishedArticle(
    TicketScope Scope,
    ArticleId Article,
    PortalId Portal,
    CollectionId? Collection,
    int Position,
    int RevisionNumber,
    DateTimeOffset PublishedAt,
    ArticleTranslation Content);

/// <summary>Capacity and size limits help center stores enforce atomically with writes.</summary>
public static class HelpCenterLimits
{
    public const int MaximumPortalsPerWorkspace = 32;
    public const int MaximumCollectionsPerPortal = 1000;
    public const int MaximumArticlesPerPortal = 10_000;
    public const int MaximumRevisionsPerArticle = 1000;
    public const int MaximumCollectionDepth = 3;
    public const int MaximumPosition = 1_000_000;
    public const int MaximumPageSize = 100;

    /// <summary>UTC with whole-microsecond precision, the resolution durable stores (PostgreSQL) keep.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
