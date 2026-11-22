using System.Security.Cryptography;
using System.Text.Json;
using TicketFlow.Core;

namespace TicketFlow.HelpCenter;

/// <summary>
/// The hashed identity of an anonymous visitor: lowercase hex SHA-256 of the JSON array [tenant, project, environment, app,
/// workspace, visitor key]. Stores only ever see this hash, never the raw visitor key.
/// </summary>
public readonly record struct VisitorHash
{
    private readonly string? value;

    private VisitorHash(string value) => this.value = value;

    /// <exception cref="InvalidOperationException">The hash is <c>default</c>.</exception>
    public string Value => value ?? throw new InvalidOperationException("The VisitorHash is uninitialized.");

    public bool IsInitialized => value is not null;

    /// <summary>Hashes an application-supplied visitor key (cookie, widget visitor ID, ...) of 8-256 characters within <paramref name="scope"/>.</summary>
    public static VisitorHash Create(TicketScope scope, string visitorKey)
    {
        ArgumentNullException.ThrowIfNull(scope);
        TicketScope.RequireIdentifier(visitorKey, nameof(visitorKey));
        if (visitorKey.Length < 8)
            throw new ArgumentException("Visitor keys must contain at least 8 characters.", nameof(visitorKey));
        var framed = JsonSerializer.SerializeToUtf8Bytes(new[] { scope.TenantId, scope.ProjectId, scope.EnvironmentId, scope.AppId, scope.WorkspaceId.Value, visitorKey });
        return new(Convert.ToHexStringLower(SHA256.HashData(framed)));
    }

    /// <summary>Restores a stored hash (64 lowercase hex characters).</summary>
    public static VisitorHash FromStored(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 64 || !value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new FormatException("A visitor hash must be 64 lowercase hex characters.");
        return new(value);
    }

    public override string ToString() => "VisitorHash { <redacted> }";
}

/// <summary>Aggregated counters of one article: distinct-visitor daily views and helpful / not helpful votes.</summary>
public sealed record ArticleFeedbackCounts(long Views, long Helpful, long NotHelpful)
{
    public static ArticleFeedbackCounts Empty { get; } = new(0, 0, 0);
}

/// <summary>The view or vote changed the counters.</summary>
public sealed record FeedbackCounted(ArticleFeedbackCounts Counts);

/// <summary>Duplicate: the visitor already viewed the article that day, or already cast the same vote. Counters are unchanged.</summary>
public sealed record FeedbackUnchanged(ArticleFeedbackCounts Counts);

/// <summary>The article does not exist (or, on public paths, is not published in a public portal).</summary>
public sealed record FeedbackArticleNotFound
{
    private FeedbackArticleNotFound()
    {
    }

    public static FeedbackArticleNotFound Instance { get; } = new();
}

/// <summary>Outcome of recording a view or vote. Switch over every case without a discard arm.</summary>
public union ArticleFeedbackResult(FeedbackCounted, FeedbackUnchanged, FeedbackArticleNotFound);
