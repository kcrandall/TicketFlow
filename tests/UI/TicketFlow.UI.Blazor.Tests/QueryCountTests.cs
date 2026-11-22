using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.SampleHost;

namespace TicketFlow.UI.Blazor.Tests;

/// <summary>Counts store calls while rendering the conversations page: the work per page must not grow with the number of rows.</summary>
[TestClass]
public sealed class QueryCountTests
{
    [TestMethod]
    public async Task ConversationsPageDoesNotIssueQueriesPerRow()
    {
        var grants = Counting<IAccessGrantStore>.Wrap(new InMemoryAccessGrantStore());
        await using var app = SampleHostApp.Build(["--environment", "Development"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["TicketFlow:Storage"] = "InMemory", ["TicketFlow:DevelopmentAuthentication"] = "true" });
            builder.Services.AddSingleton(grants.Proxy);
        });
        await SampleHostApp.InitializeAsync(app, seed: true);
        await app.StartAsync();
        var client = app.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", "admin");
        await client.GetAsync("/support/conversations");   // warm-up (sensitive permission snapshot is cached afterwards)

        grants.Reset();
        var response = await client.GetAsync("/support/conversations?status=all");
        var html = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var rows = html.Split("class=\"tf-inbox-row\"").Length - 1;
        Assert.IsGreaterThanOrEqualTo(20, rows, "The page renders a full page of conversations.");
        var loads = grants.Count(nameof(IAccessGrantStore.ListForSubjectsAsync));
        Assert.IsLessThanOrEqualTo(12, loads, $"Grant loads must be constant per page, not per row ({rows} rows, {loads} loads).");
        await app.StopAsync();
    }

    /// <summary>A call-counting proxy over a store interface.</summary>
    public class Counting<T> : DispatchProxy where T : class
    {
        private T inner = default!;
        private readonly Dictionary<string, int> counts = [];

        public T Proxy => (this as T)!;

        public static Counting<T> Wrap(T inner)
        {
            var proxy = Create<T, Counting<T>>();
            var counting = (Counting<T>)(object)proxy;
            counting.inner = inner;
            return counting;
        }

        public void Reset()
        {
            lock (counts)
                counts.Clear();
        }

        public int Count(string method)
        {
            lock (counts)
                return counts.GetValueOrDefault(method);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            lock (counts)
                counts[targetMethod!.Name] = counts.GetValueOrDefault(targetMethod.Name) + 1;
            try
            {
                return targetMethod.Invoke(inner, args);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                throw error.InnerException;
            }
        }
    }
}
