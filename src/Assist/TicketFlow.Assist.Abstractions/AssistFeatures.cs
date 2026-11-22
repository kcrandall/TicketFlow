using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.HelpCenter;

namespace TicketFlow.Assist;

/// <summary>Summarize a conversation the actor can read.</summary>
public sealed record AssistSummaryRequest
{
    public AssistSummaryRequest(ConversationId conversation)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        Conversation = conversation;
    }

    public ConversationId Conversation { get; }
}

/// <summary>A conversation summary suggestion (model output; never logged).</summary>
public sealed record AssistSummary(string Summary, IReadOnlyList<string> KeyPoints, string? NextStep)
{
    public const int MaximumSummaryLength = 4000;
    public const int MaximumKeyPoints = 10;
    public const int MaximumKeyPointLength = 500;

    public override string ToString() => "AssistSummary { <redacted> }";
}

/// <summary>Help center grounding for a reply draft: the portal and locale to search, and an optional query (default: the latest customer message).</summary>
public sealed record AssistGrounding
{
    public AssistGrounding(PortalId portal, HelpCenterLocale locale, string? query = null)
    {
        if (!portal.IsInitialized || !locale.IsInitialized)
            throw new ArgumentException("Portal and locale are required.");
        Portal = portal;
        Locale = locale;
        Query = query is null ? null : HelpCenterText.RequireLine(query, HelpCenterSearchQuery.MaximumQueryLength, nameof(query));
    }

    public PortalId Portal { get; }
    public HelpCenterLocale Locale { get; }
    public string? Query { get; }

    public override string ToString() => $"AssistGrounding {{ Portal = {Portal}, Locale = {Locale} }}";
}

/// <summary>Draft a reply, optionally grounded in help center articles, with optional agent instructions (≤ 2000 characters).</summary>
public sealed record AssistDraftRequest
{
    public const int MaximumInstructionsLength = 2000;

    public AssistDraftRequest(ConversationId conversation, AssistGrounding? grounding = null, string? instructions = null)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        Conversation = conversation;
        Grounding = grounding;
        Instructions = AssistText.OptionalText(instructions, MaximumInstructionsLength, nameof(instructions));
    }

    public ConversationId Conversation { get; }
    public AssistGrounding? Grounding { get; }
    public string? Instructions { get; }

    public override string ToString() => $"AssistDraftRequest {{ Conversation = {Conversation} }}";
}

/// <summary>An article a draft cites. Only articles returned by the actor's own help center search can be cited.</summary>
public sealed record AssistCitation(ArticleId Article, PortalId Portal, HelpCenterLocale Locale, string Title, HelpCenterSlug Slug, int RevisionNumber)
{
    public override string ToString() => $"AssistCitation {{ Article = {Article}, RevisionNumber = {RevisionNumber} }}";
}

/// <summary>A reply draft suggestion. <see cref="Grounded"/> is true when help center snippets were supplied to the model.</summary>
public sealed record AssistDraft(string Body, IReadOnlyList<AssistCitation> Citations, bool Grounded)
{
    public const int MaximumBodyLength = 20_000;

    public override string ToString() => $"AssistDraft {{ Citations = {Citations.Count}, Grounded = {Grounded} }}";
}

/// <summary>A label the triage may suggest, with an optional description (≤ 200 characters) that helps the model.</summary>
public sealed record AssistLabelOption
{
    public AssistLabelOption(LabelId label, string? description = null)
    {
        if (label == default)
            throw new ArgumentException("A label is required.", nameof(label));
        Label = label;
        Description = AssistText.OptionalText(description, AssistTriageRequest.MaximumDescriptionLength, nameof(description));
    }

    public LabelId Label { get; }
    public string? Description { get; }
}

/// <summary>A team the triage may suggest, with an optional description (≤ 200 characters).</summary>
public sealed record AssistTeamOption
{
    public AssistTeamOption(TeamId team, string? description = null)
    {
        if (team == default)
            throw new ArgumentException("A team is required.", nameof(team));
        Team = team;
        Description = AssistText.OptionalText(description, AssistTriageRequest.MaximumDescriptionLength, nameof(description));
    }

    public TeamId Team { get; }
    public string? Description { get; }
}

/// <summary>Triage a conversation: suggestions are restricted to the supplied candidates (≤ 64 labels, ≤ 64 teams).</summary>
public sealed record AssistTriageRequest
{
    public const int MaximumOptions = 64;
    public const int MaximumDescriptionLength = 200;

    public AssistTriageRequest(ConversationId conversation, IEnumerable<AssistLabelOption>? labels = null, IEnumerable<AssistTeamOption>? teams = null, bool suggestPriority = true)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        Conversation = conversation;
        Labels = [.. labels ?? []];
        Teams = [.. teams ?? []];
        if (Labels.Count > MaximumOptions || Teams.Count > MaximumOptions || Labels.Any(option => option is null) || Teams.Any(option => option is null)
            || Labels.Select(option => option.Label).Distinct().Count() != Labels.Count || Teams.Select(option => option.Team).Distinct().Count() != Teams.Count)
            throw new ArgumentException($"At most {MaximumOptions} distinct label and team candidates are allowed.");
        if (Labels.Count == 0 && Teams.Count == 0 && !suggestPriority)
            throw new ArgumentException("Triage needs label or team candidates, or a priority suggestion.");
        SuggestPriority = suggestPriority;
    }

    public ConversationId Conversation { get; }
    public IReadOnlyList<AssistLabelOption> Labels { get; }
    public IReadOnlyList<AssistTeamOption> Teams { get; }
    public bool SuggestPriority { get; }
}

