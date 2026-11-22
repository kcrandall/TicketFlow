namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>A help-center portal; <see cref="Visibility"/> is <c>private</c> or <c>public</c>.</summary>
public sealed record PortalDto(string Id, string Slug, string Name, string DefaultLocale, IReadOnlyList<string> SupportedLocales, string Visibility, string? CustomDomain, long Revision);

public sealed record PortalRequest(string Slug, string Name, string DefaultLocale, IReadOnlyList<string>? SupportedLocales = null, string? Visibility = null, string? CustomDomain = null,
    string? Id = null);

public sealed record CollectionTranslationDto(string Locale, string Name, string Slug, string? Description = null);

public sealed record CollectionDto(string Id, string PortalId, string? ParentId, int Position, int Depth, IReadOnlyList<CollectionTranslationDto> Translations, long Revision);

public sealed record CollectionRequest(string PortalId, IReadOnlyList<CollectionTranslationDto> Translations, string? ParentId = null, int Position = 0, string? Id = null);

/// <summary>An article translation; <see cref="Body"/> is Markdown (rendered and sanitized server-side for readers).</summary>
public sealed record ArticleTranslationDto(string Locale, string Title, string Slug, string Body, string? Excerpt = null, IReadOnlyList<string>? Tags = null);

/// <summary>An article; <see cref="State"/> is <c>draft</c>, <c>published</c> or <c>archived</c>.</summary>
public sealed record ArticleDto(
    string Id,
    string PortalId,
    string? CollectionId,
    int Position,
    IReadOnlyList<ArticleTranslationDto> Translations,
    string State,
    int? PublishedRevision,
    ActorDto CreatedBy,
    DateTimeOffset CreatedAt,
    ActorDto UpdatedBy,
    DateTimeOffset UpdatedAt,
    long Revision);

public sealed record ArticleRequest(string PortalId, IReadOnlyList<ArticleTranslationDto> Translations, string? CollectionId = null, int Position = 0, string? Id = null);

public sealed record ArticleRevisionDto(string ArticleId, int Number, IReadOnlyList<ArticleTranslationDto> Translations, ActorDto PublishedBy, DateTimeOffset PublishedAt);

/// <summary>A rendered (sanitized HTML) preview of a translation.</summary>
public sealed record ArticlePreviewDto(string ArticleId, string Locale, string Title, string Html);

public sealed record ArticleFeedbackDto(long Views, long Helpful, long NotHelpful);

public sealed record ArticleSnippetDto(string ArticleId, string PortalId, string Locale, string Title, string Slug, string? Excerpt, string Snippet, double Rank, int RevisionNumber);

public sealed record RollbackRequest(int Revision);

public sealed record PublicPortalDto(string Id, string Slug, string Name, string DefaultLocale, IReadOnlyList<string> SupportedLocales);

public sealed record PublicCollectionDto(string Id, string? ParentId, int Position, string Locale, string Name, string Slug, string? Description);

public sealed record PublicArticleSummaryDto(string Id, string? CollectionId, int Position, string Title, string Slug, string? Excerpt, DateTimeOffset PublishedAt);

/// <summary>A published article; <see cref="Html"/> is sanitized server-side.</summary>
public sealed record PublicArticleDto(string Id, string PortalId, string? CollectionId, string Locale, string Title, string Slug, string? Excerpt, IReadOnlyList<string> Tags, string Html,
    int RevisionNumber, DateTimeOffset PublishedAt);

/// <summary>An anonymous view or feedback; <see cref="VisitorKey"/> is an opaque client-generated key used only for de-duplication.</summary>
public sealed record ArticleVisitRequest(string VisitorKey, bool? Helpful = null);
