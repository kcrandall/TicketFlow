using TicketFlow.Core;

namespace TicketFlow.Surveys;

public enum SurveyKind
{
    /// <summary>Customer satisfaction, 1 (very dissatisfied) to 5 (very satisfied); 4-5 count as satisfied.</summary>
    Csat = 1,

    /// <summary>Net promoter score, 0-10; 9-10 promoters, 7-8 passives, 0-6 detractors.</summary>
    Nps = 2,
}

public static class SurveyScales
{
    public static int Minimum(SurveyKind kind) => kind switch
    {
        SurveyKind.Csat => 1,
        SurveyKind.Nps => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static int Maximum(SurveyKind kind) => kind switch
    {
        SurveyKind.Csat => 5,
        SurveyKind.Nps => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool IsValid(SurveyKind kind, int score) => Enum.IsDefined(kind) && score >= Minimum(kind) && score <= Maximum(kind);

    /// <summary>Positive (satisfied / promoter), neutral (CSAT 3 / passive) or negative (dissatisfied / detractor) as 1, 0 or -1.</summary>
    public static int Sentiment(SurveyKind kind, int score) => kind switch
    {
        SurveyKind.Csat => score >= 4 ? 1 : score == 3 ? 0 : -1,
        SurveyKind.Nps => score >= 9 ? 1 : score >= 7 ? 0 : -1,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>The question shown in one locale, with an optional prompt for the free-text comment.</summary>
public sealed record SurveyQuestion
{
    public const int MaximumTextLength = 512;
    public const int MaximumPromptLength = 256;

    public SurveyQuestion(SurveyLocale locale, string text, string? commentPrompt = null)
    {
        if (!locale.IsInitialized)
            throw new ArgumentException("A locale is required.", nameof(locale));
        Locale = locale;
        Text = SurveyText.RequireLine(text, MaximumTextLength, nameof(text));
        CommentPrompt = commentPrompt is null ? null : SurveyText.RequireLine(commentPrompt, MaximumPromptLength, nameof(commentPrompt));
    }

    public SurveyLocale Locale { get; }
    public string Text { get; }
    public string? CommentPrompt { get; }
}

public enum SurveyTrigger
{
    /// <summary>Invite the conversation's contact when the conversation is resolved.</summary>
    ConversationResolved = 1,
}

/// <summary>
/// When invitations are created and how long they live: <see cref="Delay"/> after the trigger (0-7 days), at most one invitation
/// per contact per survey within <see cref="ContactCooldown"/> (0-365 days), and a response window of
/// <see cref="InvitationLifetime"/> (1 hour - 30 days) from delivery.
/// </summary>
public sealed record SurveyTriggerPolicy
{
    public static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumCooldown = TimeSpan.FromDays(365);
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(30);

    public SurveyTriggerPolicy(TimeSpan delay, TimeSpan contactCooldown, TimeSpan invitationLifetime, SurveyTrigger trigger = SurveyTrigger.ConversationResolved)
    {
        if (!Enum.IsDefined(trigger))
            throw new ArgumentOutOfRangeException(nameof(trigger));
        if (delay < TimeSpan.Zero || delay > MaximumDelay)
            throw new ArgumentOutOfRangeException(nameof(delay));
        if (contactCooldown < TimeSpan.Zero || contactCooldown > MaximumCooldown)
            throw new ArgumentOutOfRangeException(nameof(contactCooldown));
        if (invitationLifetime < MinimumLifetime || invitationLifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(invitationLifetime));
        Trigger = trigger;
        Delay = Whole(delay);
        ContactCooldown = Whole(contactCooldown);
        InvitationLifetime = Whole(invitationLifetime);
    }

    public SurveyTrigger Trigger { get; }
    public TimeSpan Delay { get; }
    public TimeSpan ContactCooldown { get; }
    public TimeSpan InvitationLifetime { get; }

    /// <summary>Policies are stored with whole-second precision.</summary>
    private static TimeSpan Whole(TimeSpan value) => TimeSpan.FromSeconds(Math.Floor(value.TotalSeconds));
}

/// <summary>
/// A CSAT or NPS survey for the whole workspace (<see cref="Inbox"/> null) or one inbox. When both apply to a conversation, the
/// inbox survey of a kind wins over the workspace survey of the same kind. Inbox references are opaque to this package.
/// </summary>
public sealed record SurveyDefinition
{
    public const int MaximumNameLength = 128;
    public const int MaximumQuestions = 32;

    public SurveyDefinition(SurveyId id, string name, SurveyKind kind, SurveyLocale defaultLocale, IEnumerable<SurveyQuestion> questions, SurveyTriggerPolicy policy, InboxId? inbox = null, bool enabled = true)
    {
        if (!id.IsInitialized || !defaultLocale.IsInitialized || inbox == default(InboxId))
            throw new ArgumentException("Survey identifier, default locale and inbox (when supplied) must be initialized.", !id.IsInitialized ? nameof(id) : !defaultLocale.IsInitialized ? nameof(defaultLocale) : nameof(inbox));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(policy);
        var byLocale = new SortedDictionary<SurveyLocale, SurveyQuestion>();
        foreach (var question in questions)
        {
            ArgumentNullException.ThrowIfNull(question, nameof(questions));
            if (!byLocale.TryAdd(question.Locale, question) || byLocale.Count > MaximumQuestions)
                throw new ArgumentException($"Questions need distinct locales (at most {MaximumQuestions}).", nameof(questions));
        }
        if (!byLocale.ContainsKey(defaultLocale))
            throw new ArgumentException("A question in the default locale is required.", nameof(questions));
        Id = id;
        Name = SurveyText.RequireLine(name, MaximumNameLength, nameof(name));
        Kind = kind;
        DefaultLocale = defaultLocale;
        Questions = [.. byLocale.Values];
        Policy = policy;
        Inbox = inbox;
        Enabled = enabled;
    }

    public SurveyId Id { get; }
    public string Name { get; }
    public SurveyKind Kind { get; }
    public SurveyLocale DefaultLocale { get; }

    /// <summary>One question per locale, ordered by locale; always includes <see cref="DefaultLocale"/>.</summary>
    public IReadOnlyList<SurveyQuestion> Questions { get; }

    public SurveyTriggerPolicy Policy { get; }
    public InboxId? Inbox { get; }
    public bool Enabled { get; }

    /// <summary>The question for <paramref name="locale"/>, falling back to the default locale.</summary>
    public SurveyQuestion QuestionFor(SurveyLocale? locale) =>
        Questions.FirstOrDefault(question => locale is { } wanted && question.Locale == wanted) ?? Questions.First(question => question.Locale == DefaultLocale);

    public bool Equals(SurveyDefinition? other) =>
        other is not null && Id == other.Id && string.Equals(Name, other.Name, StringComparison.Ordinal) && Kind == other.Kind && DefaultLocale == other.DefaultLocale
        && Questions.SequenceEqual(other.Questions) && Policy == other.Policy && Inbox == other.Inbox && Enabled == other.Enabled;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Kind, DefaultLocale, Questions.Count, Policy, Inbox, Enabled);
}

/// <summary>A survey definition with its scope and revision (first revision is 1).</summary>
public sealed record StoredSurvey(TicketScope Scope, SurveyDefinition Survey, long Revision);
