using System.Reflection;
using BlazorBlueprint.Components;
using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;
using TicketFlow.UI.Blazor;
using TicketFlow.UI.Blazor.Infrastructure;
using TicketFlow.UI.Blazor.Theming;

namespace TicketFlow.UI.Blazor.Tests;

/// <summary>Builds bUnit contexts and UI sessions without a running host.</summary>
internal static class UiTestSupport
{
    public static readonly TicketScope Scope = new AccessPartition("tenant", "project", "test", "web").InWorkspace(new WorkspaceId("acme"));

    public static BunitContext Context(Action<IServiceCollection>? services = null)
    {
        var context = new BunitContext();
        context.Services.AddBlazorBlueprintComponents();
        context.Services.AddOptions<TicketFlowBlazorUIOptions>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<UiSensitivePermissionCache>();
        services?.Invoke(context.Services);
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        return context;
    }

    /// <summary>A ready session for user <c>agent</c> holding <paramref name="permissions"/>; the feature services in <paramref name="features"/> resolve to inert stubs.</summary>
    public static TicketFlowUISession Session(BunitContext context, IEnumerable<Permission> permissions, string path = "", TicketFlowThemePreference? theme = null,
        string? cookie = null, TicketFlowBlazorUIOptions? options = null)
    {
        var resolved = options ?? context.Services.GetRequiredService<IOptions<TicketFlowBlazorUIOptions>>().Value;
        var http = new DefaultHttpContext { RequestServices = context.Services };
        http.Request.Path = resolved.BasePath + (path.Length == 0 ? "" : "/" + path);
        if (cookie is not null)
            http.Request.Headers.Cookie = cookie;
        var principal = new UserPrincipal(Scope, new UserId("agent"));
        var session = new TicketFlowUISession(http, resolved, UiSessionState.Ready, Scope, principal, [.. permissions], theme ?? UiTheme.Read(http, resolved))
        {
            Antiforgery = ("__RequestVerificationToken", "token"),
        };
        return session;
    }

    /// <summary>Registers inert implementations of feature service interfaces (presence only drives section visibility).</summary>
    public static void AddStubs(this IServiceCollection services, params Type[] interfaces)
    {
        foreach (var type in interfaces)
        {
            var create = typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!.MakeGenericMethod(type, typeof(InertProxy));
            var instance = create.Invoke(null, null)!;
            services.AddSingleton(type, instance);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1852:Seal internal types", Justification = "DispatchProxy requires a non-sealed proxy base type.")]
    public class InertProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException("Inert stub.");
    }
}
