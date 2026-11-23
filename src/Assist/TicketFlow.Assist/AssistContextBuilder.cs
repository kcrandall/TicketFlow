using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Assist;

/// <summary>What context to build: the timeline, the contact and optional help center grounding.</summary>
public sealed record AssistContextRequest(bool IncludeTimeline = true, bool IncludeContact = true, AssistGrounding? Grounding = null, bool BuildSupportData = true);

/// <summary>A help center snippet supplied to the model under <see cref="Key"/> (<c>A1</c>, <c>A2</c>, ...).</summary>
public sealed record AssistSnippet(string Key, ArticleSnippet Article)
{
    public override string ToString() => $"AssistSnippet {{ Key = {Key}, Article = {Article.Article} }}";
}

/// <summary>
/// Context built for one actor: the masked conversation view, the visible timeline events used, the rendered and escaped support
/// data, the snippets offered for citation and how many older events were omitted. Contains only what the actor can see.
/// </summary>
public sealed record AssistConversationContext(
    ConversationView Conversation,
    IReadOnlyList<TimelineEventView> Events,
    string SupportData,
    IReadOnlyList<AssistSnippet> Snippets,
    int OmittedEvents,
    bool NotesIncluded,
    bool ContactIncluded)
{
    public override string ToString() => $"AssistConversationContext {{ Conversation = {Conversation.Id}, Events = {Events.Count}, Snippets = {Snippets.Count} }}";
}

/// <summary>Outcome of building context. Switch over every case without a discard arm.</summary>
public union AssistContextResult(AssistConversationContext, AssistRefused);

/// <summary>
/// Builds model context strictly from what the requesting principal can see. The conversation and timeline are read through
/// <see cref="IConversationService"/> (masking applied; notes only when the read included them), the contact through
/// <see cref="IContactService"/> (omitted without <c>contact:read</c>) and help center snippets through <see cref="IHelpCenterSearch"/>
/// with the principal (public content only without <c>helpcenter:read</c>). <c>assist:use</c> is authorized on the conversation's
/// Access resource. Masked values are replaced with <c>[masked]</c>; every value is XML-escaped inside delimited sections so customer
/// content cannot break out of the data block; size is bounded, keeping the newest events.
/// </summary>
public sealed class AssistContextBuilder
{
    public const string MaskedPlaceholder = "[masked]";
    private const int TimelinePageSize = 200;

    private readonly IConversationService conversations;
    private readonly IContactService contacts;
    private readonly IHelpCenterSearch helpCenter;
    private readonly IAccessEvaluator evaluator;
    private readonly AssistOptions options;

    public AssistContextBuilder(IConversationService conversations, IContactService contacts, IHelpCenterSearch helpCenter, IAccessEvaluator evaluator, IOptions<AssistOptions> options)
    {
        this.conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        this.contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        this.helpCenter = helpCenter ?? throw new ArgumentNullException(nameof(helpCenter));
        this.evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
    }

