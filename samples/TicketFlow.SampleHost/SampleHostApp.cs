using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using TicketFlow.Access;
using TicketFlow.Access.Stores.Postgres;
using TicketFlow.Automation;
using TicketFlow.Automation.Stores.Postgres;
using TicketFlow.Channels;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Channels.LiveChat.Stores.Postgres;
using TicketFlow.Channels.Stores.Postgres;
using TicketFlow.Contacts;
using TicketFlow.Contacts.Stores.Postgres;
using TicketFlow.Conversations;
using TicketFlow.Conversations.Stores.Postgres;
using TicketFlow.Core;
using TicketFlow.HelpCenter;
using TicketFlow.HelpCenter.Stores.Postgres;
using TicketFlow.Hosting.AspNetCore;
using TicketFlow.Hosting.Worker;
using TicketFlow.Hosting.Worker.Stores.Postgres;
using System.Threading.RateLimiting;
using TicketFlow.Events;
using TicketFlow.Events.Stores.Postgres;
using TicketFlow.Identity;
using TicketFlow.Identity.Stores.Postgres;
using TicketFlow.Integrations.IdentityFlow;
using TicketFlow.Integrations.IdentityFlow.SignalR;
using TicketFlow.Integrations.Surveys;
using TicketFlow.Mcp;
using TicketFlow.Mcp.AspNetCore;
using TicketFlow.Realtime;
using TicketFlow.Realtime.SignalR;
using TicketFlow.Reporting;
using TicketFlow.Reporting.Stores.Postgres;
using TicketFlow.Routing;
using TicketFlow.Routing.Stores.Postgres;
using TicketFlow.Sla;
using TicketFlow.Sla.Stores.Postgres;
using TicketFlow.Surveys;
using TicketFlow.Surveys.Stores.Postgres;
using TicketFlow.UI.Blazor;
using TicketFlow.UI.Blazor.Infrastructure;
using TicketFlow.Workspaces;
using TicketFlow.Workspaces.Stores.Postgres;

namespace TicketFlow.SampleHost;

