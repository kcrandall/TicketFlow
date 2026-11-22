using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.UI.Blazor.Theming;
using TicketFlow.Workspaces;

namespace TicketFlow.UI.Blazor.Infrastructure;

/// <summary>Display information for a user identifier. Applications own user profiles; register <see cref="ITicketFlowUserDirectory"/> to supply names.</summary>
public sealed record TicketFlowUserProfile(string DisplayName, string? Email = null);

/// <summary>Optional lookup of user display names (TicketFlow stores user identifiers only). Without it the UI shows identifiers.</summary>
public interface ITicketFlowUserDirectory
{
    ValueTask<TicketFlowUserProfile?> FindAsync(TicketScope scope, UserId user, CancellationToken cancellationToken = default);
}

public enum UiSessionState
{
    /// <summary>The request carries no authenticated user.</summary>
    Unauthenticated = 1,

    /// <summary>Authenticated, but the host's resolvers produced no workspace, partition or principal.</summary>
    NoPrincipal = 2,

    Ready = 3,
}

/// <summary>
/// Per-request UI state: the scope (host partition resolver plus the configured workspace), the principal mapped by the host's
/// <see cref="ITicketFlowPrincipalResolver"/> and a snapshot of the catalog permissions the principal holds at workspace level, loaded
/// with <see cref="IAccessEvaluator.LoadDecisionContextAsync"/>. The snapshot only drives navigation and which controls render; every
/// read and write still goes through the feature services, which authorize with Access.
/// </summary>
public sealed class TicketFlowUISession
{
    private const int DecisionBatch = AccessDecisionContexts.MaximumPermissions;
    private readonly HashSet<Permission> permissions;
    private readonly Dictionary<string, string> userNames = new(StringComparer.Ordinal);

    internal TicketFlowUISession(HttpContext http, TicketFlowBlazorUIOptions options, UiSessionState state, TicketScope? scope, PrincipalRef? principal,
        HashSet<Permission> permissions, TicketFlowThemePreference theme)
    {
        Http = http;
        Options = options;
        State = state;
        this.scope = scope;
        this.principal = principal;
        this.permissions = permissions;
        Theme = theme;
        Path = RelativePath(http, options);
    }

    private readonly TicketScope? scope;
    private readonly PrincipalRef? principal;

    public HttpContext Http { get; }
    public TicketFlowBlazorUIOptions Options { get; }
    public UiSessionState State { get; }
    public TicketScope Scope => scope ?? throw new InvalidOperationException("The session has no scope.");
    public PrincipalRef Principal => principal ?? throw new InvalidOperationException("The session has no principal.");
    public TicketFlowThemePreference Theme { get; internal set; }
    public IServiceProvider Services => Http.RequestServices;
    public CancellationToken Aborted => Http.RequestAborted;

    /// <summary>The page path relative to <see cref="TicketFlowBlazorUIOptions.BasePath"/> without leading slash (empty for the dashboard).</summary>
    public string Path { get; internal set; }

    /// <summary>The outcome of the previous form post (flash), shown once.</summary>
    public UiOutcome? Flash { get; internal set; }

    /// <summary>The posted form when a refused action re-renders the page (so inputs keep their values).</summary>
    public IFormCollection? Form { get; internal set; }

    /// <summary>Action-specific state rendered once (revealed values, previews, dry-run results); never persisted.</summary>
    public object? PageState { get; internal set; }

    /// <summary>The antiforgery form field name and request token for forms rendered in this response.</summary>
    public (string Name, string Token) Antiforgery { get; internal set; }

    /// <summary>The value posted for <paramref name="name"/> by a re-rendered form, else <paramref name="fallback"/>.</summary>
    public string? FormValue(string name, string? fallback) =>
        Form is not null && Form.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : fallback;

    public T? PageStateAs<T>() where T : class => PageState as T;

    public UserId? UserId => principal?.Value is UserPrincipal user ? user.UserId : null;

    public IReadOnlySet<Permission> Permissions => permissions;

    public bool Can(Permission permission) => permissions.Contains(permission);

    public bool CanAny(params Permission[] candidates) => candidates.Any(permissions.Contains);

