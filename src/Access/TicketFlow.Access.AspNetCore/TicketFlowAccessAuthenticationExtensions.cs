using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketFlow.Core;

namespace TicketFlow.Access.AspNetCore;

/// <summary>ASP.NET Core authentication and discovery endpoint extensions for TicketFlow access control.</summary>
public static class TicketFlowAccessAuthenticationExtensions
{
    public const string DefaultHybridScheme = "TicketFlowHybrid";

    private const string ServiceAccountKeyPrefix = ServiceAccountService.KeyPrefixMarker;

    public static AuthenticationBuilder AddTicketFlowExternalIdentityAuthentication(
        this AuthenticationBuilder builder,
        string authenticationScheme = ExternalIdentityAuthenticationOptions.DefaultScheme,
        Action<ExternalIdentityAuthenticationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        return builder.AddScheme<ExternalIdentityAuthenticationOptions, ExternalIdentityAuthenticationHandler>(
            authenticationScheme,
            configure ?? (static _ => { }));
    }

    public static AuthenticationBuilder AddTicketFlowExternalIdentityAuthentication(
        this IServiceCollection services,
        Action<ExternalIdentityAuthenticationOptions>? configure = null,
        string authenticationScheme = ExternalIdentityAuthenticationOptions.DefaultScheme,
        bool setAsDefault = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        var builder = setAsDefault
            ? services.AddAuthentication(options =>
            {
                options.DefaultScheme = authenticationScheme;
                options.DefaultAuthenticateScheme = authenticationScheme;
                options.DefaultChallengeScheme = authenticationScheme;
            })
            : services.AddAuthentication();

        return builder.AddTicketFlowExternalIdentityAuthentication(authenticationScheme, configure);
    }

    public static AuthenticationBuilder AddTicketFlowApiKeyAuthentication(
        this AuthenticationBuilder builder,
        string authenticationScheme = TicketFlowApiKeyAuthenticationOptions.DefaultScheme,
        Action<TicketFlowApiKeyAuthenticationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        return builder.AddScheme<TicketFlowApiKeyAuthenticationOptions, TicketFlowApiKeyAuthenticationHandler>(
            authenticationScheme,
            configure ?? (static _ => { }));
    }

    public static AuthenticationBuilder AddTicketFlowApiKeyAuthentication(
        this IServiceCollection services,
        Action<TicketFlowApiKeyAuthenticationOptions>? configure = null,
        string authenticationScheme = TicketFlowApiKeyAuthenticationOptions.DefaultScheme,
        bool setAsDefault = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);

        var builder = setAsDefault
            ? services.AddAuthentication(options =>
            {
                options.DefaultScheme = authenticationScheme;
                options.DefaultAuthenticateScheme = authenticationScheme;
                options.DefaultChallengeScheme = authenticationScheme;
            })
            : services.AddAuthentication();

        return builder.AddTicketFlowApiKeyAuthentication(authenticationScheme, configure);
    }

    public static AuthenticationBuilder AddTicketFlowHybridAuthentication(
        this IServiceCollection services,
        string primaryScheme,
        Action<TicketFlowApiKeyAuthenticationOptions>? configureApiKey = null,
        string hybridScheme = DefaultHybridScheme,
        string apiKeyScheme = TicketFlowApiKeyAuthenticationOptions.DefaultScheme,
        bool setAsDefault = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryScheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(hybridScheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyScheme);

        var builder = setAsDefault
            ? services.AddAuthentication(options =>
            {
                options.DefaultScheme = hybridScheme;
                options.DefaultAuthenticateScheme = hybridScheme;
                options.DefaultChallengeScheme = hybridScheme;
            })
            : services.AddAuthentication();

        return builder.AddTicketFlowHybridAuthentication(primaryScheme, configureApiKey, hybridScheme, apiKeyScheme);
    }

    public static AuthenticationBuilder AddTicketFlowHybridAuthentication(
        this AuthenticationBuilder builder,
        string primaryScheme,
        Action<TicketFlowApiKeyAuthenticationOptions>? configureApiKey = null,
        string hybridScheme = DefaultHybridScheme,
        string apiKeyScheme = TicketFlowApiKeyAuthenticationOptions.DefaultScheme)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryScheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(hybridScheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyScheme);

        builder.AddTicketFlowApiKeyAuthentication(apiKeyScheme, configureApiKey);
        builder.AddPolicyScheme(
            hybridScheme,
            displayName: "TicketFlow Hybrid Authentication",
            options =>
            {
                options.ForwardDefaultSelector = httpContext =>
                {
                    var monitor = httpContext.RequestServices.GetService<IOptionsMonitor<TicketFlowApiKeyAuthenticationOptions>>();
                    var apiKeyOptions = monitor?.Get(apiKeyScheme);
                    var headerName = apiKeyOptions?.HeaderName ?? TicketFlowApiKeyAuthenticationOptions.DefaultHeaderName;
                    var allowBearer = apiKeyOptions?.AllowBearerToken ?? true;

                    return TicketFlowApiKeyAuthenticationHandler.HasApiKeyCredentials(
                        httpContext.Request,
                        headerName,
                        allowBearer,
                        requiredBearerPrefix: ServiceAccountKeyPrefix)
                        ? apiKeyScheme
                        : primaryScheme;
                };
            });

        return builder;
    }

    public static RouteHandlerBuilder MapTicketFlowRoleCatalogEndpoint(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/.well-known/ticketflow-roles",
        Func<HttpContext, ValueTask<TicketScope?>>? resolveScope = null,
        TicketScope? defaultScope = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return endpoints.MapGet(
            pattern,
            async (
                HttpContext httpContext,
                IAccessPrincipalResolver principalResolver,
                IRoleFederationService roleFederationService) =>
            {
                if (httpContext.User.Identity?.IsAuthenticated != true)
                    return Results.Unauthorized();

                var scope = resolveScope is not null
                    ? await resolveScope(httpContext).ConfigureAwait(false)
                    : TicketFlowClaimsScopeResolver.FromClaims(httpContext, defaultScope);
                if (scope is null)
                    return Results.BadRequest(new { error = "TicketFlow scope could not be resolved." });

                var principal = await principalResolver
                    .ResolveAsync(httpContext.User, scope, httpContext.RequestAborted)
                    .ConfigureAwait(false);
                if (principal is null)
                    return Results.Unauthorized();

                var result = await roleFederationService
                    .GetRoleCatalogAsync(principal, httpContext.RequestAborted)
                    .ConfigureAwait(false);

                return result switch
                {
                    TicketValue<FederatedRoleCatalog> ok => Results.Ok(ok.Value),
                    TicketFailure failure => failure.Kind switch
                    {
                        TicketFailureKind.Forbidden => Results.Json(
                            new { error = failure.Errors.FirstOrDefault() ?? "Forbidden.", kind = failure.Kind.ToString(), code = failure.Code },
                            statusCode: StatusCodes.Status403Forbidden),
                        _ => Results.BadRequest(
                            new { error = failure.Errors.FirstOrDefault() ?? "Invalid request.", kind = failure.Kind.ToString(), code = failure.Code }),
                    },
                };
            });
    }
}
