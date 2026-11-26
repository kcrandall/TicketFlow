using TicketFlow.Conversations;
using TicketFlow.Core;

namespace TicketFlow.Automation;

/// <summary>
/// A workspace automation rule: when <see cref="Trigger"/> fires and <see cref="Condition"/> (when supplied) is true, run
/// <see cref="Actions"/> in order as the automation API client. Enabled rules are evaluated in ascending <see cref="Priority"/>
/// (then identifier, ordinal); a matching rule with <see cref="StopProcessing"/> ends evaluation for the triggering event. Events caused
/// by automation fire the rule only when <see cref="RunOnAutomationEvents"/> (and always within the engine's depth and rate limits).
/// </summary>
public sealed record AutomationRule
{
    public const int MaximumNameLength = 128;
    public const int MaximumActions = 16;
    public const int MaximumPriority = 10_000;

    public AutomationRule(AutomationRuleId id, string name, AutomationTrigger trigger, AutomationCondition? condition, IEnumerable<AutomationAction> actions,
        int priority = 100, bool enabled = true, bool stopProcessing = false, bool runOnAutomationEvents = false)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A rule identifier is required.", nameof(id));
        Id = id;
        Name = AutomationText.RequireText(name, MaximumNameLength, nameof(name));
        if (trigger.Value is null)
            throw new ArgumentException("A trigger is required.", nameof(trigger));
        Trigger = trigger;
        Condition = condition;
        ArgumentNullException.ThrowIfNull(actions);
        Actions = [.. actions];
        if (Actions.Count is < 1 or > MaximumActions || Actions.Any(action => action.Value is null))
            throw new ArgumentException($"A rule has 1-{MaximumActions} actions.", nameof(actions));
        if (Actions.Any(action => action.Value is ExecuteSlaEscalationAction) && trigger.Value is not SlaEventTrigger)
            throw new ArgumentException("SLA escalation actions require an SLA event trigger.", nameof(actions));
        if (priority is < 0 or > MaximumPriority)
            throw new ArgumentOutOfRangeException(nameof(priority), $"Priorities are 0-{MaximumPriority}.");
        Priority = priority;
        Enabled = enabled;
        StopProcessing = stopProcessing;
        RunOnAutomationEvents = runOnAutomationEvents;
    }

    public AutomationRuleId Id { get; }
    public string Name { get; }
    public AutomationTrigger Trigger { get; }
    public AutomationCondition? Condition { get; }
    public IReadOnlyList<AutomationAction> Actions { get; }
    public int Priority { get; }
    public bool Enabled { get; }
    public bool StopProcessing { get; }
    public bool RunOnAutomationEvents { get; }

    /// <summary>The schedule of a <see cref="ScheduledTrigger"/>, otherwise <see langword="null"/>.</summary>
    public AutomationSchedule? Schedule => Trigger.Value is ScheduledTrigger scheduled ? scheduled.Schedule : null;

    public bool Equals(AutomationRule? other) =>
        other is not null && Id == other.Id && string.Equals(Name, other.Name, StringComparison.Ordinal) && Equals(Trigger.Value, other.Trigger.Value) && Equals(Condition, other.Condition)
        && Actions.SequenceEqual(other.Actions) && Priority == other.Priority && Enabled == other.Enabled && StopProcessing == other.StopProcessing
        && RunOnAutomationEvents == other.RunOnAutomationEvents;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Priority, Actions.Count);

    public override string ToString() => $"AutomationRule {{ Id = {Id}, Priority = {Priority}, Enabled = {Enabled} }}";
}

/// <summary>A rule with its scope and revision (first revision is 1).</summary>
public sealed record StoredAutomationRule(TicketScope Scope, AutomationRule Rule, long Revision)
{
    public AutomationRuleId Id => Rule.Id;
}

/// <summary>Who can see and run a macro. Never renumber.</summary>
public enum MacroVisibility
{
    /// <summary>Every agent with <c>macro:run</c>; managed with <c>macro:manage</c>.</summary>
    Shared = 1,

    /// <summary>Only <see cref="Macro.Owner"/>, who manages it with <c>macro:run</c>.</summary>
    Personal = 2,
}

