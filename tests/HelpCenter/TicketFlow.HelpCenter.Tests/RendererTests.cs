using System.Diagnostics;

namespace TicketFlow.HelpCenter.Tests;

[TestClass]
public sealed class RendererTests
{
    private static readonly SafeMarkdownRenderer Renderer = new();

    private static string Html(string markdown) => Renderer.Render(markdown).Html;

    [TestMethod]
    public void RendersStructuralMarkdown()
    {
        var html = Html("# Title\n\nSome **bold** and *em* and `code` and ~~gone~~.\n\n- one\n- two\n\n1. first\n\n> quote\n\n```\nblock\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n[docs](https://example.com/docs \"Docs\") ![logo](https://example.com/logo.png)");
        StringAssert.Contains(html, "<h1>Title</h1>");
        StringAssert.Contains(html, "<strong>bold</strong>");
        StringAssert.Contains(html, "<em>em</em>");
        StringAssert.Contains(html, "<code>code</code>");
        StringAssert.Contains(html, "<del>gone</del>");
        StringAssert.Contains(html, "<li>one</li>");
        StringAssert.Contains(html, "<blockquote>");
        StringAssert.Contains(html, "<pre><code>block");
        StringAssert.Contains(html, "<table>");
        StringAssert.Contains(html, "<a href=\"https://example.com/docs\" title=\"Docs\" rel=\"nofollow noopener noreferrer\">docs</a>");
        StringAssert.Contains(html, "<img src=\"https://example.com/logo.png\" alt=\"logo\">");
    }

