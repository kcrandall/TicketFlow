using System.Net;
using System.Text;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using Ganss.Xss;
using Markdig;

namespace TicketFlow.HelpCenter;

/// <summary>
/// Default <see cref="IArticleRenderer"/>: Markdig with raw HTML disabled (HTML in the source is rendered as text) and a restricted
/// extension set, followed by an HtmlSanitizer allow-list pass. Only structural tags survive, attributes are limited to
/// <c>href</c>, <c>src</c>, <c>alt</c>, <c>title</c> and <c>start</c>, URLs must be relative or use <c>http</c>, <c>https</c> or
/// <c>mailto</c> (<c>src</c>: <c>http</c>/<c>https</c>/relative only), no styles, classes, ids, data attributes or event handlers
/// survive, and every link gets <c>rel="nofollow noopener noreferrer"</c>. Input nested beyond Markdig's depth limit is shown as
/// escaped preformatted text. Thread-safe.
/// </summary>
public sealed class SafeMarkdownRenderer : IArticleRenderer
{
    private static readonly string[] Tags =
    [
        "a", "blockquote", "br", "code", "del", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "img", "li", "ol", "p", "pre", "strong",
        "table", "tbody", "td", "th", "thead", "tr", "ul", "sup", "sub",
    ];

    private readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .Build();

    private readonly HtmlSanitizer sanitizer;

    public SafeMarkdownRenderer()
    {
        var options = new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(Tags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(["href", "src", "alt", "title", "start"], StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(["http", "https", "mailto"], StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href", "src"], StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<CssRuleType>(),
            AllowedClasses = new HashSet<string>(StringComparer.Ordinal),
            AllowDataAttributes = false,
        };
        sanitizer = new HtmlSanitizer(options) { KeepChildNodes = true };
        sanitizer.FilterUrl += (_, e) =>
        {
            // Images may not use mailto: (or any non-web scheme).
            if (e.Tag?.NodeName.Equals("IMG", StringComparison.OrdinalIgnoreCase) == true && e.SanitizedUrl is { } url
                && Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is not ("http" or "https"))
                e.SanitizedUrl = null;
        };
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement element && element.NodeName.Equals("A", StringComparison.OrdinalIgnoreCase))
                element.SetAttribute("rel", "nofollow noopener noreferrer");
        };
    }

    public RenderedMarkdown Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (!HelpCenterText.IsMultiline(markdown, ArticleTranslation.MaximumBodyLength))
            throw new ArgumentException("Markdown must be bounded multi-line text.", nameof(markdown));
        string html;
        try
        {
            html = Markdown.ToHtml(markdown, pipeline);
        }
        catch (ArgumentException)
        {
            // Markdig refuses input nested beyond its depth limit; show such (pathological) content as escaped preformatted text.
            html = "<pre>" + WebUtility.HtmlEncode(markdown) + "</pre>";
        }
        return new RenderedMarkdown(sanitizer.Sanitize(html));
    }

    public string ToPlainText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        if (!HelpCenterText.IsMultiline(markdown, ArticleTranslation.MaximumBodyLength))
            throw new ArgumentException("Markdown must be bounded multi-line text.", nameof(markdown));
        string text;
        try
        {
            text = Markdown.ToPlainText(markdown, pipeline);
        }
        catch (ArgumentException)
        {
            text = markdown;
        }
        var limit = Math.Min(text.Length, ArticleTranslation.MaximumBodyLength);
        var clean = true;
        for (var i = 0; i < limit; i++)
        {
            var c = text[i];
            if (c is (< ' ' and not '\n' and not '\r' and not '\t') or (>= '\u007F' and <= '\u009F'))
            {
                clean = false;
                break;
            }
        }
        if (clean)
        {
            if (limit < text.Length && limit > 0 && char.IsHighSurrogate(text[limit - 1]))
                limit--;
            return limit == text.Length ? text.Trim() : text.AsSpan(0, limit).Trim().ToString();
        }
        var builder = new StringBuilder(limit);
        for (var i = 0; i < text.Length; i++)
        {
            if (builder.Length == ArticleTranslation.MaximumBodyLength)
                break;
            var character = text[i];
            if (character is (< ' ' and not '\n' and not '\r' and not '\t') or (>= '\u007F' and <= '\u009F'))
                continue;
            builder.Append(character);
        }
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
            builder.Length--;
        return builder.ToString().Trim();
    }
}
