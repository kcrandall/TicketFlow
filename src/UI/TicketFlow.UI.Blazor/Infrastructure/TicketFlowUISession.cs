using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
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

    /// <summary>Profiles of several users in one call (pages resolve every visible user at once). Override for a batched lookup.</summary>
    async ValueTask<IReadOnlyDictionary<UserId, TicketFlowUserProfile>> FindManyAsync(TicketScope scope, IReadOnlyCollection<UserId> users, CancellationToken cancellationToken = default)
    {
        var profiles = new Dictionary<UserId, TicketFlowUserProfile>();
        foreach (var user in users)
            if (await FindAsync(scope, user, cancellationToken).ConfigureAwait(false) is { } profile)
                profiles[user] = profile;
        return profiles;
    }
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
    private static readonly ConditionalWeakTable<IPermissionCatalog, CatalogChunks> PermissionChunkCache = new();
    private readonly HashSet<Permission> permissions;
    private readonly Dictionary<string, string> userNames = new(StringComparer.Ordinal);
    private string? cachedDisplayName;
    private bool displayNameResolved;

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

    public UserId? UserId => principal is UserPrincipal user ? user.UserId : null;

    public IReadOnlySet<Permission> Permissions => permissions;

    public bool Can(Permission permission) => permissions.Contains(permission);

    public bool CanAny(Permission first, Permission second) => permissions.Contains(first) || permissions.Contains(second);

    public bool CanAny(Permission first, Permission second, Permission third) => permissions.Contains(first) || permissions.Contains(second) || permissions.Contains(third);

    public bool CanAny(params Permission[] candidates)
    {
        for (var i = 0; i < candidates.Length; i++)
            if (permissions.Contains(candidates[i]))
                return true;
        return false;
    }

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
            if (displayNameResolved)
                return cachedDisplayName!;
            var user = Http.User;
            cachedDisplayName = ClaimDisplayName(user) ?? (principal is ApiClientPrincipal client ? client.ClientId.Value : UserName(UserId));
            displayNameResolved = true;
            return cachedDisplayName;
        }
    }

    private static string? ClaimDisplayName(ClaimsPrincipal user) =>
        user.FindFirstValue("display_name") ?? user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name) ?? user.Identity?.Name;

    public string? Email => Http.User.FindFirstValue("email") ?? Http.User.FindFirstValue(ClaimTypes.Email);

    /// <summary>Display name of a user identifier (directory lookups are preloaded with <see cref="LoadUsersAsync"/>).</summary>
    public string UserName(UserId? user) => user is null ? "Unassigned" : userNames.TryGetValue(user.Value.Value, out var name) ? name : user.Value.Value;

    public async Task LoadUsersAsync(IEnumerable<UserId?> users)
    {
        if (Service<ITicketFlowUserDirectory>() is not { } directory || scope is null)
            return;
        List<UserId>? missing = null;
        HashSet<string>? queued = null;
        foreach (var candidate in users)
        {
            if (candidate is not { } user || userNames.ContainsKey(user.Value))
                continue;
            queued ??= new HashSet<string>(StringComparer.Ordinal);
            if (!queued.Add(user.Value))
                continue;
            missing ??= [];
            missing.Add(user);
        }
        if (missing is null)
            return;
        foreach (var (user, profile) in await directory.FindManyAsync(scope, missing, Aborted).ConfigureAwait(false))
            userNames[user.Value] = profile.DisplayName;
        displayNameResolved = false;
    }

    public static string Initials(string name)
    {
        var span = name.AsSpan();
        int firstStart = -1, firstLen = 0, lastStart = -1;
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && IsInitialSeparator(span[i]))
                i++;
            if (i >= span.Length)
                break;
            var start = i;
            while (i < span.Length && !IsInitialSeparator(span[i]))
                i++;
            if (firstStart < 0)
            {
                firstStart = start;
                firstLen = i - start;
            }
            else
                lastStart = start;
        }
        if (firstStart < 0)
            return "?";
        if (lastStart < 0)
            return firstLen == 1
                ? char.ToUpperInvariant(span[firstStart]).ToString()
                : string.Create(2, (span[firstStart], span[firstStart + 1]), static (dst, state) =>
                {
                    dst[0] = char.ToUpperInvariant(state.Item1);
                    dst[1] = char.ToUpperInvariant(state.Item2);
                });
        return string.Create(2, (span[firstStart], span[lastStart]), static (dst, state) =>
        {
            dst[0] = char.ToUpperInvariant(state.Item1);
            dst[1] = char.ToUpperInvariant(state.Item2);
        });
    }

    private static bool IsInitialSeparator(char c) => c is ' ' or '.' or '-' or '_' or '@';

    internal static string RelativePath(HttpContext http, TicketFlowBlazorUIOptions options)
    {
        var path = http.Request.Path.Value ?? string.Empty;
        if (options.BasePath != "/" && path.StartsWith(options.BasePath, StringComparison.OrdinalIgnoreCase))
            path = path[options.BasePath.Length..];
        return path.Trim('/');
    }

    /// <summary>Lightweight session resolution without loading the full permission snapshot (for streaming endpoints that authorize through feature services).</summary>
    internal static async Task<TicketFlowUISession> ResolvePrincipalAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptions<TicketFlowBlazorUIOptions>>().Value;
        var theme = UiTheme.Read(http, options);
        var none = new HashSet<Permission>();
        if (http.User.Identity?.IsAuthenticated != true)
            return new TicketFlowUISession(http, options, UiSessionState.Unauthenticated, null, null, none, theme);
        var workspace = options.ResolveWorkspace is { } resolveWorkspace ? await resolveWorkspace(http).ConfigureAwait(false) : options.Workspace;
        var partitions = services.GetService<ITicketFlowPartitionResolver>();
        var ticketFlowResolver = services.GetService<ITicketFlowPrincipalResolver>();
        var accessResolver = services.GetService<IAccessPrincipalResolver>();
        if (workspace is not { } id || (ticketFlowResolver is null && accessResolver is null))
            return new TicketFlowUISession(http, options, UiSessionState.NoPrincipal, null, null, none, theme);
        var partition = partitions is not null
            ? await partitions.ResolveAsync(http, http.RequestAborted).ConfigureAwait(false)
            : PartitionFromClaims(http.User);
        if (partition is null)
            return new TicketFlowUISession(http, options, UiSessionState.NoPrincipal, null, null, none, theme);
        var scope = partition.InWorkspace(id);
        var principal = await ResolvePrincipalAsync(http, scope, ticketFlowResolver, accessResolver).ConfigureAwait(false);
        if (principal is null || principal.Scope != scope)
            return new TicketFlowUISession(http, options, UiSessionState.NoPrincipal, scope, null, none, theme);
        return new TicketFlowUISession(http, options, UiSessionState.Ready, scope, principal, none, theme);
    }

    private static async Task<PrincipalRef?> ResolvePrincipalAsync(
        HttpContext http,
        TicketScope scope,
        ITicketFlowPrincipalResolver? ticketFlowResolver,
        IAccessPrincipalResolver? accessResolver)
    {
        if (accessResolver is not null && IsTicketFlowApiKey(http.User))
            return await accessResolver.ResolveAsync(http.User, scope, http.RequestAborted).ConfigureAwait(false);

        var hasConfiguredHostResolver = ticketFlowResolver is not null
            && (ticketFlowResolver is not OptionsTicketFlowPrincipalResolver
                || http.RequestServices.GetService<IOptions<TicketFlowEndpointOptions>>()?.Value.ResolvePrincipal is not null);

        if (hasConfiguredHostResolver)
        {
            var principal = await ticketFlowResolver!.ResolveAsync(http, scope, http.RequestAborted).ConfigureAwait(false);
            if (principal is null)
                return null;

            return EnrichExternalClaims(http, scope, principal, accessResolver);
        }

        if (accessResolver is not null)
            return await accessResolver.ResolveAsync(http.User, scope, http.RequestAborted).ConfigureAwait(false);

        return null;
    }

    private static PrincipalRef EnrichExternalClaims(
        HttpContext http,
        TicketScope scope,
        PrincipalRef principal,
        IAccessPrincipalResolver? accessResolver)
    {
        if (principal is not (UserPrincipal or ApiClientPrincipal))
            return principal;
        if (principal.ExternalRoles.Count > 0 && principal.IdentityProvider is not null)
            return principal;

        var claimsResolver = accessResolver as ClaimsPrincipalResolver
            ?? new ClaimsPrincipalResolver(http.RequestServices.GetService<IOptions<ClaimsPrincipalResolverOptions>>());
        IReadOnlyCollection<string> externalRoles = principal.ExternalRoles.Count > 0
            ? principal.ExternalRoles
            : claimsResolver.ExtractExternalRoles(http.User);
        var identityProvider = principal.IdentityProvider ?? claimsResolver.TryGetIdentityProvider(http.User);

        if (externalRoles.Count == principal.ExternalRoles.Count && identityProvider == principal.IdentityProvider)
            return principal;

        return principal switch
        {
            UserPrincipal user => new UserPrincipal(scope, user.UserId, user.Teams, externalRoles, identityProvider),
            ApiClientPrincipal client => new ApiClientPrincipal(scope, client.ClientId, externalRoles, identityProvider),
            _ => principal,
        };
    }

    private static bool IsTicketFlowApiKey(ClaimsPrincipal user) =>
        user.Identity is { IsAuthenticated: true } identity
        && string.Equals(identity.AuthenticationType, "ApiKey", StringComparison.Ordinal)
        && user.HasClaim(static c => string.Equals(c.Type, "api_key_id", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(c.Value));

    private static AccessPartition? PartitionFromClaims(ClaimsPrincipal user)
    {
        var tenant = user.FindFirstValue("tenant_id");
        var project = user.FindFirstValue("project_id");
        var environment = user.FindFirstValue("environment_id") ?? user.FindFirstValue("environment");
        var app = user.FindFirstValue("app_id") ?? user.FindFirstValue("app_name");
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(app))
            return null;
        try
        {
            return new AccessPartition(tenant.Trim(), project.Trim(), environment.Trim(), app.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Resolves the session of <paramref name="http"/>: authentication state, scope, principal, permission snapshot and theme.</summary>
    public static async Task<TicketFlowUISession> ResolveAsync(HttpContext http)
    {
        var basic = await ResolvePrincipalAsync(http).ConfigureAwait(false);
        if (basic.State != UiSessionState.Ready)
            return basic;
        var services = http.RequestServices;
        var options = basic.Options;
        var scope = basic.Scope;
        var principal = basic.Principal;

        var held = basic.permissions;
        if (services.GetService<IPermissionCatalog>() is { } catalog)
        {
            var chunks = GetChunks(catalog);
            var planner = services.GetService<IAccessQueryPlanner>();
            var evaluator = planner is null ? services.GetService<IAccessEvaluator>() : null;
            var cache = services.GetRequiredService<UiSensitivePermissionCache>();
            var key = UiSensitivePermissionCache.Key(principal);
            var needSensitive = !cache.TryGet(key, out var sensitive);

            var regularChunks = chunks.Regular;
            var sensitiveChunks = needSensitive ? chunks.Sensitive : [];
            var total = regularChunks.Length + sensitiveChunks.Length;
            if (total == 1)
            {
                if (regularChunks.Length == 1)
                    held.UnionWith(await HeldAsync(planner, evaluator, principal, scope, regularChunks[0], http.RequestAborted).ConfigureAwait(false));
                else
                {
                    var probed = new HashSet<Permission>(await HeldAsync(planner, evaluator, principal, scope, sensitiveChunks[0], http.RequestAborted).ConfigureAwait(false));
                    sensitive = probed;
                    cache.Set(key, probed, options.SensitivePermissionCacheDuration);
                }
            }
            else if (total > 1)
            {
                var tasks = new Task<IReadOnlySet<Permission>>[total];
                for (var i = 0; i < regularChunks.Length; i++)
                    tasks[i] = HeldAsync(planner, evaluator, principal, scope, regularChunks[i], http.RequestAborted);
                for (var i = 0; i < sensitiveChunks.Length; i++)
                    tasks[regularChunks.Length + i] = HeldAsync(planner, evaluator, principal, scope, sensitiveChunks[i], http.RequestAborted);
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                for (var i = 0; i < regularChunks.Length; i++)
                    held.UnionWith(results[i]);
                if (needSensitive)
                {
                    var probed = new HashSet<Permission>();
                    for (var i = 0; i < sensitiveChunks.Length; i++)
                        probed.UnionWith(results[regularChunks.Length + i]);
                    sensitive = probed;
                    cache.Set(key, probed, options.SensitivePermissionCacheDuration);
                }
            }
            else if (needSensitive)
            {
                var empty = new HashSet<Permission>();
                sensitive = empty;
                cache.Set(key, empty, options.SensitivePermissionCacheDuration);
            }
            held.UnionWith(sensitive);
        }
        if (ClaimDisplayName(http.User) is null && basic.UserId is { } userId)
            await basic.LoadUsersAsync([userId]).ConfigureAwait(false);
        return basic;
    }

    private sealed class CatalogChunks
    {
        public int Count { get; init; }
        public required Permission[][] Regular { get; init; }
        public required Permission[][] Sensitive { get; init; }
    }

    private static CatalogChunks GetChunks(IPermissionCatalog catalog)
    {
        var definitions = catalog.Definitions;
        if (PermissionChunkCache.TryGetValue(catalog, out var cached) && cached.Count == definitions.Count)
            return cached;
        var regular = new List<Permission>(definitions.Count);
        var sensitive = new List<Permission>();
        for (var i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (definition.IsSensitive)
                sensitive.Add(definition.Permission);
            else
                regular.Add(definition.Permission);
        }
        var built = new CatalogChunks
        {
            Count = definitions.Count,
            Regular = regular.Count == 0 ? [] : [.. regular.Chunk(DecisionBatch)],
            Sensitive = sensitive.Count == 0 ? [] : [.. sensitive.Chunk(DecisionBatch)],
        };
        PermissionChunkCache.AddOrUpdate(catalog, built);
        return built;
    }

    /// <summary>
    /// Permissions held on any resource of the scope: <see cref="IAccessQueryPlanner.HeldAnywhereAsync"/> (one grant load per batch) when
    /// a planner is registered, otherwise workspace-level decisions.
    /// </summary>
    private static async Task<IReadOnlySet<Permission>> HeldAsync(IAccessQueryPlanner? planner, IAccessEvaluator? evaluator, PrincipalRef principal, TicketScope scope, Permission[] permissions,
        CancellationToken cancellationToken)
    {
        if (planner is not null)
            return await planner.HeldAnywhereAsync(principal, scope, permissions, cancellationToken).ConfigureAwait(false);
        var held = new HashSet<Permission>();
        if (evaluator is null)
            return held;
        var context = await evaluator.LoadDecisionContextAsync(principal, scope, permissions, cancellationToken).ConfigureAwait(false);
        var resource = WorkspaceResources.Workspace(scope);
        for (var i = 0; i < permissions.Length; i++)
            if ((await context.DecideAsync(permissions[i], resource, cancellationToken).ConfigureAwait(false)).IsPermitted)
                held.Add(permissions[i]);
        return held;
    }
}

internal static class UiUrls
{
    public static string Combine(string basePath, string path, params (string Key, string? Value)[] query)
    {
        var root = basePath == "/" ? string.Empty : basePath;
        var relative = path.TrimStart('/');
        var url = relative.Length == 0 ? (root.Length == 0 ? "/" : root) : $"{root}/{relative}";
        if (query.Length == 0)
            return url;
        StringBuilder? sb = null;
        for (var i = 0; i < query.Length; i++)
        {
            var (key, value) = query[i];
            if (string.IsNullOrEmpty(value))
                continue;
            if (sb is null)
            {
                sb = new StringBuilder(url.Length + 32);
                sb.Append(url).Append(url.Contains('?') ? '&' : '?');
            }
            else
                sb.Append('&');
            sb.Append(UrlEncoder.Default.Encode(key)).Append('=').Append(UrlEncoder.Default.Encode(value));
        }
        return sb is null ? url : sb.ToString();
    }

    /// <summary>A safe local return path inside the UI, or the dashboard.</summary>
    public static string SafeReturn(string basePath, string? candidate)
    {
        var root = basePath == "/" ? "/" : basePath;
        if (string.IsNullOrEmpty(candidate) || candidate[0] != '/' || candidate.StartsWith("//", StringComparison.Ordinal))
            return root;
        for (var i = 0; i < candidate.Length; i++)
        {
            var c = candidate[i];
            if (c < 0x20 || c == 0x7f || c == '\\')
                return root;
        }
        if (!(candidate == root || candidate.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal) || candidate.StartsWith(root + "?", StringComparison.Ordinal)))
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
    private static readonly IReadOnlySet<Permission> Empty = new HashSet<Permission>();
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, IReadOnlySet<Permission> Permissions)> entries = new(StringComparer.Ordinal);

    public static string Key(PrincipalRef principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var scope = principal.Scope;
        var sb = new StringBuilder(128);
        AppendPart(sb, scope.TenantId);
        AppendPart(sb, scope.ProjectId);
        AppendPart(sb, scope.EnvironmentId);
        AppendPart(sb, scope.AppId);
        AppendPart(sb, scope.WorkspaceId.Value);

        switch (principal)
        {
            case UserPrincipal user:
                sb.Append("u;");
                AppendPart(sb, user.UserId.Value);
                AppendProvider(sb, user.IdentityProvider);
                AppendCount(sb, user.Teams.Count);
                if (user.Teams.Count > 0)
                    foreach (var team in user.Teams.Select(static t => t.Value).Order(StringComparer.Ordinal))
                        AppendPart(sb, team);
                break;
            case ApiClientPrincipal client:
                sb.Append("c;");
                AppendPart(sb, client.ClientId.Value);
                AppendProvider(sb, client.IdentityProvider);
                AppendCount(sb, 0);
                break;
            case AutomationPrincipal automation:
                sb.Append("a;");
                AppendPart(sb, automation.AutomationId);
                AppendProvider(sb, null);
                AppendCount(sb, 0);
                break;
            case SystemPrincipal system:
                sb.Append("s;");
                AppendPart(sb, system.Component);
                AppendProvider(sb, null);
                AppendCount(sb, 0);
                break;
        }

        AppendCount(sb, principal.ExternalRoles.Count);
        if (principal.ExternalRoles.Count > 0)
            foreach (var role in principal.ExternalRoles.Select(static r => r.ToLowerInvariant()).Order(StringComparer.Ordinal))
                AppendPart(sb, role);

        return sb.ToString();
    }

    private static void AppendPart(StringBuilder sb, string value) =>
        sb.Append(value.Length).Append(':').Append(value).Append(';');

    private static void AppendCount(StringBuilder sb, int count) =>
        sb.Append('#').Append(count).Append(';');

    private static void AppendProvider(StringBuilder sb, string? provider)
    {
        if (provider is null)
            sb.Append("-;");
        else
        {
            var normalized = provider.ToLowerInvariant();
            sb.Append('+').Append(normalized.Length).Append(':').Append(normalized).Append(';');
        }
    }

    public bool TryGet(string key, out IReadOnlySet<Permission> permissions)
    {
        if (entries.TryGetValue(key, out var entry) && entry.Expires > time.GetUtcNow())
        {
            permissions = entry.Permissions;
            return true;
        }
        permissions = Empty;
        return false;
    }

    public void Set(string key, IReadOnlySet<Permission> permissions, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;
        var now = time.GetUtcNow();
        if (entries.Count >= MaximumEntries)
            foreach (var pair in entries)
                if (pair.Value.Expires <= now)
                    entries.TryRemove(pair.Key, out _);
        if (entries.Count < MaximumEntries)
            entries[key] = (now + duration, permissions);
    }
}
