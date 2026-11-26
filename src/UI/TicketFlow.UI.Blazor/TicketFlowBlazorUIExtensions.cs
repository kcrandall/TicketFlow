using BlazorBlueprint.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Core;
using TicketFlow.Reporting;
using TicketFlow.UI.Blazor.Components;
using TicketFlow.UI.Blazor.Infrastructure;
using TicketFlow.UI.Blazor.Theming;

namespace TicketFlow.UI.Blazor;

public static class TicketFlowBlazorUIServiceCollectionExtensions
{
    /// <summary>
    /// Registers the TicketFlow Blazor UI: options, Razor components (static SSR), antiforgery and BlazorBlueprint components. The host
    /// keeps its own authentication; principals come from the registered <c>ITicketFlowPrincipalResolver</c> and
    /// <c>ITicketFlowPartitionResolver</c> (for example <c>AddTicketFlowEndpoints()</c> plus <c>AddIdentityFlowPrincipals()</c>).
    /// </summary>
    public static IServiceCollection AddTicketFlowBlazorUI(this IServiceCollection services, Action<TicketFlowBlazorUIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<TicketFlowBlazorUIOptions>().Validate(options =>
        {
            options.Validate();
            return true;
        });
        if (configure is not null)
            services.Configure(configure);
        services.AddRazorComponents();
        services.AddAntiforgery();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<UiSensitivePermissionCache>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(BlazorBlueprintMarker)))
        {
            services.AddSingleton<BlazorBlueprintMarker>();
            services.AddBlazorBlueprintComponents();
        }
        return services;
    }

    /// <inheritdoc cref="AddTicketFlowBlazorUI(IServiceCollection, Action{TicketFlowBlazorUIOptions}?)"/>
    public static TicketFlowBuilder AddTicketFlowBlazorUI(this TicketFlowBuilder builder, Action<TicketFlowBlazorUIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddTicketFlowBlazorUI(configure);
        return builder;
    }

    private sealed class BlazorBlueprintMarker;
}