    public async Task<AssistContextResult> BuildAsync(PrincipalRef actor, ConversationId conversation, AssistContextRequest request, CancellationToken cancellationToken = default)
    {
        if (actor.Value is null)
            throw new ArgumentException("An actor is required.", nameof(actor));
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        ArgumentNullException.ThrowIfNull(request);

        var lookup = await conversations.GetAsync(actor, conversation, cancellationToken).ConfigureAwait(false);
        var (view, refused) = lookup switch
        {
            ConversationView found => (found, (AssistRefused?)null),
            ConversationNotFound => (null, NotFound),
            ConversationRefused => ((ConversationView?)null, Forbidden(ConversationPermissions.Read)),
        };
        if (view is null)
            return refused!;
        var decision = await evaluator.AuthorizeAsync(new AccessRequest(actor, AssistPermissions.Use, Resource(actor.Scope, view)), cancellationToken).ConfigureAwait(false);
        if (!decision.IsPermitted)
            return Forbidden(AssistPermissions.Use);

        var timelineTask = request.IncludeTimeline ? ReadTimelineAsync(actor, view, cancellationToken) : null;
        var contactTask = request.IncludeContact ? contacts.GetAsync(actor, view.Contact, cancellationToken) : null;
        var eagerSearchTask = request.Grounding is { Query: not null } eagerGrounding && options.MaximumSnippets > 0
            ? SearchAsync(actor, eagerGrounding, [], cancellationToken)
            : null;

        if (timelineTask is not null && contactTask is not null && eagerSearchTask is not null)
            await Task.WhenAll(timelineTask, contactTask, eagerSearchTask).ConfigureAwait(false);
        else if (timelineTask is not null && contactTask is not null)
            await Task.WhenAll(timelineTask, contactTask).ConfigureAwait(false);
        else if (timelineTask is not null && eagerSearchTask is not null)
            await Task.WhenAll(timelineTask, eagerSearchTask).ConfigureAwait(false);
        else if (contactTask is not null && eagerSearchTask is not null)
            await Task.WhenAll(contactTask, eagerSearchTask).ConfigureAwait(false);

        IReadOnlyList<TimelineEventView> events = [];
        var notesIncluded = false;
        if (timelineTask is not null)
        {
            var read = await timelineTask.ConfigureAwait(false);
            if (read is null)
                return Forbidden(ConversationPermissions.Read);
            (events, notesIncluded) = read.Value;
        }

        ContactView? contact = null;
        if (contactTask is not null)
            contact = await contactTask.ConfigureAwait(false) switch
            {
                ContactApplied<ContactView> applied => applied.Value,
                ContactRefused => null,
            };

        IReadOnlyList<AssistSnippet> snippets = [];
        if (eagerSearchTask is not null)
            snippets = await eagerSearchTask.ConfigureAwait(false);
        else if (request.Grounding is { } grounding && options.MaximumSnippets > 0)
            snippets = await SearchAsync(actor, grounding, events, cancellationToken).ConfigureAwait(false);

        if (!request.BuildSupportData)
            return new AssistConversationContext(view, events, string.Empty, snippets, 0, notesIncluded, contact is not null);

        var header = new StringBuilder();
        RenderConversation(header, view);
        if (contact is not null)
            RenderContact(header, contact);
        if (snippets.Count > 0)
            RenderKnowledgeBase(header, snippets);

        var budget = Math.Max(options.MaximumContextCharacters - header.Length, 1_000);
        var rendered = new List<string>();
        var used = 0;
        var included = new List<TimelineEventView>();
        for (var index = events.Count - 1; index >= 0; index--)
        {
            var line = RenderEvent(events[index], notesIncluded);
            if (line is null)
                continue;
            if (used + line.Length > budget)
            {
                if (rendered.Count == 0)
                {
                    rendered.Add(AssistText.Truncate(line, budget));
                    included.Add(events[index]);
                }
                break;
            }
            used += line.Length;
            rendered.Add(line);
            included.Add(events[index]);
        }
        rendered.Reverse();
        included.Reverse();
        var omitted = events.Count - included.Count;
        var earlierHistory = omitted > 0 || (events.Count > 0 && events[0].Event.Sequence > 1);

        var data = new StringBuilder(header.Length + used + 128);
        data.Append(header);
        if (request.IncludeTimeline)
        {
            data.Append("<timeline");
            if (earlierHistory)
                data.Append(" earlier_events_omitted=\"true\"");
            data.Append(">\n");
            foreach (var line in rendered)
                data.Append(line);
            data.Append("</timeline>\n");
        }
        return new AssistConversationContext(view, included, data.ToString(), snippets, omitted, notesIncluded, contact is not null);
    }