/// <summary>
/// Composes every TicketFlow feature into one ASP.NET Core host. Configuration: <c>TicketFlow:Storage</c> (<c>Postgres</c> or
/// <c>InMemory</c>), <c>ConnectionStrings:TicketFlow</c>, <c>TicketFlow:Partition</c>, <c>TicketFlow:Workspace</c>,
/// <c>TicketFlow:WidgetOrigins</c>, <c>TicketFlow:WidgetPath</c>, <c>TicketFlow:SeedOnStartup</c>, <c>TicketFlow:DevelopmentAuthentication</c>
/// and optional <c>IdentityFlow:VisitorTokens:{Authority,Audience}</c>.
/// </summary>
public static class SampleHostApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var configuration = builder.Configuration;
        var settings = SampleSettings.From(configuration);
        var services = builder.Services;
        // Adjustable clock (registered before the features so their TryAdd keeps it): the seeder backdates demo history with it.
        var clock = new SampleClock();
        services.AddSingleton(clock);
        services.AddSingleton<TimeProvider>(clock);

        var ticketFlow = services.AddTicketFlow()
            .AddAccessControl()
            .AddWorkspaces()
            .AddContacts()
            .AddConversations()
            .AddRouting()
            .AddSla()
            .AddAutomation()
            .AddHelpCenter()
            .AddSurveys(options =>
            {
                if (configuration["TicketFlow:SurveyTokenKey"] is { Length: > 0 } key)
                    options.TokenKey = Convert.FromBase64String(key);
            })
            .AddConversationSurveys()
            .AddReporting()
            .AddEvents()
            .AddStaffIdentity()
            .AddChannels(options => options.WorkerPrincipal = scope => new ApiClientPrincipal(scope, SampleSeeder.ChannelWorker))
            .AddRealtime()
            .AddLiveChat()
            .AddTicketFlowSignalR(options => options.VisitorPartition = settings.Partition)
            .AddTicketFlowEndpoints(options => options.Partition = settings.Partition)
            .AddTicketFlowOpenApi()
            .AddIdentityFlowRealtimePrincipals();
        // Callers mapped from IdentityFlow can never become one of TicketFlow's own worker clients.
        services.AddOptions<IdentityFlowPrincipalOptions>().PostConfigure<IServiceProvider>((options, provider) =>
            options.ReservedApiClientIds.UnionWith(TicketFlowServiceIdentities.Collect(provider).Select(id => id.Value)));
        if (settings.UsePostgres)
        {
            var connection = configuration.GetConnectionString("TicketFlow") ?? throw new InvalidOperationException("ConnectionStrings:TicketFlow is required for Postgres storage.");
            var dataSource = NpgsqlDataSource.Create(connection);
            services.AddSingleton(dataSource);
            ticketFlow.AddPostgresAccessStores(dataSource).AddPostgresWorkspaceStores(dataSource).AddPostgresContactStores(dataSource)
                .AddPostgresConversationStores(dataSource).AddPostgresRoutingStores(dataSource).AddPostgresSlaStores(dataSource)
                .AddPostgresAutomationStores(dataSource).AddPostgresHelpCenterStores(dataSource).AddPostgresSurveyStores(dataSource)
                .AddPostgresChannelStores(dataSource).AddPostgresLiveChatStores(dataSource).AddPostgresWorkerCheckpoints(dataSource)
                .AddPostgresReportingStores(dataSource).AddPostgresStaffIdentityStores(dataSource).AddPostgresEventStores(dataSource);
        }
        else
        {
            ticketFlow.AddInMemoryAccessStores().AddInMemoryWorkspaceStores().AddInMemoryContactStores().AddInMemoryConversationStores()
                .AddInMemoryRoutingStores().AddInMemorySlaStores().AddInMemoryAutomationStores().AddInMemoryHelpCenterStores()
                .AddInMemorySurveyStores().AddInMemoryChannelStores().AddInMemoryLiveChatStores().AddInMemoryReportingStores()
                .AddInMemoryStaffIdentityStores().AddInMemoryEventStores();
        }
        ticketFlow.AddTicketFlowWorkers();
        if (configuration["IdentityFlow:VisitorTokens:Authority"] is { Length: > 0 } authority)
            ticketFlow.AddIdentityFlowVisitorTokens(options =>
            {
                options.Authority = authority;
                options.Audiences.Add(configuration["IdentityFlow:VisitorTokens:Audience"] ?? "ticketflow-widget");
            });
        services.AddSingleton<SampleSurveyTokens>();
        services.AddSingleton<ISurveyInvitationSender, DevelopmentSurveyInvitationSender>();

        // The support UI (static SSR Blazor) at /support. It inherits whatever authentication the host uses (here the development
        // handler) and maps users with the same ITicketFlowPrincipalResolver as the REST endpoints (AddIdentityFlowRealtimePrincipals).
        var developmentAuthentication = configuration.GetValue<bool>("TicketFlow:DevelopmentAuthentication");
        services.AddTicketFlowBlazorUI(options =>
        {
            options.BasePath = "/support";
            options.ProductName = "Acme Support";
            options.Workspace = settings.Workspace;
            if (developmentAuthentication)
            {
                options.LoginPath = "/dev/login";
                options.ReturnUrlParameter = "returnUrl";
                options.LogoutPath = "/dev/logout";
            }
        });
        services.AddSingleton<ITicketFlowUserDirectory, SampleUserDirectory>();

        // MCP endpoint at /mcp (TicketFlow.Mcp toolsets inside the host's own MCP server). The caller is resolved by the same
        // partition/principal resolvers as the REST endpoints, so an API key maps to an ApiClientPrincipal holding ordinary grants
        // (the seed grants mcp-assistant the built-in Agent role). Authentication and the authorization policy stay with the host.
        services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTicketFlowTools(options => options.DefaultWorkspace = settings.Workspace.Value)
            .WithTicketFlowHostPrincipals();

        var health = services.AddHealthChecks().AddTicketFlowWorkers();
        if (settings.UsePostgres)
            health.AddCheck<PostgresHealthCheck>("postgres");

        // Authentication. The development handler is for local use only and refuses to run outside Development. Production hosts plug in
        // IdentityFlow (cookies, JWT bearer, API keys, PATs); AddIdentityFlowRealtimePrincipals() maps those users to TicketFlow principals.
        if (configuration.GetValue<bool>("TicketFlow:DevelopmentAuthentication"))
        {
            if (!builder.Environment.IsDevelopment())
                throw new InvalidOperationException("TicketFlow:DevelopmentAuthentication is only allowed in the Development environment.");
            services.AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);
        }
        else
            services.AddAuthentication();
        services.AddAuthorization();
        // Anonymous endpoints (public portal, survey responses, visitor hub, uploads) are limited per client IP. Behind a proxy, configure
        // forwarded headers first so RemoteIpAddress is the client's address.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("ticketflow-anonymous", http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
        });
        services.Configure<TicketFlowEndpointOptions>(options => options.AnonymousRateLimitingPolicy = "ticketflow-anonymous");
        services.AddSingleton(settings);

        var app = builder.Build();
        // Static web assets (support UI CSS/JS, BlazorBlueprint, _framework/blazor.web.js). Test hosts run without the manifest.
        var assets = Path.Combine(AppContext.BaseDirectory, "TicketFlow.SampleHost.staticwebassets.endpoints.json");
        if (File.Exists(assets))
            app.MapStaticAssets(assets);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapTicketFlowWorkspaces();
        app.MapTicketFlowAccess();
        app.MapTicketFlowContacts();
        app.MapTicketFlowConversations();
        app.MapTicketFlowRouting();
        app.MapTicketFlowSla();
        app.MapTicketFlowLiveChat();
        app.MapTicketFlowAutomation();
        app.MapTicketFlowHelpCenter();
        app.MapTicketFlowSurveys();
        app.MapTicketFlowPublicPortal();
        app.MapTicketFlowPublicSurveys();
        app.MapTicketFlowOpenApi();
        app.MapTicketFlowMcp("/mcp");
        var (agent, visitor) = app.MapTicketFlowRealtime();
        agent.RequireAuthorization();
        visitor.RequireRateLimiting("ticketflow-anonymous");
        app.MapLiveChatAttachmentUpload().RequireRateLimiting("ticketflow-anonymous");
        app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthResponses.WriteAsync });

        var widget = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, settings.WidgetPath));
        if (Directory.Exists(widget))
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(widget), RequestPath = "/widget" });
        app.MapGet("/demo", (HttpContext http) => Results.Content(DemoPage.Render(http, settings), "text/html")).ExcludeFromDescription();
        app.MapTicketFlowBlazorUI();
        if (developmentAuthentication)
            DevelopmentLogin.Map(app);
        app.MapGet("/", () => Results.Redirect("/support")).ExcludeFromDescription();
        return app;
    }

    /// <summary>Applies every PostgreSQL migration (explicit, idempotent) and seeds when requested or configured.</summary>
    public static async Task InitializeAsync(WebApplication app, bool seed, CancellationToken cancellationToken = default)
    {
        var settings = app.Services.GetRequiredService<SampleSettings>();
        if (settings.UsePostgres)
        {
            var services = app.Services;
            await services.GetRequiredService<PostgresAccessStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresWorkspaceStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresContactStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresConversationStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresRoutingStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresSlaStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresAutomationStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresHelpCenterStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresSurveyStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresChannelStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresLiveChatStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresWorkerCheckpointStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresReportingStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresStaffIdentityStore>().InitializeAsync(cancellationToken);
            await services.GetRequiredService<PostgresEventStore>().InitializeAsync(cancellationToken);
        }
        if (seed || settings.SeedOnStartup)
            await SampleSeeder.SeedAsync(app.Services, settings, cancellationToken);
    }
}

