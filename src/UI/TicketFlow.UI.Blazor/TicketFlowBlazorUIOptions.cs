using Microsoft.AspNetCore.Http;
using TicketFlow.Core;
using TicketFlow.UI.Blazor.Theming;

namespace TicketFlow.UI.Blazor;

/// <summary>Options of the TicketFlow Blazor UI (<c>AddTicketFlowBlazorUI</c>).</summary>
public sealed class TicketFlowBlazorUIOptions
{
    /// <summary>Path the UI is mounted under (default <c>/support</c>). Must start with <c>/</c> and not end with one.</summary>
    public string BasePath { get; set; } = "/support";

    /// <summary>Product name shown in the sidebar header and the document title (default <c>TicketFlow</c>).</summary>
    public string ProductName { get; set; } = "TicketFlow";

    /// <summary>
    /// The host's sign-in URL. Unauthenticated requests are redirected here with <see cref="ReturnUrlParameter"/> set to the requested
    /// page; without it they get 401. The UI never implements sign-in itself.
    /// </summary>
    public string? LoginPath { get; set; }

    /// <summary>The host's sign-out URL, linked from the account menu. Hidden when <see langword="null"/>.</summary>
    public string? LogoutPath { get; set; }

    /// <summary>Query parameter carrying the return URL to <see cref="LoginPath"/> (default <c>ReturnUrl</c>, the ASP.NET Core cookie default).</summary>
    public string ReturnUrlParameter { get; set; } = "ReturnUrl";

    /// <summary>The workspace of single-workspace hosts when <see cref="ResolveWorkspace"/> is not set.</summary>
    public WorkspaceId? Workspace { get; set; }

    /// <summary>Resolves the workspace a request manages (for example from the host name, a claim or a cookie). Wins over <see cref="Workspace"/>.</summary>
    public Func<HttpContext, ValueTask<WorkspaceId?>>? ResolveWorkspace { get; set; }

    /// <summary>The theme rendered when the viewer has not chosen one (default: zinc, dark).</summary>
    public TicketFlowThemePreference DefaultTheme { get; set; } = new(TicketFlowThemes.Zinc.Id, TicketFlowThemeMode.Dark);

    /// <summary>Themes offered by the theme switcher. Starts with the built-in themes; add host themes (see <see cref="TicketFlowTheme"/>).</summary>
    public IList<TicketFlowTheme> Themes { get; } = [.. TicketFlowThemes.BuiltIn];

    /// <summary>Additional stylesheets (host theme overrides, fonts) linked after the UI stylesheet.</summary>
    public IList<string> Stylesheets { get; } = [];

    /// <summary>Name of the cookie that persists the viewer's theme (default <c>tf-theme</c>).</summary>
    public string ThemeCookieName { get; set; } = "tf-theme";

    /// <summary>
    /// Renders the full HTML document and sidebar shell (default). Hosts that embed <c>TicketFlowUI</c> inside their own Blazor layout
    /// render only the shell; see docs/ui-blazor.md.
    /// </summary>
    public bool ShowSidebar { get; set; } = true;

    /// <summary>Shows sections the viewer cannot open as disabled entries instead of hiding them (default false).</summary>
    public bool ShowDisabledSections { get; set; }

    /// <summary>
    /// Live conversation updates (timeline refresh, typing indicators) through the TicketFlow SignalR agent hub. Requires
    /// <c>TicketFlow.Realtime.SignalR</c> mapped by the host; the UI works fully without it (default true).
    /// </summary>
    public bool EnableRealtime { get; set; } = true;

    /// <summary>Path of the agent hub (default <c>/ticketflow/realtime/agent</c>).</summary>
    public string RealtimeHubPath { get; set; } = "/ticketflow/realtime/agent";

    /// <summary>Loads <c>_framework/blazor.web.js</c> for enhanced navigation and form posts (default true). Pages work without it.</summary>
    public bool EnhancedNavigation { get; set; } = true;

    /// <summary>
    /// How long the navigation snapshot of sensitive permissions (each probe is audited by Access) is reused per principal
    /// (default 60 seconds; zero probes on every request). Only affects which controls render.
    /// </summary>
    public TimeSpan SensitivePermissionCacheDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Rows per list page (default 25, 1-100).</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>
    /// Shows the Service Accounts &amp; API Keys management tab and forms in the Access UI (default true).
    /// Set to <see langword="false"/> when an external identity provider manages service accounts and API keys and TicketFlow only consumes roles.
    /// </summary>
    public bool ManageApiKeysInUI { get; set; } = true;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(BasePath) || !BasePath.StartsWith('/') || (BasePath.Length > 1 && BasePath.EndsWith('/')) || BasePath.Contains("//", StringComparison.Ordinal))
            throw new InvalidOperationException("TicketFlowBlazorUIOptions.BasePath must start with '/' and must not end with '/'.");
        if (SensitivePermissionCacheDuration < TimeSpan.Zero || SensitivePermissionCacheDuration > TimeSpan.FromHours(1))
            throw new InvalidOperationException("TicketFlowBlazorUIOptions.SensitivePermissionCacheDuration must be 0-1 hour.");
        if (PageSize is < 1 or > 100)
            throw new InvalidOperationException("TicketFlowBlazorUIOptions.PageSize must be 1-100.");
        if (string.IsNullOrWhiteSpace(ThemeCookieName) || string.IsNullOrWhiteSpace(ProductName))
            throw new InvalidOperationException("TicketFlowBlazorUIOptions.ThemeCookieName and ProductName are required.");
        foreach (var theme in Themes)
            theme.Validate();
        if (Themes.Select(theme => theme.Id).Distinct(StringComparer.Ordinal).Count() != Themes.Count)
            throw new InvalidOperationException("Theme identifiers must be unique.");
        if (!Themes.Any(theme => string.Equals(theme.Id, DefaultTheme.Theme, StringComparison.Ordinal)))
            throw new InvalidOperationException("TicketFlowBlazorUIOptions.DefaultTheme must name a registered theme.");
    }
}
