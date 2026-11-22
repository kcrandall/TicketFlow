namespace TicketFlow.Hosting.AspNetCore.Contracts;

/// <summary>A help-center portal; <see cref="Visibility"/> is <c>private</c> or <c>public</c>.</summary>
public sealed record PortalResponse(string Id, string Slug, string Name, string DefaultLocale, IReadOnlyList<string> SupportedLocales, string Visibility, string? CustomDomain, long Revision);

public sealed record PortalRequest(string Slug, string Name, string DefaultLocale, IReadOnlyList<string>? SupportedLocales = null, string? Visibility = null, string? CustomDomain = null,
    string? Id = null);

public sealed record CollectionTranslationModel(string Locale, string Name, string Slug, string? Description = null);

public sealed record CollectionResponse(string Id, string PortalId, string? ParentId, int Position, int Depth, IReadOnlyList<CollectionTranslationModel> Translations, long Revision);

public sealed record CollectionRequest(string PortalId, IReadOnlyList<CollectionTranslationModel> Translations, string? ParentId = null, int Position = 0, string? Id = null);

/// <summary>An article translation; <see cref="Body"/> is Markdown (rendered and sanitized server-side for readers).</summary>
public sealed record ArticleTranslationModel(string Locale, string Title, string Slug, string Body, string? Excerpt = null, IReadOnlyList<string>? Tags = null);

/// <summary>An article; <see cref="State"/> is <c>draft</c>, <c>published</c> or <c>archived</c>.</summary>
public sealed record ArticleResponse(
    string Id,
    string PortalId,
    string? CollectionId,
    int Position,
    IReadOnlyList<ArticleTranslationModel> Translations,
    string State,
    int? PublishedRevision,
    ActorResponse CreatedBy,
    DateTimeOffset CreatedAt,
    ActorResponse UpdatedBy,
    DateTimeOffset UpdatedAt,
    long Revision);

public sealed record ArticleRequest(string PortalId, IReadOnlyList<ArticleTranslationModel> Translations, string? CollectionId = null, int Position = 0, string? Id = null);

public sealed record ArticleRevisionResponse(string ArticleId, int Number, IReadOnlyList<ArticleTranslationModel> Translations, ActorResponse PublishedBy, DateTimeOffset PublishedAt);

/// <summary>A rendered (sanitized HTML) preview of a translation.</summary>
public sealed record ArticlePreviewResponse(string ArticleId, string Locale, string Title, string Html);

public sealed record ArticleFeedbackResponse(long Views, long Helpful, long NotHelpful);

public sealed record ArticleSnippetResponse(string ArticleId, string PortalId, string Locale, string Title, string Slug, string? Excerpt, string Snippet, double Rank, int RevisionNumber);

public sealed record RollbackRequest(int Revision);

public sealed record PublicPortalResponse(string Id, string Slug, string Name, string DefaultLocale, IReadOnlyList<string> SupportedLocales);

public sealed record PublicCollectionResponse(string Id, string? ParentId, int Position, string Locale, string Name, string Slug, string? Description);

public sealed record PublicArticleSummaryResponse(string Id, string? CollectionId, int Position, string Title, string Slug, string? Excerpt, DateTimeOffset PublishedAt);

/// <summary>A published article; <see cref="Html"/> is sanitized server-side.</summary>
public sealed record PublicArticleResponse(string Id, string PortalId, string? CollectionId, string Locale, string Title, string Slug, string? Excerpt, IReadOnlyList<string> Tags, string Html,
    int RevisionNumber, DateTimeOffset PublishedAt);

/// <summary>An anonymous view or feedback; <see cref="VisitorKey"/> is an opaque client-generated key used only for de-duplication.</summary>
public sealed record ArticleVisitRequest(string VisitorKey, bool? Helpful = null);
