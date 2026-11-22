namespace TicketFlow.Assist;

/// <summary>Assistant configuration: registered models, default model per feature, default workspace settings and context bounds.</summary>
public sealed class AssistOptions
{
    /// <summary>Registered models. Provider packages add theirs (for example <c>AddClaudeAssist()</c>).</summary>
    public IList<AssistModelDefinition> Models { get; } = [];

    /// <summary>Default model identifier per feature. Provider packages fill features that are not set.</summary>
    public IDictionary<AssistFeature, string> FeatureModels { get; } = new Dictionary<AssistFeature, string>();

    /// <summary>Whether workspaces without stored settings may use the assistant (default true).</summary>
    public bool EnabledByDefault { get; set; } = true;

    /// <summary>Daily token quota for workspaces without stored settings (default 2,000,000).</summary>
    public long DefaultDailyTokenLimit { get; set; } = 2_000_000;

    /// <summary>Per-minute request limit for workspaces without stored settings (default 60).</summary>
    public int DefaultRequestsPerMinute { get; set; } = 60;

    /// <summary>Upper bound of the rendered support data per request (default 60,000 characters).</summary>
    public int MaximumContextCharacters { get; set; } = 60_000;

    /// <summary>Longest message or note body included before truncation (default 4,000 characters).</summary>
    public int MaximumMessageCharacters { get; set; } = 4_000;

    /// <summary>Most recent timeline events read into context (default 200, at most 1,000).</summary>
    public int MaximumTimelineEvents { get; set; } = 200;

    /// <summary>Help center snippets supplied to drafts (default 5, at most 10).</summary>
    public int MaximumSnippets { get; set; } = 5;

    /// <summary>Include contact identity values (masked ones always become placeholders). Default false: data minimization.</summary>
    public bool IncludeContactIdentities { get; set; }

    /// <summary>Record summaries, drafts and triage as <c>assist.*</c> extension events (default true).</summary>
    public bool RecordTimelineEvents { get; set; } = true;

    internal AssistSettings DefaultSettings() => new(EnabledByDefault, DefaultDailyTokenLimit, DefaultRequestsPerMinute);

    internal void Validate()
    {
        _ = DefaultSettings();
        if (MaximumContextCharacters is < 4_000 or > 400_000)
            throw new InvalidOperationException("MaximumContextCharacters must be between 4,000 and 400,000.");
        if (MaximumMessageCharacters is < 200 or > 64 * 1024)
            throw new InvalidOperationException("MaximumMessageCharacters must be between 200 and 65,536.");
        if (MaximumTimelineEvents is < 1 or > 1_000)
            throw new InvalidOperationException("MaximumTimelineEvents must be between 1 and 1,000.");
        if (MaximumSnippets is < 0 or > 10)
            throw new InvalidOperationException("MaximumSnippets must be between 0 and 10.");
    }
}