/// <summary>
/// A named ordered list of 1-16 actions an agent runs manually on a conversation with the agent's own permissions. Rule-only actions
/// (webhooks, surveys, SLA escalations) are not allowed.
/// </summary>
public sealed record Macro
{
    public const int MaximumNameLength = 128;
    public const int MaximumDescriptionLength = 1024;

    public Macro(MacroId id, string name, MacroVisibility visibility, UserId? owner, IEnumerable<AutomationAction> actions, string? description = null)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A macro identifier is required.", nameof(id));
        Id = id;
        Name = AutomationText.RequireText(name, MaximumNameLength, nameof(name));
        Visibility = AutomationText.Defined(visibility, nameof(visibility));
        if (owner == default(UserId) || (visibility == MacroVisibility.Personal) != (owner is not null))
            throw new ArgumentException("Personal macros, and only they, name an owner.", nameof(owner));
        Owner = owner;
        ArgumentNullException.ThrowIfNull(actions);
        Actions = [.. actions];
        if (Actions.Count is < 1 or > AutomationRule.MaximumActions || Actions.Any(action => action.Value is null))
            throw new ArgumentException($"A macro has 1-{AutomationRule.MaximumActions} actions.", nameof(actions));
        if (Actions.Any(action => action.IsRuleOnly))
            throw new ArgumentException("Macros cannot send webhooks, trigger surveys or run SLA escalations.", nameof(actions));
        Description = AutomationText.OptionalText(description, MaximumDescriptionLength, nameof(description));
    }

    public MacroId Id { get; }
    public string Name { get; }
    public MacroVisibility Visibility { get; }
    public UserId? Owner { get; }
    public IReadOnlyList<AutomationAction> Actions { get; }
    public string? Description { get; }

    public bool Equals(Macro? other) =>
        other is not null && Id == other.Id && string.Equals(Name, other.Name, StringComparison.Ordinal) && Visibility == other.Visibility && Owner == other.Owner && Actions.SequenceEqual(other.Actions)
        && string.Equals(Description, other.Description, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Id, Name, Visibility, Actions.Count);
}

public sealed record StoredMacro(TicketScope Scope, Macro Macro, long Revision)
{
    public MacroId Id => Macro.Id;
}

/// <summary>One locale's template of a canned response.</summary>
public sealed record CannedResponseVariant
{
    public const int MaximumLocaleLength = 35;

    public CannedResponseVariant(string locale, AutomationTemplate template)
    {
        Locale = RequireLocale(locale, nameof(locale));
        Template = template ?? throw new ArgumentNullException(nameof(template));
    }

    /// <summary>A BCP 47-style tag such as <c>en</c> or <c>pt-BR</c> (compared case-insensitively).</summary>
    public string Locale { get; }

    public AutomationTemplate Template { get; }

    public static string RequireLocale(string locale, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(locale, parameterName);
        var span = locale.AsSpan();
        if (span.Length is 0 or > MaximumLocaleLength)
            throw new ArgumentException("Locales are BCP 47-style tags such as 'en' or 'pt-BR'.", parameterName);
        var partIndex = 0;
        var pos = 0;
        while (true)
        {
            var dash = span[pos..].IndexOf('-');
            var part = dash < 0 ? span[pos..] : span.Slice(pos, dash);
            if (partIndex == 0)
            {
                if (part.Length is < 2 or > 8)
                    throw new ArgumentException("Locales are BCP 47-style tags such as 'en' or 'pt-BR'.", parameterName);
                for (var i = 0; i < part.Length; i++)
                    if (!char.IsAsciiLetter(part[i]))
                        throw new ArgumentException("Locales are BCP 47-style tags such as 'en' or 'pt-BR'.", parameterName);
            }
            else
            {
                if (part.Length is < 1 or > 8)
                    throw new ArgumentException("Locales are BCP 47-style tags such as 'en' or 'pt-BR'.", parameterName);
                for (var i = 0; i < part.Length; i++)
                    if (!char.IsAsciiLetterOrDigit(part[i]))
                        throw new ArgumentException("Locales are BCP 47-style tags such as 'en' or 'pt-BR'.", parameterName);
            }
            if (dash < 0)
                break;
            pos += dash + 1;
            partIndex++;
        }
        return locale;
    }
}