    /// <summary>The Access resource of a masked view: identifiers, routing fields, labels and the condition form of unmasked attributes.</summary>
    public static AccessResource Resource(TicketScope scope, ConversationView view)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(view);
        return new(scope, view.Id.Value, view.Team, view.Inbox, view.Assignee, view.Labels,
            view.Attributes
                .Where(attribute => !attribute.Masked && attribute.Value?.ConditionValue is not null)
                .Select(attribute => new KeyValuePair<string, string>(attribute.Key, attribute.Value!.Value.ConditionValue!)));
    }

    private async Task<(IReadOnlyList<TimelineEventView> Events, bool NotesIncluded)?> ReadTimelineAsync(PrincipalRef actor, ConversationView view, CancellationToken cancellationToken)
    {
        var events = new List<TimelineEventView>();
        long? after = Math.Max(0, view.LastSequence - options.MaximumTimelineEvents);
        var notesIncluded = true;
        while (after is { } cursor && events.Count < options.MaximumTimelineEvents)
        {
            var limit = Math.Min(TimelinePageSize, options.MaximumTimelineEvents - events.Count);
            switch (await conversations.ReadTimelineAsync(actor, view.Id, cursor, limit, cancellationToken).ConfigureAwait(false))
            {
                case TimelinePage page:
                    notesIncluded &= page.NotesIncluded;
                    events.AddRange(page.Events);
                    after = page.Events.Count == 0 ? null : page.NextAfterSequence;
                    break;
                case ConversationNotFound:
                case ConversationRefused:
                    return null;
            }
        }
        return (events, notesIncluded);
    }

    private async Task<IReadOnlyList<AssistSnippet>> SearchAsync(PrincipalRef actor, AssistGrounding grounding, IReadOnlyList<TimelineEventView> events, CancellationToken cancellationToken)
    {
        var text = grounding.Query ?? SearchText(events);
        if (text is null)
            return [];
        var query = new HelpCenterSearchQuery(grounding.Portal, grounding.Locale, text, options.MaximumSnippets);
        IReadOnlyList<ArticleSnippet> hits = await helpCenter.SearchAsync(actor, query, cancellationToken).ConfigureAwait(false) switch
        {
            HelpCenterSucceeded<IReadOnlyList<ArticleSnippet>> succeeded => succeeded.Value,
            HelpCenterRefused { Reason: HelpCenterRefusal.Forbidden } => await helpCenter.SearchPublicAsync(actor.Scope, query, cancellationToken).ConfigureAwait(false),
            HelpCenterRefused => [],
        };
        return [.. hits.Take(options.MaximumSnippets).Select((hit, index) => new AssistSnippet("A" + (index + 1).ToString(CultureInfo.InvariantCulture), hit))];
    }

    /// <summary>The latest incoming message as a single-line search query (≤ 256 characters), or null.</summary>
    private static string? SearchText(IReadOnlyList<TimelineEventView> events)
    {
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index].Event.Event.Value is not MessageEvent { Direction: MessageDirection.Incoming } message)
                continue;
            var builder = new StringBuilder();
            foreach (var character in message.Body)
            {
                if (char.IsWhiteSpace(character) || char.IsControl(character))
                {
                    if (builder.Length > 0 && builder[^1] != ' ')
                        builder.Append(' ');
                }
                else
                    builder.Append(character);
                if (builder.Length >= HelpCenterSearchQuery.MaximumQueryLength)
                    break;
            }
            var text = AssistText.Truncate(builder.ToString().Trim(), HelpCenterSearchQuery.MaximumQueryLength);
            if (text.Length > 0 && System.Text.Unicode.Utf16.IsValid(text))
                return text;
        }
        return null;
    }

    private static void RenderConversation(StringBuilder builder, ConversationView view)
    {
        builder.Append("<conversation id=\"").Append(Escape(view.Id.Value))
            .Append("\" status=\"").Append(view.Status.Kind.ToString())
            .Append("\" priority=\"").Append(view.Priority.ToString())
            .Append("\" inbox=\"").Append(Escape(view.Inbox.Value)).Append('"');
        if (view.Team is { } team)
            builder.Append(" team=\"").Append(Escape(team.Value)).Append('"');
        builder.Append(" assigned=\"").Append(view.Assignee is null ? "false" : "true").Append('"');
        if (view.Labels.Count > 0)
            builder.Append(" labels=\"").Append(Escape(string.Join(",", view.Labels.Select(label => label.Value)))).Append('"');
        builder.Append(" created_at=\"").Append(Instant(view.CreatedAt)).Append("\">\n");
        foreach (var attribute in view.Attributes)
        {
            builder.Append("<attribute key=\"").Append(Escape(attribute.Key)).Append('"');
            if (attribute.Masked || attribute.Value is null)
                builder.Append(" masked=\"true\">").Append(MaskedPlaceholder);
            else
                builder.Append('>').Append(Escape(Value(attribute.Value.Value)));
            builder.Append("</attribute>\n");
        }
        builder.Append("</conversation>\n");
    }

    private void RenderContact(StringBuilder builder, ContactView contact)
    {
        builder.Append("<contact");
        if (contact.Profile.DisplayName is { } name)
            builder.Append(" name=\"").Append(Escape(name)).Append('"');
        if (contact.Profile.Company is { } company)
            builder.Append(" company=\"").Append(Escape(company)).Append('"');
        if (contact.Profile.Locale is { } locale)
            builder.Append(" locale=\"").Append(Escape(locale)).Append('"');
        builder.Append(">\n");
        if (options.IncludeContactIdentities)
            foreach (var identity in contact.Identities)
            {
                builder.Append("<identity kind=\"").Append(identity.Kind.ToString()).Append('"');
                if (identity.Masked || identity.Value is null)
                    builder.Append(" masked=\"true\">").Append(MaskedPlaceholder);
                else
                    builder.Append('>').Append(Escape(identity.Value));
                builder.Append("</identity>\n");
            }
        foreach (var attribute in contact.Attributes)
        {
            builder.Append("<attribute key=\"").Append(Escape(attribute.Key)).Append('"');
            if (attribute.Masked || attribute.Value is null)
                builder.Append(" masked=\"true\">").Append(MaskedPlaceholder);
            else
                builder.Append('>').Append(Escape(Value(attribute.Value.Value)));
            builder.Append("</attribute>\n");
        }
        builder.Append("</contact>\n");
    }

    private static void RenderKnowledgeBase(StringBuilder builder, IReadOnlyList<AssistSnippet> snippets)
    {
        builder.Append("<knowledge_base>\n");
        foreach (var snippet in snippets)
        {
            builder.Append("<article key=\"").Append(snippet.Key).Append("\" title=\"").Append(Escape(snippet.Article.Title)).Append("\">");
            if (snippet.Article.Excerpt is { } excerpt)
                builder.Append(Escape(excerpt)).Append(" — ");
            builder.Append(Escape(snippet.Article.Snippet)).Append("</article>\n");
        }
        builder.Append("</knowledge_base>\n");
    }

    private string? RenderEvent(TimelineEventView view, bool notesIncluded)
    {
        var timeline = view.Event;
        var builder = new StringBuilder();
        builder.Append("<event seq=\"").Append(timeline.Sequence.ToString(CultureInfo.InvariantCulture)).Append("\" at=\"").Append(Instant(timeline.OccurredAt)).Append('"');
        switch (timeline.Event)
        {
            case MessageEvent message:
                builder.Append(" type=\"message\" direction=\"").Append(message.Direction == MessageDirection.Incoming ? "incoming" : "outgoing")
                    .Append("\" from=\"").Append(message.Direction == MessageDirection.Incoming ? "customer" : Author(timeline.Actor)).Append('"');
                if (message.Attachments.Count > 0)
                    builder.Append(" attachments=\"").Append(message.Attachments.Count.ToString(CultureInfo.InvariantCulture)).Append('"');
                builder.Append('>').Append(Body(message.Body));
                break;
            case NoteEvent note:
                if (!notesIncluded)
                    return null;
                builder.Append(" type=\"internal_note\" from=\"").Append(Author(timeline.Actor)).Append("\">").Append(Body(note.Body));
                break;
            case AssignmentChanged assignment:
                builder.Append(" type=\"assignment\">").Append(assignment.Current is null ? "unassigned" : "assigned");
                break;
            case TeamChanged team:
                builder.Append(" type=\"team\">").Append(Escape(team.Current?.Value ?? "none"));
                break;
            case StatusChanged status:
                builder.Append(" type=\"status\">").Append(status.Previous.Kind.ToString()).Append(" -&gt; ").Append(status.Current.Kind.ToString());
                break;
            case PriorityChanged priority:
                builder.Append(" type=\"priority\">").Append(priority.Previous.ToString()).Append(" -&gt; ").Append(priority.Current.ToString());
                break;
            case LabelsChanged labels:
                builder.Append(" type=\"labels\">");
                if (labels.Added.Count > 0)
                    builder.Append("added ").Append(Escape(string.Join(",", labels.Added.Select(label => label.Value))));
                if (labels.Removed.Count > 0)
                    builder.Append(labels.Added.Count > 0 ? "; " : "").Append("removed ").Append(Escape(string.Join(",", labels.Removed.Select(label => label.Value))));
                break;
            case AttributeChanged attribute:
                builder.Append(" type=\"attribute\" key=\"").Append(Escape(attribute.Key)).Append('"');
                if (view.MaskedFields.Count > 0)
                    builder.Append(" masked=\"true\">").Append(MaskedPlaceholder);
                else
                    builder.Append('>').Append(attribute.Current is { } current ? Escape(Value(current)) : "removed");
                break;
            case ParticipantChanged:
                builder.Append(" type=\"participants\">changed");
                break;
            case ContactChanged:
                builder.Append(" type=\"contact\">merged into another contact");
                break;
            case SystemEvent system:
                builder.Append(" type=\"system\">").Append(Escape(system.Code));
                break;
            case ExtensionEvent extension:
                builder.Append(" type=\"extension\">").Append(Escape(extension.Kind));
                break;
        }
        builder.Append("</event>\n");
        return builder.ToString();
    }

    private string Body(string body) =>
        body.Length > options.MaximumMessageCharacters ? Escape(AssistText.Truncate(body, options.MaximumMessageCharacters)) + " …[truncated]" : Escape(body);

    private static string Author(AccessActor actor) => actor.Kind switch
    {
        AccessActorKind.User => "agent",
        AccessActorKind.ApiClient => "integration",
        AccessActorKind.Automation => "automation",
        AccessActorKind.System => "system",
        _ => "unknown",
    };

    private static string Instant(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static string Value(ConversationAttributeValue value) => value switch
    {
        ConversationTextValue text => text.Value,
        ConversationNumberValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        ConversationBooleanValue boolean => boolean.Value ? "true" : "false",
        ConversationDateValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ConversationListValue list => string.Join(", ", list.Items),
    };

    internal static string Value(ContactAttributeValue value) => value switch
    {
        TextAttributeValue text => text.Value,
        NumberAttributeValue number => number.Value.ToString(CultureInfo.InvariantCulture),
        BooleanAttributeValue boolean => boolean.Value ? "true" : "false",
        DateAttributeValue date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ListAttributeValue list => string.Join(", ", list.Items),
    };

    private static readonly SearchValues<char> EscapeChars = SearchValues.Create("&<>\"");

    /// <summary>XML-escapes text so data cannot open or close sections.</summary>
    internal static string Escape(string value)
    {
        var span = value.AsSpan();
        var first = span.IndexOfAny(EscapeChars);
        if (first < 0)
            return value;
        var builder = new StringBuilder(value.Length + 16);
        AppendEscaped(builder, span, first);
        return builder.ToString();
    }

    internal static void AppendEscaped(StringBuilder builder, ReadOnlySpan<char> span)
    {
        var first = span.IndexOfAny(EscapeChars);
        if (first < 0)
        {
            builder.Append(span);
            return;
        }
        AppendEscaped(builder, span, first);
    }

    private static void AppendEscaped(StringBuilder builder, ReadOnlySpan<char> span, int next)
    {
        while (true)
        {
            if (next > 0)
                builder.Append(span[..next]);
            builder.Append(span[next] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => "&quot;",
            });
            span = span[(next + 1)..];
            next = span.IndexOfAny(EscapeChars);
            if (next < 0)
            {
                if (!span.IsEmpty)
                    builder.Append(span);
                return;
            }
        }
    }

    private static AssistRefused NotFound { get; } = new(AssistRefusal.NotFound, ["The conversation does not exist."]);

    private static AssistRefused Forbidden(Permission permission) => new(AssistRefusal.Forbidden, [$"Permission '{permission}' is required."]);
}
