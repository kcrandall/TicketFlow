using System.Net;
using System.Text;

namespace TicketFlow.Automation;

/// <summary>How substituted values are escaped for the template's content type. Never renumber.</summary>
public enum TemplateEscaping
{
    /// <summary>Plain text: values are inserted verbatim.</summary>
    None = 1,

    /// <summary>Markdown: punctuation that could form links, emphasis or HTML is backslash-escaped.</summary>
    Markdown = 2,

    /// <summary>HTML: values are HTML-encoded.</summary>
    Html = 3,
}

/// <summary>One literal run or variable reference of a parsed template.</summary>
public sealed record TemplateSegment(string? Text, string? Variable, string? Fallback);

/// <summary>
/// A safe text template: literal text with <c>{{ variable }}</c> or <c>{{ variable | fallback }}</c> placeholders. There is no code,
/// no expressions, no loops and no includes — only whitelisted variables:
/// <c>contact.name</c>, <c>contact.company</c>, <c>contact.attributes.&lt;key&gt;</c>, <c>agent.name</c>, <c>conversation.id</c>,
/// <c>conversation.status</c>, <c>conversation.priority</c>, <c>conversation.attributes.&lt;key&gt;</c>. Templates are at most
/// <see cref="MaximumLength"/> characters with at most <see cref="MaximumVariables"/> placeholders; rendered output is capped at
/// <see cref="MaximumOutputBytes"/> UTF-8 bytes and each value at <see cref="MaximumValueLength"/> characters. A value that is missing
/// or masked for the rendering principal renders as the fallback (or nothing): masked values are never substituted.
/// </summary>
public sealed record AutomationTemplate
{
    public const int MaximumLength = 16 * 1024;
    public const int MaximumVariables = 64;
    public const int MaximumFallbackLength = 256;
    public const int MaximumValueLength = 4096;
    public const int MaximumOutputBytes = 64 * 1024;

    public const string ContactName = "contact.name";
    public const string ContactCompany = "contact.company";
    public const string ContactAttributePrefix = "contact.attributes.";
    public const string AgentName = "agent.name";
    public const string ConversationIdVariable = "conversation.id";
    public const string ConversationStatus = "conversation.status";
    public const string ConversationPriority = "conversation.priority";
    public const string ConversationAttributePrefix = "conversation.attributes.";

    public AutomationTemplate(string source)
    {
        Source = AutomationText.RequireContent(source, MaximumLength, nameof(source));
        var segments = new List<TemplateSegment>();
        var variables = new List<string>();
        var position = 0;
        while (position < Source.Length)
        {
            var open = Source.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                segments.Add(new TemplateSegment(Source[position..], null, null));
                break;
            }
            if (open > position)
                segments.Add(new TemplateSegment(Source[position..open], null, null));
            var close = Source.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                throw new ArgumentException("A template placeholder is not closed.", nameof(source));
            var body = Source[(open + 2)..close];
            if (body.Contains("{{", StringComparison.Ordinal))
                throw new ArgumentException("Template placeholders cannot nest.", nameof(source));
            var pipe = body.IndexOf('|');
            var variable = (pipe < 0 ? body : body[..pipe]).Trim();
            var fallback = pipe < 0 ? null : body[(pipe + 1)..].Trim();
            if (!IsVariable(variable))
                throw new ArgumentException("A template references an unknown variable.", nameof(source));
            if (fallback is { Length: > MaximumFallbackLength })
                throw new ArgumentException($"Template fallbacks are at most {MaximumFallbackLength} characters.", nameof(source));
            segments.Add(new TemplateSegment(null, variable, fallback));
            if (!variables.Contains(variable, StringComparer.Ordinal))
                variables.Add(variable);
            if (segments.Count(segment => segment.Variable is not null) > MaximumVariables)
                throw new ArgumentException($"Templates have at most {MaximumVariables} placeholders.", nameof(source));
            position = close + 2;
        }
        Segments = segments.AsReadOnly();
        Variables = variables.AsReadOnly();
    }

    public string Source { get; }

    public IReadOnlyList<TemplateSegment> Segments { get; }

    /// <summary>Distinct referenced variables in first-use order.</summary>
    public IReadOnlyList<string> Variables { get; }

    /// <summary>True for a whitelisted variable path.</summary>
    public static bool IsVariable(string? variable) => variable switch
    {
        ContactName or ContactCompany or AgentName or ConversationIdVariable or ConversationStatus or ConversationPriority => true,
        not null when variable.StartsWith(ContactAttributePrefix, StringComparison.Ordinal) => AutomationText.IsToken(variable.AsSpan(ContactAttributePrefix.Length), 64),
        not null when variable.StartsWith(ConversationAttributePrefix, StringComparison.Ordinal) => AutomationText.IsToken(variable.AsSpan(ConversationAttributePrefix.Length), 64),
        _ => false,
    };

    /// <summary>
    /// Renders with <paramref name="values"/> (variable → value; absent or <see langword="null"/> = unavailable or masked). Returns
    /// <see langword="null"/> when the output would exceed <see cref="MaximumOutputBytes"/>.
    /// </summary>
    public string? Render(IReadOnlyDictionary<string, string?> values, TemplateEscaping escaping = TemplateEscaping.None)
    {
        ArgumentNullException.ThrowIfNull(values);
        AutomationText.Defined(escaping, nameof(escaping));
        var output = new StringBuilder();
        foreach (var segment in Segments)
        {
            if (segment.Text is { } text)
            {
                output.Append(text);
            }
            else
            {
                var value = values.TryGetValue(segment.Variable!, out var supplied) && !string.IsNullOrEmpty(supplied) ? supplied : null;
                if (value is null)
                {
                    output.Append(segment.Fallback);
                    continue;
                }
                if (value.Length > MaximumValueLength)
                    value = value[..MaximumValueLength];
                output.Append(Escape(Sanitize(value), escaping));
            }
            if (output.Length > MaximumOutputBytes)
                return null;
        }
        var rendered = output.ToString();
        return Encoding.UTF8.GetByteCount(rendered) > MaximumOutputBytes ? null : rendered;
    }

    /// <summary>Removes control characters other than tab/CR/LF and lone surrogates from substituted values.</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                builder.Append(character).Append(value[++index]);
                continue;
            }
            if (char.IsSurrogate(character) || (character is <= '\u001F' or (>= '\u007F' and <= '\u009F') && character is not ('\t' or '\r' or '\n')))
                continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string Escape(string value, TemplateEscaping escaping) => escaping switch
    {
        TemplateEscaping.None => value,
        TemplateEscaping.Html => WebUtility.HtmlEncode(value),
        TemplateEscaping.Markdown => EscapeMarkdown(value),
        _ => throw new ArgumentOutOfRangeException(nameof(escaping)),
    };

    private static string EscapeMarkdown(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '.' or '!' or '|' or '<' or '>' or '~')
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    public bool Equals(AutomationTemplate? other) => other is not null && Source == other.Source;

    public override int GetHashCode() => Source.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => $"AutomationTemplate {{ Variables = {Variables.Count} }}";
}
