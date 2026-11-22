using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace TicketFlow.HelpCenter;

/// <summary>
/// A bounded full-text search over published articles of one portal and locale: query text of at most
/// <see cref="MaximumQueryLength"/> characters, at most <see cref="MaximumLimit"/> hits, offset at most <see cref="MaximumOffset"/>.
/// </summary>
public sealed record HelpCenterSearchQuery
{
    public const int MaximumQueryLength = 256;
    public const int MaximumLimit = 50;
    public const int MaximumOffset = 200;

    public HelpCenterSearchQuery(PortalId portal, HelpCenterLocale locale, string text, int limit = 10, int offset = 0)
    {
        if (!portal.IsInitialized || !locale.IsInitialized)
            throw new ArgumentException("Portal and locale are required.");
        Text = HelpCenterText.RequireLine(text, MaximumQueryLength, nameof(text));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaximumLimit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, MaximumOffset);
        Portal = portal;
        Locale = locale;
        Limit = limit;
        Offset = offset;
    }

    public PortalId Portal { get; }
    public HelpCenterLocale Locale { get; }

    /// <summary>Visitor- or agent-supplied text; never logged.</summary>
    public string Text { get; }

    public int Limit { get; }
    public int Offset { get; }

    public override string ToString() => "HelpCenterSearchQuery { <redacted> }";
}

/// <summary>
/// A ranked search hit: identifiers, title, slug, excerpt and a plain-text snippet (no markup, at most
/// <see cref="MaximumSnippetLength"/> characters). Higher <see cref="Rank"/> is more relevant; ranks are comparable only within one
/// result list. Every text field is plain text that may contain characters such as '&lt;': HTML-encode it when rendering.
/// </summary>
public sealed record ArticleSnippet(
    ArticleId Article,
    PortalId Portal,
    HelpCenterLocale Locale,
    string Title,
    HelpCenterSlug Slug,
    string? Excerpt,
    string Snippet,
    double Rank,
    int RevisionNumber)
{
    public const int MaximumSnippetLength = 320;

    public override string ToString() => "ArticleSnippet { <redacted> }";
}

/// <summary>
/// Maps help center locales to PostgreSQL text search configurations by primary language, falling back to <c>simple</c>. Only names
/// from this fixed allow-list are ever sent to the database.
/// </summary>
public static class HelpCenterSearchConfiguration
{
    public const string Fallback = "simple";

    private static readonly FrozenDictionary<string, string> ByLanguage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ar"] = "arabic", ["hy"] = "armenian", ["eu"] = "basque", ["ca"] = "catalan", ["da"] = "danish", ["nl"] = "dutch",
        ["en"] = "english", ["fi"] = "finnish", ["fr"] = "french", ["de"] = "german", ["el"] = "greek", ["hi"] = "hindi",
        ["hu"] = "hungarian", ["id"] = "indonesian", ["ga"] = "irish", ["it"] = "italian", ["lt"] = "lithuanian", ["ne"] = "nepali",
        ["nb"] = "norwegian", ["nn"] = "norwegian", ["no"] = "norwegian", ["pt"] = "portuguese", ["ro"] = "romanian",
        ["ru"] = "russian", ["sr"] = "serbian", ["es"] = "spanish", ["sv"] = "swedish", ["ta"] = "tamil", ["tr"] = "turkish",
        ["yi"] = "yiddish",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every configuration name this map can return, including <see cref="Fallback"/>.</summary>
    public static IReadOnlySet<string> Names { get; } = ByLanguage.Values.Append(Fallback).ToFrozenSet(StringComparer.Ordinal);

    public static string ForLocale(HelpCenterLocale locale) =>
        locale.IsInitialized && ByLanguage.TryGetValue(locale.Language, out var name) ? name : Fallback;
}

/// <summary>Store-neutral helpers for naive (development) search and snippets.</summary>
public static class HelpCenterSearchText
{
    /// <summary>Maximum distinct query terms considered.</summary>
    public const int MaximumTerms = 32;

    /// <summary>Lowercase letter/digit runs of <paramref name="text"/> (culture-invariant, accents folded), at most <paramref name="limit"/> distinct terms.</summary>
    public static IReadOnlyList<string> Terms(string text, int limit = MaximumTerms)
    {
        ArgumentNullException.ThrowIfNull(text);
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0 && terms.Count < limit && seen.Add(current.ToString()))
                terms.Add(current.ToString());
            current.Clear();
        }
        foreach (var character in Fold(text))
        {
            if (char.IsLetterOrDigit(character))
                current.Append(char.ToLowerInvariant(character));
            else
                Flush();
        }
        Flush();
        return terms;
    }

    /// <summary>
    /// A plain-text window of at most <see cref="ArticleSnippet.MaximumSnippetLength"/> characters around the first matching term,
    /// with whitespace collapsed.
    /// </summary>
    public static string Snippet(string plainText, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentNullException.ThrowIfNull(terms);
        var collapsed = string.Join(' ', plainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var folded = Fold(collapsed).ToLowerInvariant();
        var start = 0;
        foreach (var term in terms)
        {
            var index = folded.IndexOf(term, StringComparison.Ordinal);
            if (index >= 0)
            {
                start = Math.Max(0, index - 80);
                break;
            }
        }
        // Folding is length-preserving for almost all text; clamp in case it is not.
        start = Math.Min(start, collapsed.Length);
        var length = Math.Min(ArticleSnippet.MaximumSnippetLength, collapsed.Length - start);
        if (length < collapsed.Length - start && length > 0 && char.IsHighSurrogate(collapsed[start + length - 1]))
            length--;
        if (start > 0 && char.IsLowSurrogate(collapsed[start]))
        {
            start++;
            length = Math.Max(0, length - 1);
        }
        return collapsed.Substring(start, length);
    }

    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
