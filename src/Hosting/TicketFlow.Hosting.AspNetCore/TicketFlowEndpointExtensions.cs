using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketFlow.Hosting.AspNetCore.Endpoints;

namespace TicketFlow.Hosting.AspNetCore;

public static class TicketFlowEndpointExtensions
{
    /// <summary>
    /// Registers endpoint options, ProblemDetails and the options-based <see cref="ITicketFlowPartitionResolver"/> and
    /// <see cref="ITicketFlowPrincipalResolver"/> fallbacks (application registrations win). Feature services are registered by their own
    /// packages (<c>AddConversations()</c>, ...); map only the groups whose services the host registered.
    /// </summary>
    public static TicketFlowBuilder AddTicketFlowEndpoints(this TicketFlowBuilder builder, Action<TicketFlowEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<TicketFlowEndpointOptions>().Validate(options =>
        {
            options.Validate();
            return true;
        });
        if (configure is not null)
            services.Configure(configure);
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(options =>
        {
            if (!options.SerializerOptions.TypeInfoResolverChain.Contains(TicketFlowHttpJsonContext.Default))
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, TicketFlowHttpJsonContext.Default);
        });
        services.TryAddScoped<ITicketFlowPartitionResolver, OptionsTicketFlowPartitionResolver>();
        services.TryAddScoped<ITicketFlowPrincipalResolver, OptionsTicketFlowPrincipalResolver>();
        return builder;
    }

    /// <summary>
    /// Adds the TicketFlow OpenAPI document (<see cref="TicketFlowEndpointOptions.OpenApiDocumentName"/>, default <c>ticketflow</c>)
    /// containing only TicketFlow endpoints. Serve it with <see cref="MapTicketFlowOpenApi"/>.
    /// </summary>
    public static TicketFlowBuilder AddTicketFlowOpenApi(this TicketFlowBuilder builder, string documentName = "ticketflow")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);
        builder.Services.Configure<TicketFlowEndpointOptions>(options => options.OpenApiDocumentName = documentName);
        builder.Services.AddOpenApi(documentName, options =>
        {
            options.ShouldInclude = description => description.GroupName == documentName;
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "TicketFlow API";
                document.Info.Description = "TicketFlow support endpoints. Writes on existing records are fenced with If-Match revision tags; errors are RFC 9457 problem details with a 'code' extension.";
                return Task.CompletedTask;
            });
        });
        return builder;
    }

    /// <summary>Serves OpenAPI documents at <paramref name="pattern"/> (default <c>/ticketflow/openapi/{documentName}.json</c>).</summary>
    public static IEndpointConventionBuilder MapTicketFlowOpenApi(this IEndpointRouteBuilder endpoints, string pattern = "/ticketflow/openapi/{documentName}.json")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapOpenApi(pattern);
    }

    /// <summary>Maps every TicketFlow endpoint group (authenticated and anonymous). Returns the route builder for chaining.</summary>
    public static IEndpointRouteBuilder MapTicketFlowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapTicketFlowWorkspaces();
        endpoints.MapTicketFlowAccess();
        endpoints.MapTicketFlowContacts();
        endpoints.MapTicketFlowConversations();
        endpoints.MapTicketFlowRouting();
        endpoints.MapTicketFlowSla();
        endpoints.MapTicketFlowLiveChat();
        endpoints.MapTicketFlowAutomation();
        endpoints.MapTicketFlowHelpCenter();
        endpoints.MapTicketFlowSurveys();
        endpoints.MapTicketFlowAssist();
        endpoints.MapTicketFlowPublicPortal();
        endpoints.MapTicketFlowPublicSurveys();
        return endpoints;
    }

    /// <summary>Workspace settings, inboxes (and inbox agents), teams (and members) and business calendars.</summary>
    public static RouteGroupBuilder MapTicketFlowWorkspaces(this IEndpointRouteBuilder endpoints) => WorkspaceEndpoints.Map(Group(endpoints, "Workspaces"));

    /// <summary>The caller's effective permissions, roles, grants, field policies and break-glass activation.</summary>
    public static RouteGroupBuilder MapTicketFlowAccess(this IEndpointRouteBuilder endpoints) => AccessEndpoints.Map(Group(endpoints, "Access"));

    /// <summary>Contact search, CRUD, reveal, merge/unmerge, export and attribute definitions.</summary>
    public static RouteGroupBuilder MapTicketFlowContacts(this IEndpointRouteBuilder endpoints) => ContactEndpoints.Map(Group(endpoints, "Contacts"));

    /// <summary>Conversation lists, timelines, commands and attribute definitions.</summary>
    public static RouteGroupBuilder MapTicketFlowConversations(this IEndpointRouteBuilder endpoints) => ConversationEndpoints.Map(Group(endpoints, "Conversations"));

    /// <summary>Agent status and settings, queues, presence, reply locks and routing policies.</summary>
    public static RouteGroupBuilder MapTicketFlowRouting(this IEndpointRouteBuilder endpoints) => RoutingEndpoints.Map(Group(endpoints, "Routing"));

    /// <summary>Conversation SLA status, risk lists and SLA policies.</summary>
    public static RouteGroupBuilder MapTicketFlowSla(this IEndpointRouteBuilder endpoints) => SlaEndpoints.Map(Group(endpoints, "SLA"));

    /// <summary>Live-chat widget configurations, channel endpoints and the agent visitor view.</summary>
    public static RouteGroupBuilder MapTicketFlowLiveChat(this IEndpointRouteBuilder endpoints) => LiveChatEndpoints.Map(Group(endpoints, "Live chat"));

    /// <summary>Automation rules (dry run, replay, executions), macros and canned responses.</summary>
    public static RouteGroupBuilder MapTicketFlowAutomation(this IEndpointRouteBuilder endpoints) => AutomationEndpoints.Map(Group(endpoints, "Automation"));

    /// <summary>Help center administration: portals, collections, articles, revisions, previews, feedback and agent search.</summary>
    public static RouteGroupBuilder MapTicketFlowHelpCenter(this IEndpointRouteBuilder endpoints) => HelpCenterEndpoints.Map(Group(endpoints, "Help center"));

    /// <summary>Survey definitions and responses.</summary>
    public static RouteGroupBuilder MapTicketFlowSurveys(this IEndpointRouteBuilder endpoints) => SurveyEndpoints.Map(Group(endpoints, "Surveys"));

    /// <summary>Assistant features, settings and usage.</summary>
    public static RouteGroupBuilder MapTicketFlowAssist(this IEndpointRouteBuilder endpoints) => AssistEndpoints.Map(Group(endpoints, "Assist"));

    /// <summary>Anonymous public help-center portal (rate-limited when <see cref="TicketFlowEndpointOptions.AnonymousRateLimitingPolicy"/> is set).</summary>
    public static RouteGroupBuilder MapTicketFlowPublicPortal(this IEndpointRouteBuilder endpoints) => PublicEndpoints.MapPortal(PublicGroup(endpoints, "Public portal"));

    /// <summary>Anonymous survey lookup and response by token (rate-limited when configured). Tokens travel in request bodies.</summary>
    public static RouteGroupBuilder MapTicketFlowPublicSurveys(this IEndpointRouteBuilder endpoints) => PublicEndpoints.MapSurveys(PublicGroup(endpoints, "Public surveys"));

    private static TicketFlowEndpointOptions Options(IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetService<IOptions<TicketFlowEndpointOptions>>()?.Value
            ?? throw new InvalidOperationException("Call AddTicketFlowEndpoints() before mapping TicketFlow endpoints.");
        options.Validate();
        return options;
    }

    private static RouteGroupBuilder Group(IEndpointRouteBuilder endpoints, string tag)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = Options(endpoints);
        var group = endpoints.MapGroup(options.RoutePrefix.TrimEnd('/') + "/workspaces/{" + TicketFlowScopes.WorkspaceRouteValue + "}")
            .WithTags(tag)
            .WithGroupName(options.OpenApiDocumentName)
            .WithMetadata(new TicketFlowRequestSizeLimit(options.MaximumRequestBodyBytes))
            .AddEndpointFilterFactory(TicketFlowEndpointFilter.Create(options.MaximumRequestBodyBytes))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
        if (options.RequireAuthorization)
        {
            if (options.AuthorizationPolicy is { } policy)
                group.RequireAuthorization(policy);
            else
                group.RequireAuthorization();
        }
        return group;
    }

    private static RouteGroupBuilder PublicGroup(IEndpointRouteBuilder endpoints, string tag)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = Options(endpoints);
        var group = endpoints.MapGroup(options.PublicRoutePrefix.TrimEnd('/') + "/workspaces/{" + TicketFlowScopes.WorkspaceRouteValue + "}")
            .WithTags(tag)
            .WithGroupName(options.OpenApiDocumentName)
            .WithMetadata(new TicketFlowRequestSizeLimit(options.MaximumRequestBodyBytes))
            .AddEndpointFilterFactory(TicketFlowEndpointFilter.Create(options.MaximumRequestBodyBytes))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .AllowAnonymous();
        if (options.AnonymousRateLimitingPolicy is { } rateLimit)
            group.RequireRateLimiting(rateLimit);
        return group;
    }
}