/// <summary>
/// Reusable reply content: a workspace-unique <see cref="Shortcut"/> token and 1-16 locale <see cref="Variants"/> of a safe
/// <see cref="AutomationTemplate"/>, one of which is the <see cref="DefaultLocale"/>.
/// </summary>
public sealed record CannedResponse
{
    public const int MaximumShortcutLength = 64;
    public const int MaximumTitleLength = 128;
    public const int MaximumVariants = 16;
    private readonly CannedResponseVariant defaultVariant;

    public CannedResponse(CannedResponseId id, string shortcut, string title, string defaultLocale, IEnumerable<CannedResponseVariant> variants,
        MessageContentType contentType = MessageContentType.PlainText)
    {
        if (!id.IsInitialized)
            throw new ArgumentException("A canned response identifier is required.", nameof(id));
        Id = id;
        Shortcut = AutomationText.RequireToken(shortcut, MaximumShortcutLength, nameof(shortcut), "Shortcuts");
        Title = AutomationText.RequireText(title, MaximumTitleLength, nameof(title));
        DefaultLocale = CannedResponseVariant.RequireLocale(defaultLocale, nameof(defaultLocale));
        ArgumentNullException.ThrowIfNull(variants);
        Variants = [.. variants.OrderBy(variant => variant?.Locale, StringComparer.OrdinalIgnoreCase)];
        if (Variants.Count is < 1 or > MaximumVariants || Variants.Any(variant => variant is null)
            || Variants.Select(variant => variant.Locale).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Variants.Count)
            throw new ArgumentException($"Canned responses have 1-{MaximumVariants} variants with distinct locales.", nameof(variants));
        CannedResponseVariant? foundDefault = null;
        for (var i = 0; i < Variants.Count; i++)
        {
            if (string.Equals(Variants[i].Locale, defaultLocale, StringComparison.OrdinalIgnoreCase))
            {
                foundDefault = Variants[i];
                break;
            }
        }
        defaultVariant = foundDefault ?? throw new ArgumentException("The default locale must have a variant.", nameof(defaultLocale));
        ContentType = AutomationText.Defined(contentType, nameof(contentType));
    }

    public CannedResponseId Id { get; }
    public string Shortcut { get; }
    public string Title { get; }
    public string DefaultLocale { get; }
    public IReadOnlyList<CannedResponseVariant> Variants { get; }
    public MessageContentType ContentType { get; }

    /// <summary>The escaping for <see cref="ContentType"/>.</summary>
    public TemplateEscaping Escaping => ContentType switch
    {
        MessageContentType.PlainText => TemplateEscaping.None,
        MessageContentType.Markdown => TemplateEscaping.Markdown,
        MessageContentType.Html => TemplateEscaping.Html,
        _ => throw new InvalidOperationException("Unknown content type."),
    };

    /// <summary>The variant for <paramref name="locale"/>: exact tag, then same language, then the default.</summary>
    public CannedResponseVariant Select(string? locale)
    {
        if (!string.IsNullOrEmpty(locale))
        {
            for (var i = 0; i < Variants.Count; i++)
                if (string.Equals(Variants[i].Locale, locale, StringComparison.OrdinalIgnoreCase))
                    return Variants[i];
            var localeSpan = locale.AsSpan();
            var dash = localeSpan.IndexOf('-');
            var language = dash < 0 ? localeSpan : localeSpan[..dash];
            for (var i = 0; i < Variants.Count; i++)
            {
                var vSpan = Variants[i].Locale.AsSpan();
                var vDash = vSpan.IndexOf('-');
                var vLang = vDash < 0 ? vSpan : vSpan[..vDash];
                if (vLang.Equals(language, StringComparison.OrdinalIgnoreCase))
                    return Variants[i];
            }
        }
        return defaultVariant;
    }

    public bool Equals(CannedResponse? other) =>
        other is not null && Id == other.Id && string.Equals(Shortcut, other.Shortcut, StringComparison.Ordinal) && string.Equals(Title, other.Title, StringComparison.Ordinal) && string.Equals(DefaultLocale, other.DefaultLocale
, StringComparison.Ordinal) && Variants.SequenceEqual(other.Variants) && ContentType == other.ContentType;

    public override int GetHashCode() => HashCode.Combine(Id, Shortcut, Variants.Count);
}

public sealed record StoredCannedResponse(TicketScope Scope, CannedResponse Response, long Revision)
{
    public CannedResponseId Id => Response.Id;
}
