using System.Text.RegularExpressions;
using TicketFlow.Core;

namespace TicketFlow.Channels;

/// <summary>
/// Formats and extracts conversation ticket tags embedded in email subjects (<c>[Ticket #&lt;conversationId&gt;]</c>).
/// Acts as a fallback correlation mechanism when email clients strip <c>In-Reply-To</c> / <c>References</c> headers,
/// while <see cref="IInboundChannelPipeline"/> still verifies contact ownership, inbox scope, and non-closed status.
/// </summary>
public static partial class ChannelTicketTag
{
    /// <summary>Optional diagnostic/threading header carrying the TicketFlow conversation identifier on outbound emails.</summary>
    public const string ConversationHeaderName = "X-TicketFlow-Conversation";

    /// <summary>Returns <c>[Ticket #&lt;id&gt;]</c> for the conversation.</summary>
    public static string Format(ConversationId conversationId)
    {
        if (conversationId == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversationId));
        return $"[Ticket #{conversationId.Value}]";
    }

    /// <summary>
    /// Appends the ticket tag to a subject (preserving or adding a single <c>Re: </c> prefix when requested) without duplicating
    /// an existing ticket tag.
    /// </summary>
    public static string FormatSubject(string? baseSubject, ConversationId conversationId, bool includeReplyPrefix = false, string fallbackSubject = "Your conversation")
    {
        var tag = Format(conversationId);
        var clean = Strip(baseSubject);
        if (string.IsNullOrWhiteSpace(clean))
            clean = string.IsNullOrWhiteSpace(fallbackSubject) ? "Your conversation" : fallbackSubject.Trim();
        var prefix = includeReplyPrefix && !clean.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? "Re: " : string.Empty;
        var maxBase = InboundChannelMessage.MaximumSubjectLength - prefix.Length - tag.Length - 1;
        if (clean.Length > maxBase && maxBase > 0)
            clean = clean[..maxBase].TrimEnd();
        return $"{prefix}{clean} {tag}";
    }

    /// <summary>Extracts the first valid <see cref="ConversationId"/> from a subject tag such as <c>[Ticket #&lt;id&gt;]</c>.</summary>
    public static bool TryExtract(string? subject, out ConversationId conversationId)
    {
        conversationId = default;
        if (string.IsNullOrWhiteSpace(subject))
            return false;
        foreach (Match match in TicketTagPattern().Matches(subject))
        {
            var raw = match.Groups["id"].Value;
            try
            {
                conversationId = new ConversationId(raw);
                return true;
            }
            catch (ArgumentException)
            {
                // Keep scanning if a bracketed token did not form a valid ConversationId.
            }
        }
        return false;
    }

    /// <summary>Removes any embedded <c>[Ticket #...]</c> tags from a subject so stored thread subjects do not accumulate duplicate tags.</summary>
    public static string Strip(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return string.Empty;
        var stripped = TicketTagPattern().Replace(subject, string.Empty);
        return CollapseWhitespacePattern().Replace(stripped, " ").Trim();
    }

    [GeneratedRegex(@"\[\s*Ticket\s*#\s*(?<id>[A-Za-z0-9][A-Za-z0-9._:-]{0,127})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TicketTagPattern();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CollapseWhitespacePattern();
}