public sealed record AssistLabelSuggestion(LabelId Label, double Confidence);

public sealed record AssistTeamSuggestion(TeamId Team, double Confidence);

public sealed record AssistPrioritySuggestion(ConversationPriority Priority, double Confidence);

/// <summary>Triage suggestions with confidences in [0, 1]. Apply them through the normal conversation commands.</summary>
public sealed record AssistTriage(IReadOnlyList<AssistLabelSuggestion> Labels, AssistTeamSuggestion? Team, AssistPrioritySuggestion? Priority, string? Rationale)
{
    public const int MaximumRationaleLength = 1000;

    public override string ToString() => $"AssistTriage {{ Labels = {Labels.Count}, Team = {Team?.Team}, Priority = {Priority?.Priority} }}";
}

/// <summary>Classify sentiment and intent. <see cref="Intents"/> optionally restricts intents to ≤ 64 tokens.</summary>
public sealed record AssistSentimentRequest
{
    public const int MaximumIntentLength = 64;

    public AssistSentimentRequest(ConversationId conversation, IEnumerable<string>? intents = null)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        Conversation = conversation;
        Intents = [.. (intents ?? []).Select(intent => AssistText.RequireToken(intent, MaximumIntentLength, nameof(intents), "Intents"))];
        if (Intents.Count > AssistTriageRequest.MaximumOptions || Intents.Distinct(StringComparer.Ordinal).Count() != Intents.Count)
            throw new ArgumentException($"At most {AssistTriageRequest.MaximumOptions} distinct intents are allowed.", nameof(intents));
    }

    public ConversationId Conversation { get; }
    public IReadOnlyList<string> Intents { get; }
}

public enum AssistSentiment
{
    Negative = 1,
    Neutral = 2,
    Positive = 3,
    Mixed = 4,
}

/// <summary>Sentiment and intent of the customer, with confidences in [0, 1]. <see cref="Intent"/> is a lowercase token.</summary>
public sealed record AssistSentimentAndIntent(AssistSentiment Sentiment, double SentimentConfidence, string Intent, double IntentConfidence);

/// <summary>
/// Translate either agent-supplied <see cref="Text"/> (≤ 20000 characters) or a message of the conversation that the actor can read,
/// into <see cref="TargetLocale"/>. Exactly one source is required.
/// </summary>
public sealed record AssistTranslateRequest
{
    public AssistTranslateRequest(ConversationId conversation, HelpCenterLocale targetLocale, string? text = null, MessageId? message = null)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        if (!targetLocale.IsInitialized)
            throw new ArgumentException("A target locale is required.", nameof(targetLocale));
        if ((text is null) == (message is null))
            throw new ArgumentException("Supply exactly one of text or message.");
        if (message == default(MessageId))
            throw new ArgumentException("Message identifiers must be initialized.", nameof(message));
        Conversation = conversation;
        TargetLocale = targetLocale;
        Text = AssistText.OptionalText(text, AssistDraft.MaximumBodyLength, nameof(text));
        Message = message;
    }

    public ConversationId Conversation { get; }
    public HelpCenterLocale TargetLocale { get; }
    public string? Text { get; }
    public MessageId? Message { get; }

    public override string ToString() => $"AssistTranslateRequest {{ Conversation = {Conversation}, TargetLocale = {TargetLocale} }}";
}

/// <summary>A translation suggestion; <see cref="SourceLocale"/> is the detected source language when the model reports one.</summary>
public sealed record AssistTranslation(string Text, HelpCenterLocale TargetLocale, HelpCenterLocale? SourceLocale)
{
    public override string ToString() => $"AssistTranslation {{ TargetLocale = {TargetLocale} }}";
}

public enum AssistTone
{
    Friendly = 1,
    Formal = 2,
    Concise = 3,
    Empathetic = 4,
    Apologetic = 5,
}

/// <summary>Rewrite agent-supplied text (≤ 20000 characters) in a tone, in the context of a conversation the actor can read.</summary>
public sealed record AssistToneRewriteRequest
{
    public AssistToneRewriteRequest(ConversationId conversation, string text, AssistTone tone)
    {
        if (conversation == default)
            throw new ArgumentException("A conversation identifier is required.", nameof(conversation));
        if (!Enum.IsDefined(tone))
            throw new ArgumentOutOfRangeException(nameof(tone));
        Conversation = conversation;
        Text = AssistText.RequireText(text, AssistDraft.MaximumBodyLength, nameof(text));
        Tone = tone;
    }

    public ConversationId Conversation { get; }
    public string Text { get; }
    public AssistTone Tone { get; }

    public override string ToString() => $"AssistToneRewriteRequest {{ Conversation = {Conversation}, Tone = {Tone} }}";
}

public sealed record AssistToneRewrite(string Text, AssistTone Tone)
{
    public override string ToString() => $"AssistToneRewrite {{ Tone = {Tone} }}";
}