    /// <summary>An optional feature service, or <see langword="null"/> when the host did not register the feature.</summary>
    public T? Service<T>() where T : class => Services.GetService<T>();

    public bool Has<T>() where T : class => Services.GetService<T>() is not null;

    public string? Query(string key) => Http.Request.Query.TryGetValue(key, out var values) && values.Count > 0 && values[0] is { Length: > 0 } value ? value : null;

    /// <summary>An absolute URL path for a base-relative <paramref name="path"/> plus non-empty query values.</summary>
    public string Url(string path, params (string Key, string? Value)[] query) => UiUrls.Combine(Options.BasePath, path, query);

    public string DisplayName
    {
        get
        {
            var user = Http.User;
            return user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name) ?? user.Identity?.Name ?? UserName(UserId);
        }
    }

    public string? Email => Http.User.FindFirstValue("email") ?? Http.User.FindFirstValue(ClaimTypes.Email);

    /// <summary>Display name of a user identifier (directory lookups are preloaded with <see cref="LoadUsersAsync"/>).</summary>
    public string UserName(UserId? user) => user is null ? "Unassigned" : userNames.TryGetValue(user.Value.Value, out var name) ? name : user.Value.Value;

    public async Task LoadUsersAsync(IEnumerable<UserId?> users)
    {
        if (Service<ITicketFlowUserDirectory>() is not { } directory || scope is null)
            return;
        foreach (var user in users.OfType<UserId>().Distinct())
            if (!userNames.ContainsKey(user.Value) && await directory.FindAsync(scope, user, Aborted) is { } profile)
                userNames[user.Value] = profile.DisplayName;
    }

    public static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[^1][0])),
        };
    }

    internal static string RelativePath(HttpContext http, TicketFlowBlazorUIOptions options)
    {
        var path = http.Request.Path.Value ?? string.Empty;
        if (options.BasePath != "/" && path.StartsWith(options.BasePath, StringComparison.OrdinalIgnoreCase))
            path = path[options.BasePath.Length..];
        return path.Trim('/');
    }

    /// <summary>Resolves the session of <paramref name="http"/>: authentication state, scope, principal, permission snapshot and theme.</summary>
    public static async Task<TicketFlowUISession> ResolveAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptions<TicketFlowBlazorUIOptions>>().Value;
        var theme = UiTheme.Read(http, options);
        var none = new HashSet<Permission>();
        if (http.User.Identity?.IsAuthenticated != true)
            return new TicketFlowUISession(http, options, UiSessionState.Unauthenticated, null, null, none, theme);
        var workspace = options.ResolveWorkspace is { } resolveWorkspace ? await resolveWorkspace(http) : options.Workspace;
        var partitions = services.GetService<ITicketFlowPartitionResolver>();
        var resolver = services.GetService<ITicketFlowPrincipalResolver>();
        if (workspace is not { } id || partitions is null || resolver is null || await partitions.ResolveAsync(http, http.RequestAborted) is not { } partition)
            return new TicketFlowUISession(http, options, UiSessionState.NoPrincipal, null, null, none, theme);
        var scope = partition.InWorkspace(id);
        if (await resolver.ResolveAsync(http, scope, http.RequestAborted) is not { Value: not null } principal || principal.Scope != scope)
            return new TicketFlowUISession(http, options, UiSessionState.NoPrincipal, scope, null, none, theme);

        var held = new HashSet<Permission>();
        if (services.GetService<IAccessEvaluator>() is { } evaluator && services.GetService<IPermissionCatalog>() is { } catalog)
        {
            var resource = WorkspaceResources.Workspace(scope);
            foreach (var chunk in catalog.Definitions.Where(definition => !definition.IsSensitive).Select(definition => definition.Permission).Chunk(DecisionBatch))
            {
                var context = await evaluator.LoadDecisionContextAsync(principal, scope, chunk, http.RequestAborted);
                foreach (var permission in chunk)
                    if ((await context.DecideAsync(permission, resource, http.RequestAborted)).IsPermitted)
                        held.Add(permission);
            }
            // Every decision for a sensitive permission is audited, so their snapshot is cached per principal for
            // SensitivePermissionCacheDuration (navigation gating only; the services still authorize each call).
            var cache = services.GetRequiredService<UiSensitivePermissionCache>();
            var key = UiSensitivePermissionCache.Key(principal);
            if (!cache.TryGet(key, out var sensitive))
            {
                var probed = new HashSet<Permission>();
                foreach (var chunk in catalog.Definitions.Where(definition => definition.IsSensitive).Select(definition => definition.Permission).Chunk(DecisionBatch))
                {
                    var context = await evaluator.LoadDecisionContextAsync(principal, scope, chunk, http.RequestAborted);
                    foreach (var permission in chunk)
                        if ((await context.DecideAsync(permission, resource, http.RequestAborted)).IsPermitted)
                            probed.Add(permission);
                }
                sensitive = probed;
                cache.Set(key, probed, options.SensitivePermissionCacheDuration);
            }
            held.UnionWith(sensitive);
        }
        var session = new TicketFlowUISession(http, options, UiSessionState.Ready, scope, principal, held, theme);
        await session.LoadUsersAsync([session.UserId]);
        return session;
    }
}

