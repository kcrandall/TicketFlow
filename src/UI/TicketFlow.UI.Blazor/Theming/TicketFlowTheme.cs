using System.Text;

namespace TicketFlow.UI.Blazor.Theming;

public enum TicketFlowThemeMode
{
    Dark = 1,
    Light = 2,

    /// <summary>Follows the viewer's operating system (resolved before first paint by a tiny inline script).</summary>
    System = 3,
}

/// <summary>A theme choice: theme identifier plus mode. Persisted in the theme cookie as <c>{theme}.{mode}</c>.</summary>
public sealed record TicketFlowThemePreference(string Theme, TicketFlowThemeMode Mode)
{
    public string ModeToken => Mode switch
    {
        TicketFlowThemeMode.Dark => "dark",
        TicketFlowThemeMode.Light => "light",
        _ => "system",
    };

    public string ToCookieValue() => $"{Theme}.{ModeToken}";

    /// <summary>Parses a cookie value; <see langword="null"/> when malformed or naming an unknown theme.</summary>
    public static TicketFlowThemePreference? Parse(string? value, IEnumerable<TicketFlowTheme> themes)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 80)
            return null;
        var dot = value.LastIndexOf('.');
        if (dot <= 0)
            return null;
        var id = value[..dot];
        TicketFlowThemeMode? mode = value[(dot + 1)..] switch
        {
            "dark" => TicketFlowThemeMode.Dark,
            "light" => TicketFlowThemeMode.Light,
            "system" => TicketFlowThemeMode.System,
            _ => null,
        };
        return mode is { } parsed && themes.Any(theme => theme.Id == id) ? new TicketFlowThemePreference(id, parsed) : null;
    }
}

/// <summary>
/// A theme offered by the switcher. Built-in themes are defined in <c>tf-themes.css</c> under
/// <c>:is(html, .tf-scope):not(.dark)[data-tf-theme="{id}"]</c> (light) and <c>.dark:is(html, .tf-scope)[data-tf-theme="{id}"]</c> (dark). Host themes either ship a stylesheet with the same selectors
/// (<see cref="TicketFlowBlazorUIOptions.Stylesheets"/>) or set <see cref="Light"/>/<see cref="Dark"/> token overrides, which the UI
/// renders as a style block. Tokens use the shadcn names without the leading dashes (<c>background</c>, <c>primary</c>, <c>radius</c>,
/// <c>chart-1</c>, <c>sidebar-primary</c>, ...); unspecified tokens fall back to the zinc base.
/// </summary>
public sealed class TicketFlowTheme
{
    public TicketFlowTheme(string id, string name, string swatch)
    {
        Id = id;
        Name = name;
        Swatch = swatch;
    }

    public string Id { get; }
    public string Name { get; }

    /// <summary>A CSS color shown in the theme switcher.</summary>
    public string Swatch { get; }

    /// <summary>Token overrides for light mode.</summary>
    public IDictionary<string, string> Light { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Token overrides for dark mode.</summary>
    public IDictionary<string, string> Dark { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    internal bool HasOverrides => Light.Count > 0 || Dark.Count > 0;

    internal void Validate()
    {
        if (!IsToken(Id) || string.IsNullOrWhiteSpace(Name) || !IsValue(Swatch))
            throw new InvalidOperationException($"Theme '{Id}' is invalid: identifiers are 1-40 characters of a-z, 0-9 and '-'.");
        foreach (var (key, value) in Light.Concat(Dark))
            if (!IsToken(key) || !IsValue(value))
                throw new InvalidOperationException($"Theme '{Id}' has an invalid token '{key}'.");
    }

    private string? cachedCss;
    private int cachedLightCount = -1;
    private int cachedDarkCount = -1;

    /// <summary>The CSS rules for <see cref="Light"/>/<see cref="Dark"/> overrides (validated: no braces, semicolons or markup).</summary>
    internal string ToCss()
    {
        if (cachedCss is not null && cachedLightCount == Light.Count && cachedDarkCount == Dark.Count)
            return cachedCss;
        var css = new StringBuilder();
        Append(css, $":is(html, .tf-scope):not(.dark)[data-tf-theme=\"{Id}\"]", Light);
        Append(css, $".dark:is(html, .tf-scope)[data-tf-theme=\"{Id}\"]", Dark);
        cachedLightCount = Light.Count;
        cachedDarkCount = Dark.Count;
        return cachedCss = css.ToString();
    }

    private static void Append(StringBuilder css, string selector, IDictionary<string, string> tokens)
    {
        if (tokens.Count == 0)
            return;
        css.Append(selector).Append('{');
        foreach (var (key, value) in tokens)
            css.Append("--").Append(key).Append(':').Append(value).Append(';');
        css.Append('}');
    }

    private static bool IsToken(string value) =>
        value is { Length: > 0 and <= 40 } && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static bool IsValue(string value) =>
        value is { Length: > 0 and <= 200 } && value.All(character => !char.IsControl(character) && character is not ('{' or '}' or ';' or '<' or '>' or '"' or '\\'));
}

/// <summary>Built-in themes. Zinc dark is the default.</summary>
public static class TicketFlowThemes
{
    public static TicketFlowTheme Zinc { get; } = new("zinc", "Zinc", "oklch(0.552 0.016 285.938)");
    public static TicketFlowTheme Neutral { get; } = new("neutral", "Neutral", "oklch(0.556 0 0)");
    public static TicketFlowTheme Slate { get; } = new("slate", "Slate", "oklch(0.554 0.046 257.417)");
    public static TicketFlowTheme Stone { get; } = new("stone", "Stone", "oklch(0.553 0.013 58.071)");
    public static TicketFlowTheme Blue { get; } = new("blue", "Blue", "oklch(0.623 0.214 259.815)");
    public static TicketFlowTheme Violet { get; } = new("violet", "Violet", "oklch(0.606 0.25 292.717)");
    public static TicketFlowTheme Green { get; } = new("green", "Green", "oklch(0.627 0.194 149.214)");
    public static TicketFlowTheme Rose { get; } = new("rose", "Rose", "oklch(0.645 0.246 16.439)");
    public static TicketFlowTheme Orange { get; } = new("orange", "Orange", "oklch(0.705 0.213 47.604)");

    public static IReadOnlyList<TicketFlowTheme> BuiltIn { get; } = [Zinc, Neutral, Slate, Stone, Blue, Violet, Green, Rose, Orange];
}