    [TestMethod]
    [DataRow("<script>alert(1)</script>")]
    [DataRow("<img src=x onerror=alert(1)>")]
    [DataRow("<svg onload=alert(1)><circle/></svg>")]
    [DataRow("<iframe src=\"https://evil.example\"></iframe>")]
    [DataRow("<style>body{background:url(javascript:alert(1))}</style>")]
    [DataRow("<a href=\"javascript:alert(1)\">x</a>")]
    [DataRow("<div style=\"background:url(javascript:alert(1))\">x</div>")]
    [DataRow("<object data=\"evil.swf\"></object><embed src=\"evil.swf\">")]
    [DataRow("<math><mi xlink:href=\"javascript:alert(1)\">x</mi></math>")]
    [DataRow("<form action=\"https://evil.example\"><input name=password></form>")]
    [DataRow("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">")]
    [DataRow("<!-- <script>alert(1)</script> -->")]
    public void RawHtmlIsNeverPassedThrough(string payload)
    {
        var html = Html("Intro\n\n" + payload + "\n\nInline " + payload + " end.");
        AssertSafe(html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<meta", html, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [DataRow("[click](javascript:alert(1))")]
    [DataRow("[click](JaVaScRiPt:alert(1))")]
    [DataRow("[click](java\tscript:alert(1))")]
    [DataRow("[click](&#106;avascript:alert(1))")]
    [DataRow("[click](javascript&#58;alert(1))")]
    [DataRow("[click](%6A%61%76%61script:alert(1))")]
    [DataRow("[click](vbscript:msgbox(1))")]
    [DataRow("[click](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [DataRow("[click](file:///etc/passwd)")]
    [DataRow("<javascript:alert(1)>")]
    [DataRow("![img](javascript:alert(1))")]
    [DataRow("![img](data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+)")]
    [DataRow("![img](mailto:someone@example.com)")]
    [DataRow("[click][ref]\n\n[ref]: javascript:alert(1)")]
    [DataRow("[x](https://example.com \"title\\\" onmouseover=\\\"alert(1)\")")]
    public void UnsafeUrlsAreRemoved(string markdown)
    {
        var html = Html(markdown);
        AssertSafe(html);
        foreach (var scheme in new[] { "javascript:", "vbscript:", "data:", "file:" })
            Assert.DoesNotContain("=\"" + scheme, html.Replace("&#", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"mailto:", html, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void SafeLinksKeepSchemesAndGetRel()
    {
        StringAssert.Contains(Html("[mail](mailto:help@example.com)"), "href=\"mailto:help@example.com\" rel=\"nofollow noopener noreferrer\"");
        StringAssert.Contains(Html("[rel](/articles/reset)"), "href=\"/articles/reset\" rel=\"nofollow noopener noreferrer\"");
        StringAssert.Contains(Html("Visit https://example.com today"), "<a href=\"https://example.com\" rel=\"nofollow noopener noreferrer\">");
    }

    [TestMethod]
    public void HtmlInTextIsEscaped()
    {
        var html = Html("Use `<b>` tags or 2 < 3 & \"quotes\" <b>bold</b>");
        StringAssert.Contains(html, "<code>&lt;b&gt;</code>");
        StringAssert.Contains(html, "&lt;b&gt;bold&lt;/b&gt;");
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
    }

    [TestMethod]
    public void PathologicalInputIsBoundedAndSafe()
    {
        var inputs = new[]
        {
            new string('>', 50_000),
            string.Concat(Enumerable.Repeat("> ", 20_000)) + "deep",
            string.Join('\n', Enumerable.Range(0, 250).Select(level => new string(' ', level * 2) + "- item")),
            string.Concat(Enumerable.Repeat("- ", 20_000)) + "x",
            string.Concat(Enumerable.Repeat("1. ", 20_000)) + "x",
            new string('[', 50_000) + new string(']', 40_000),
            new string('*', 99_000),
            string.Concat(Enumerable.Repeat("*a **b ", 12_000)),
            string.Concat(Enumerable.Repeat("<a href=\"javascript:x\">", 4_000)),
        };
        foreach (var input in inputs)
        {
            var watch = Stopwatch.StartNew();
            var html = Html(input);
            AssertSafe(html);
            _ = Renderer.ToPlainText(input);
            Assert.IsLessThan(TimeSpan.FromSeconds(10), watch.Elapsed, $"Rendering took {watch.Elapsed} for input starting '{input[..10]}'");
        }
    }

    [TestMethod]
    public void PlainTextStripsMarkdown()
    {
        var text = Renderer.ToPlainText("# Reset\n\nOpen **settings** and [choose](https://example.com) reset.\n\n<script>x</script>");
        StringAssert.Contains(text, "Reset");
        StringAssert.Contains(text, "Open settings and choose reset.");
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void RejectsUnboundedOrInvalidInput()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Renderer.Render(new string('a', ArticleTranslation.MaximumBodyLength + 1)));
        Assert.ThrowsExactly<ArgumentException>(() => Renderer.Render("bad\u0000"));
        Assert.ThrowsExactly<ArgumentException>(() => Renderer.Render("lone \ud800 surrogate"));
        Assert.AreEqual(string.Empty, Html(string.Empty));
    }

    private static readonly HashSet<string> AllowedTags = new(StringComparer.Ordinal)
    {
        "a", "blockquote", "br", "code", "del", "em", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "img", "li", "ol", "p", "pre", "strong",
        "table", "tbody", "td", "th", "thead", "tr", "ul", "sup", "sub",
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal) { "href", "src", "alt", "title", "start", "rel" };

    /// <summary>Every real tag (escaped text is not a tag) is allow-listed, with allow-listed attributes and safe URLs.</summary>
    private static void AssertSafe(string html)
    {
        foreach (System.Text.RegularExpressions.Match tag in System.Text.RegularExpressions.Regex.Matches(html, "<(/?)([^\\s>/]+)([^>]*)>"))
        {
            var name = tag.Groups[2].Value;
            Assert.Contains(name, AllowedTags, $"Unexpected tag <{name}> in {html}");
            foreach (System.Text.RegularExpressions.Match attribute in System.Text.RegularExpressions.Regex.Matches(tag.Groups[3].Value, "([^\\s=\"']+)(?:=\"([^\"]*)\")?"))
            {
                var attributeName = attribute.Groups[1].Value;
                Assert.Contains(attributeName, AllowedAttributes, $"Unexpected attribute {attributeName} in {tag.Value}");
                if (attributeName is "href" or "src")
                {
                    var url = System.Net.WebUtility.HtmlDecode(attribute.Groups[2].Value).Trim();
                    var colon = url.IndexOf(':');
                    var slash = url.IndexOfAny(['/', '?', '#']);
                    var scheme = colon >= 0 && (slash < 0 || colon < slash) ? url[..colon].ToLowerInvariant() : null;
                    Assert.IsTrue(scheme is null or "http" or "https" || (scheme == "mailto" && attributeName == "href"), $"Unsafe URL in {tag.Value}");
                }
            }
        }
    }
}