internal static class UiUrls
{
    public static string Combine(string basePath, string path, params (string Key, string? Value)[] query)
    {
        var root = basePath == "/" ? string.Empty : basePath;
        var relative = path.TrimStart('/');
        var url = relative.Length == 0 ? (root.Length == 0 ? "/" : root) : $"{root}/{relative}";
        var parts = query.Where(pair => !string.IsNullOrEmpty(pair.Value)).Select(pair => $"{UrlEncoder.Default.Encode(pair.Key)}={UrlEncoder.Default.Encode(pair.Value!)}").ToList();
        return parts.Count == 0 ? url : url + (url.Contains('?') ? "&" : "?") + string.Join('&', parts);
    }

    /// <summary>A safe local return path inside the UI, or the dashboard.</summary>
    public static string SafeReturn(string basePath, string? candidate)
    {
        var root = basePath == "/" ? "/" : basePath;
        if (string.IsNullOrEmpty(candidate) || !candidate.StartsWith('/') || candidate.StartsWith("//", StringComparison.Ordinal) || candidate.Contains('\\')
            || !(candidate == root || candidate.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal) || candidate.StartsWith(root + "?", StringComparison.Ordinal)))
            return root;
        return candidate;
    }
}

/// <summary>
/// Short-lived per-principal cache of the sensitive permissions a principal holds at workspace level. Probing a sensitive permission
/// records an audit event, so the navigation snapshot is reused for a short time instead of re-auditing every page view.
/// </summary>
public sealed class UiSensitivePermissionCache(TimeProvider time)
{
    private const int MaximumEntries = 10_000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlySet<Permission> Permissions)> entries = new(StringComparer.Ordinal);

    public static string Key(PrincipalRef principal) => principal.Value switch
    {
        UserPrincipal user => $"{principal.Scope}|u|{user.UserId.Value}|{string.Join(',', user.Teams.Select(team => team.Value).Order(StringComparer.Ordinal))}",
        ApiClientPrincipal client => $"{principal.Scope}|c|{client.ClientId.Value}",
        AutomationPrincipal automation => $"{principal.Scope}|a|{automation.AutomationId}",
        SystemPrincipal system => $"{principal.Scope}|s|{system.Component}",
        _ => $"{principal.Scope}|?",
    };

    public bool TryGet(string key, out IReadOnlySet<Permission> permissions)
    {
        if (entries.TryGetValue(key, out var entry) && entry.Expires > time.GetUtcNow())
        {
            permissions = entry.Permissions;
            return true;
        }
        permissions = new HashSet<Permission>();
        return false;
    }

    public void Set(string key, IReadOnlySet<Permission> permissions, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;
        if (entries.Count >= MaximumEntries)
            foreach (var expired in entries.Where(pair => pair.Value.Expires <= time.GetUtcNow()).Select(pair => pair.Key).ToList())
                entries.TryRemove(expired, out _);
        if (entries.Count < MaximumEntries)
            entries[key] = (time.GetUtcNow() + duration, permissions);
    }
}
