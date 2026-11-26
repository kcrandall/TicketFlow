using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TicketFlow.Assist;
using static TicketFlow.Hosting.AspNetCore.Tests.EndpointHost;

namespace TicketFlow.Hosting.AspNetCore.Tests;

/// <summary>Sweeps every mapped TicketFlow route through the live endpoint data source.</summary>
[TestClass]
public sealed partial class RouteSweepTests
{
    private const string Query = "?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z&portal=x&locale=en&q=x";
    private const string Prefix = "/ticketflow/api/workspaces/{workspaceId}";

    /// <summary>PUT/DELETE routes whose revision fence is optional (commands on the current revision, presence, own status).</summary>
    private static readonly HashSet<string> OptionalFence = new(StringComparer.Ordinal)
    {
        "PUT /conversations/{conversationId}/assignee", "PUT /conversations/{conversationId}/team", "PUT /conversations/{conversationId}/status",
        "PUT /conversations/{conversationId}/priority", "PUT /conversations/{conversationId}/presence", "DELETE /conversations/{conversationId}/presence",
        "PUT /routing/agents/me/status", "PUT /routing/agents/{userId}/status", "DELETE /inboxes/{inboxId}/agents/{userId}",
    };

    [TestMethod]
    public async Task PrincipalsWithoutGrantsGetNoDataFromAnyAuthenticatedRoute()
    {
        await using var host = await CreateAsync(services: WithAssist);
        var stranger = host.Client("stranger");
        var routes = Routes(host).ToList();
        Assert.IsGreaterThan(100, routes.Count);
        var failures = new List<string>();
        foreach (var (method, pattern) in routes)
        {
            var query = !string.Equals(method, "GET", StringComparison.Ordinal) ? string.Empty : Query + (pattern.EndsWith("/aggregate", StringComparison.Ordinal) ? "&kind=csat" : string.Empty);
            using var request = new HttpRequestMessage(new HttpMethod(method), Concrete(pattern) + query);
            if (method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            var response = await stranger.SendAsync(request);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync();
            var allowed = string.Equals(method, "GET"
, StringComparison.Ordinal) ? status is 403 or 404 || status == 200 && (IsEmptyCollection(body) || pattern.EndsWith("/me", StringComparison.Ordinal))
                : status is 400 or 403 or 404 or 422;
            if (!allowed)
                failures.Add($"{method} {pattern} -> {status}");
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task EveryUnfencedPutAndDeleteRequiresIfMatch()
    {
        await using var host = await CreateAsync(services: WithAssist);
        var owner = host.Client();
        var failures = new List<string>();
        foreach (var (method, pattern) in Routes(host).Where(route => route.Method is "PUT" or "DELETE"))
        {
            var relative = pattern[Prefix.Length..];
            if (OptionalFence.Contains($"{method} {relative}"))
                continue;
            using var request = new HttpRequestMessage(new HttpMethod(method), Concrete(pattern));
            if (string.Equals(method, "PUT", StringComparison.Ordinal))
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await owner.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.PreconditionRequired)
                failures.Add($"{method} {relative} -> {(int)response.StatusCode}");
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task OpenApiDeclaresSuccessAndSharedProblemResponsesForEveryOperation()
    {
        await using var host = await CreateAsync();
        var document = await JsonAsync(await host.Client(user: null).GetAsync("/ticketflow/openapi/ticketflow.json"));
        var failures = new List<string>();
        foreach (var path in document.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject().Where(item => item.Name is "get" or "post" or "put" or "patch" or "delete"))
            {
                var responses = operation.Value.GetProperty("responses").EnumerateObject().Select(item => item.Name).ToHashSet();
                if (!responses.Any(code => code.StartsWith('2')))
                    failures.Add($"{operation.Name} {path.Name}: no success response");
                string[] problems = path.Name.StartsWith("/ticketflow/public", StringComparison.Ordinal) ? ["400", "404", "422", "429"] : ["400", "401", "403", "404", "422"];
                foreach (var code in problems.Where(code => !responses.Contains(code)))
                    failures.Add($"{operation.Name} {path.Name}: missing {code}");
            }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    private static void WithAssist(Microsoft.Extensions.DependencyInjection.IServiceCollection services) =>
        new TicketFlowBuilder(services).AddAssist().AddInMemoryAssistStores();

    private static IEnumerable<(string Method, string Pattern)> Routes(EndpointHost host) =>
        ((IEndpointRouteBuilder)host.App).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/ticketflow/api/", StringComparison.Ordinal) == true)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method => (method, endpoint.RoutePattern.RawText!)))
            .Distinct().OrderBy(route => route.Item2, StringComparer.Ordinal);

    private static string Concrete(string pattern) => Parameter().Replace(pattern, match => match.Groups["name"].Value switch
    {
        "workspaceId" => "workspace",
        _ when string.Equals(match.Groups["constraint"].Value, "guid", StringComparison.Ordinal) => Guid.NewGuid().ToString(),
        _ when string.Equals(match.Groups["constraint"].Value, "int", StringComparison.Ordinal) => "1",
        _ => "x",
    });

    private static bool IsEmptyCollection(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() == 0
            : root.TryGetProperty("items", out var items) && items.GetArrayLength() == 0 || root.TryGetProperty("rows", out var rows) && rows.GetArrayLength() == 0;
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)(?::(?<constraint>[a-z]+))?\}", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Parameter();
}