public static class TicketFlowBlazorUIEndpointExtensions
{
    /// <summary>
    /// Maps the UI under <see cref="TicketFlowBlazorUIOptions.BasePath"/>: <c>GET</c> pages rendered with static SSR (unless
    /// <paramref name="pages"/> is false because the host renders <c>TicketFlowUI</c> inside its own Blazor layout), <c>POST</c> form
    /// actions (antiforgery-validated, post/redirect/get) and the report CSV export. Endpoints use order -1 so they win over a host's
    /// catch-all Razor component route at the same path. Returns the group so hosts can add conventions (for example
    /// <c>RequireAuthorization()</c> or rate limiting).
    /// </summary>
    public static RouteGroupBuilder MapTicketFlowBlazorUI(this IEndpointRouteBuilder endpoints, bool pages = true)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<TicketFlowBlazorUIOptions>>().Value;
        options.Validate();
        var group = endpoints.MapGroup(options.BasePath).ExcludeFromDescription();
        group.MapGet("/reports/export.csv", (Delegate)ExportAsync).WithOrder(-1);
        if (pages)
        {
            group.MapGet("/", (Delegate)RenderAsync).WithOrder(-1);
            group.MapGet("/{**path}", (Delegate)RenderAsync).WithOrder(-1);
        }
        group.MapPost("/", (Delegate)PostAsync).DisableAntiforgery().WithOrder(-1);
        group.MapPost("/{**path}", (Delegate)PostAsync).DisableAntiforgery().WithOrder(-1);
        return group;
    }

    private static async Task<IResult> RenderAsync(HttpContext http)
    {
        var session = await TicketFlowUISession.ResolveAsync(http);
        if (session.State == UiSessionState.Unauthenticated)
            return SignIn(http, session.Options);
        session.Flash = UiFlash.Take(http, session.Options);
        return Render(session, UiRoutes.Match(session.Path), pageState: null);
    }

    private static async Task<IResult> PostAsync(HttpContext http)
    {
        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(http))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The form expired. Reload the page and try again.");
        if (http.User.Identity?.IsAuthenticated != true)
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Unauthorized();
        }
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var options = http.RequestServices.GetRequiredService<IOptions<TicketFlowBlazorUIOptions>>().Value;
        var returnTo = UiUrls.SafeReturn(options.BasePath, form["_return"].ToString() is { Length: > 0 } posted ? posted : http.Request.Path + http.Request.QueryString);

        if (string.Equals(form["_action"].ToString(), "theme.set", StringComparison.Ordinal))
        {
            var theme = TicketFlowThemePreference.Parse($"{form["theme"]}.{form["mode"]}", options.Themes);
            if (theme is not null)
                UiTheme.Write(http, options, theme);
            return TypedResults.Redirect(returnTo);
        }
        var session = await TicketFlowUISession.ResolveAsync(http);
        if (session.State == UiSessionState.Unauthenticated)
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Unauthorized();
        }
        var route = UiRoutes.Match(session.Path);
        // Authorization of the page comes first and never depends on the posted form: a section the viewer cannot open renders
        // "Access denied" (403) and its action does not run. Services still authorize the command itself.
        if (route is null || !UiNavigation.Allows(session, route))
            return Render(session, route, pageState: null);
        var action = form["_action"].ToString();
        if (UiActions.Find(action) is not { } handler || !string.Equals(UiNavigation.ActionSection(action), route.Section, StringComparison.Ordinal))
            return TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown action for this page.");

        var result = await UiActions.RunAsync(handler, new UiActionContext(session, form));
        if (result.Outcome.Succeeded && result.PageState is null)
        {
            UiFlash.Write(http, session.Options, result.Outcome);
            var target = result.Outcome.Redirect is { } redirect ? session.Url(redirect) : returnTo;
            return TypedResults.Redirect(UiUrls.SafeReturn(session.Options.BasePath, target));
        }
        session.Flash = result.Outcome;
        session.Form = form;
        return Render(session, route, result.PageState, result.Outcome.Succeeded ? StatusCodes.Status200OK : StatusFor(result.Outcome.Kind));
    }

    private static RazorComponentResult<TicketFlowDocument> Render(TicketFlowUISession session, UiRoute? route, object? pageState, int? status = null)
    {
        var http = session.Http;
        http.Response.Headers.CacheControl = "no-store";
        session.PageState = pageState;
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        session.Antiforgery = (tokens.FormFieldName, tokens.RequestToken ?? string.Empty);
        var code = route is null ? StatusCodes.Status404NotFound
            : !UiNavigation.Allows(session, route) ? StatusCodes.Status403Forbidden
            : status ?? StatusCodes.Status200OK;
        return new RazorComponentResult<TicketFlowDocument>(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [nameof(TicketFlowDocument.Session)] = session,
            [nameof(TicketFlowDocument.Route)] = route,
            [nameof(TicketFlowDocument.StatusCode)] = code,
        })
        {
            StatusCode = code,
        };
    }

    private static int StatusFor(UiOutcomeKind kind) => kind switch
    {
        UiOutcomeKind.Forbidden => StatusCodes.Status403Forbidden,
        UiOutcomeKind.NotFound => StatusCodes.Status404NotFound,
        UiOutcomeKind.Conflict => StatusCodes.Status409Conflict,
        UiOutcomeKind.Invalid => StatusCodes.Status422UnprocessableEntity,
        UiOutcomeKind.Rejected => StatusCodes.Status409Conflict,
        UiOutcomeKind.Success or UiOutcomeKind.Info => StatusCodes.Status200OK,
        _ => StatusCodes.Status400BadRequest,
    };

    private static IResult SignIn(HttpContext http, TicketFlowBlazorUIOptions options)
    {
        if (HasApiAuthHeaders(http))
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Unauthorized();
        }
        if (options.LoginPath is not { Length: > 0 } login)
        {
            http.Response.Headers.CacheControl = "no-store";
            return TypedResults.Challenge();
        }
        var returnUrl = http.Request.PathBase + http.Request.Path + http.Request.QueryString;
        return TypedResults.Redirect(UiUrls.Combine(login, string.Empty, (options.ReturnUrlParameter, returnUrl)));
    }

    private static bool HasApiAuthHeaders(HttpContext http) =>
        http.Request.Headers.ContainsKey("X-Api-Key") || http.Request.Headers.ContainsKey("Authorization");

    private static async Task<IResult> ExportAsync(HttpContext http, string? from, string? to)
    {
        var session = await TicketFlowUISession.ResolvePrincipalAsync(http);
        if (session.State == UiSessionState.Unauthenticated)
            return SignIn(http, session.Options);
        if (session.State != UiSessionState.Ready || session.Service<IReportingService>() is not { } reporting)
            return TypedResults.Forbid();
        var (start, end) = UiFormat.Range(from, to, TimeProvider.System.GetUtcNow());
        var fileName = $"ticketflow-report-{start:yyyyMMdd}-{end:yyyyMMdd}.csv";
        await using var writer = new LazyCsvResponseWriter(http, fileName);
        switch (await reporting.ExportAsync(session.Principal, new ReportExportQuery(new ReportQuery(start, end), 100_000), writer, http.RequestAborted))
        {
            case TicketValue<ReportExportSummary>:
                writer.EnsureStarted();
                await writer.FlushAsync(http.RequestAborted);
                return TypedResults.Empty;
            case TicketFailure:
                return TypedResults.Forbid();
        }
        return TypedResults.Forbid();
    }

    private sealed class LazyCsvResponseWriter(HttpContext http, string fileName) : TextWriter
    {
        private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        private StreamWriter? inner;

        public override System.Text.Encoding Encoding => Utf8NoBom;

        public void EnsureStarted()
        {
            if (inner is not null)
                return;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.ContentType = "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            inner = new StreamWriter(http.Response.Body, Utf8NoBom, bufferSize: 16_384, leaveOpen: true);
        }

        private StreamWriter Writer()
        {
            EnsureStarted();
            return inner!;
        }

        public override void Write(char value) => Writer().Write(value);
        public override void Write(string? value) => Writer().Write(value);
        public override void Write(char[] buffer, int index, int count) => Writer().Write(buffer, index, count);
        public override void Write(ReadOnlySpan<char> buffer) => Writer().Write(buffer);
        public override Task WriteAsync(char value) => Writer().WriteAsync(value);
        public override Task WriteAsync(string? value) => Writer().WriteAsync(value);
        public override Task WriteAsync(char[] buffer, int index, int count) => Writer().WriteAsync(buffer, index, count);
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => Writer().WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(System.Text.StringBuilder? value, CancellationToken cancellationToken = default) => Writer().WriteAsync(value, cancellationToken);
        public override Task WriteLineAsync() => Writer().WriteLineAsync();
        public override Task WriteLineAsync(char value) => Writer().WriteLineAsync(value);
        public override Task WriteLineAsync(string? value) => Writer().WriteLineAsync(value);
        public override Task WriteLineAsync(char[] buffer, int index, int count) => Writer().WriteLineAsync(buffer, index, count);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => Writer().WriteLineAsync(buffer, cancellationToken);
        public override Task WriteLineAsync(System.Text.StringBuilder? value, CancellationToken cancellationToken = default) => Writer().WriteLineAsync(value, cancellationToken);
        public override Task FlushAsync() => inner is null ? Task.CompletedTask : inner.FlushAsync();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner is null ? Task.CompletedTask : inner.FlushAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var writer = inner;
                inner = null;
                writer?.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            var writer = inner;
            inner = null;
            if (writer is not null)
                await writer.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