public sealed record SampleSettings(bool UsePostgres, AccessPartition Partition, WorkspaceId Workspace, IReadOnlyList<string> WidgetOrigins, string WidgetPath, bool SeedOnStartup)
{
    public TicketScope Scope => Partition.InWorkspace(Workspace);

    public static SampleSettings From(IConfiguration configuration)
    {
        var storage = configuration["TicketFlow:Storage"] ?? "Postgres";
        if (storage is not ("Postgres" or "InMemory"))
            throw new InvalidOperationException("TicketFlow:Storage must be Postgres or InMemory.");
        var partition = new AccessPartition(configuration["TicketFlow:Partition:Tenant"] ?? "sample", configuration["TicketFlow:Partition:Project"] ?? "support",
            configuration["TicketFlow:Partition:Environment"] ?? "development", configuration["TicketFlow:Partition:App"] ?? "web");
        var origins = configuration.GetSection("TicketFlow:WidgetOrigins").Get<string[]>() is { Length: > 0 } configured ? configured : ["http://localhost:5080"];
        return new SampleSettings(storage == "Postgres", partition, new WorkspaceId(configuration["TicketFlow:Workspace"] ?? "acme"), origins,
            configuration["TicketFlow:WidgetPath"] ?? "../../clients/widget/dist", configuration.GetValue<bool>("TicketFlow:SeedOnStartup"));
    }
}

internal sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException error)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", data: new Dictionary<string, object> { ["error"] = error.GetType().Name });
        }
    }
}

internal static class HealthResponses
{
    /// <summary>Status and per-check status only (worker data lists loop names and failure counts, never customer data).</summary>
    public static Task WriteAsync(HttpContext http, HealthReport report)
    {
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(entry => entry.Key, entry => new { status = entry.Value.Status.ToString(), data = entry.Value.Data }),
        });
    }
}
