using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Automation;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Hosting.Worker.StoreContracts;
using TicketFlow.Sla;
using TicketFlow.Surveys;
using TicketFlow.Testing;
using TicketFlow.Workspaces;

namespace TicketFlow.Hosting.Worker.Tests;

[TestClass]
public sealed class WorkerLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task CustomLoopRunsRepeatedlyAndStopsGracefully()
    {
        var counter = new Counter();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(counter);
        builder.Services.AddTicketFlow().AddTicketFlowWorkerLoop<Counter>("probe", (probe, _, _) =>
        {
            probe.Increment();
            return Task.FromResult(0);
        });
        builder.Services.Configure<TicketFlowWorkerOptions>(options =>
        {
            options.Jitter = 0;
            options.Custom["probe"] = new TicketFlowLoopOptions { Interval = TimeSpan.FromMilliseconds(20) };
        });
        using var host = builder.Build();
        await host.StartAsync();
        await WaitUntilAsync(() => counter.Count >= 3);
        var health = host.Services.GetRequiredService<TicketFlowWorkerHealth>();
        Assert.AreEqual(TicketFlowLoopStatus.Running, health.Find("probe")!.Status);
        await host.StopAsync().WaitAsync(Timeout);
        Assert.AreEqual(TicketFlowLoopStatus.Stopped, health.Find("probe")!.Status);
        var after = counter.Count;
        await Task.Delay(100);
        Assert.AreEqual(after, counter.Count, "No runs after shutdown.");
    }

    [TestMethod]
    public async Task FailuresBackOffExponentiallyAndDriveHealth()
    {
        var time = new FakeTimeProvider(TestClock.DefaultStart);
        var counter = new Counter();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(counter);
        builder.Services.AddTicketFlow().AddTicketFlowWorkerLoop<Counter>("flaky", (probe, _, _) =>
        {
            probe.Increment();
            throw new InvalidOperationException("contains customer@example.com");
        });
        builder.Services.Configure<TicketFlowWorkerOptions>(options =>
        {
            options.Jitter = 0;
            options.UnhealthyAfterFailures = 3;
            options.Custom["flaky"] = new TicketFlowLoopOptions { InitialBackoff = TimeSpan.FromSeconds(10), MaximumBackoff = TimeSpan.FromSeconds(25) };
        });
        builder.Services.AddHealthChecks().AddTicketFlowWorkers();
        using var host = builder.Build();
        await host.StartAsync();
        var health = host.Services.GetRequiredService<TicketFlowWorkerHealth>();
        var checks = host.Services.GetRequiredService<HealthCheckService>();

        await WaitUntilAsync(() => counter.Count == 1 && health.Find("flaky")!.ConsecutiveFailures == 1);
        Assert.AreEqual(HealthStatus.Degraded, (await checks.CheckHealthAsync()).Status);
        time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(100);
        Assert.AreEqual(1, counter.Count, "The first backoff is 10 seconds.");
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => counter.Count == 2);
        time.Advance(TimeSpan.FromSeconds(19));
        await Task.Delay(100);
        Assert.AreEqual(2, counter.Count, "The second backoff doubles to 20 seconds.");
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => counter.Count == 3 && health.Find("flaky")!.ConsecutiveFailures == 3);
        Assert.AreEqual(HealthStatus.Unhealthy, (await checks.CheckHealthAsync()).Status);
        time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => counter.Count == 4, "The third backoff is capped at 25 seconds.");
        await host.StopAsync();
    }

    [TestMethod]
    public async Task BuiltInLoopsWithoutTheirFeatureAreNotConfiguredAndDisabledLoopsDoNotRun()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTicketFlow().AddTicketFlowWorkers(options => options.LiveChatPruning.Enabled = false);
        using var host = builder.Build();
        await host.StartAsync();
        var health = host.Services.GetRequiredService<TicketFlowWorkerHealth>();
        await WaitUntilAsync(() => health.Loops.All(state => state.Status != TicketFlowLoopStatus.Pending));
        Assert.AreEqual(TicketFlowLoopStatus.NotConfigured, health.Find(TicketFlowLoops.Outbox)!.Status);
        Assert.AreEqual(TicketFlowLoopStatus.NotConfigured, health.Find(TicketFlowLoops.SurveyDispatch)!.Status);
        Assert.AreEqual(TicketFlowLoopStatus.Disabled, health.Find(TicketFlowLoops.LiveChatPruning)!.Status);
        Assert.AreEqual(15, health.Loops.Count);
        Assert.AreEqual(TicketFlowLoopStatus.NotConfigured, health.Find(TicketFlowLoops.EventDelivery)!.Status);
        Assert.AreEqual(TicketFlowLoopStatus.NotConfigured, health.Find(TicketFlowLoops.ReportingRetention)!.Status);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task OutboxLoopRegistersConsumersAndDeliversToEveryHandler()
    {
        var handler = new RecordingHandler();
        var scope = TestScopes.Create();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddTicketFlowWorkers(options =>
            {
                options.Jitter = 0;
                options.Outbox.Interval = TimeSpan.FromMilliseconds(20);
            });
        builder.Services.AddSingleton<IConversationOutboxHandler>(handler);
        using var host = builder.Build();
        await host.StartAsync();
        var health = host.Services.GetRequiredService<TicketFlowWorkerHealth>();
        await WaitUntilAsync(() => health.Find(TicketFlowLoops.Outbox)!.LastSuccessAt is not null);

        using (var serviceScope = host.Services.CreateScope())
        {
            var services = serviceScope.ServiceProvider;
            await GrantOwnerAsync(services, scope);
            Assert.IsInstanceOfType<StoreApplied<StoredInbox>>((await services.GetRequiredService<IInboxStore>().CreateAsync(scope,
                new Inbox(new InboxId("support"), "Support", new ChannelBinding(new ChannelKind("email"), "cfg")))).Value);
            var owner = new UserPrincipal(scope, new UserId("owner"));
            var contact = As<TicketValue<ContactView>>((await services.GetRequiredService<IContactService>().CreateAsync(owner, new ContactDraft(ContactProfile.Empty))).Value);
            Assert.IsInstanceOfType<ConversationApplied>((await services.GetRequiredService<IConversationService>().StartAsync(owner,
                new StartConversationRequest(new InboxId("support"), contact.Value.Id))).Value);
        }
        await WaitUntilAsync(() => !handler.Entries.IsEmpty);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task PerWorkspaceLoopsVisitEveryWorkspaceAndIsolateFailures()
    {
        var dispatcher = new RecordingDispatcher { FailingWorkspace = "ws-2" };
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTicketFlow()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddTicketFlowWorkers(options =>
            {
                options.Jitter = 0;
                options.SurveyDispatch.Interval = TimeSpan.FromMilliseconds(20);
                options.SurveyDispatch.InitialBackoff = TimeSpan.FromSeconds(30);
                options.UnhealthyAfterFailures = 1;
            });
        builder.Services.AddSingleton<ISurveyDispatcher>(dispatcher);
        builder.Services.AddHealthChecks().AddTicketFlowWorkers();
        using var host = builder.Build();
        var settings = host.Services.GetRequiredService<IWorkspaceSettingsStore>();
        foreach (var workspace in new[] { "ws-1", "ws-2", "ws-3" })
            await settings.CreateAsync(TestScopes.Create(workspaceId: workspace), new WorkspaceSettings(workspace, "en", "UTC"));
        await host.StartAsync();
        var health = host.Services.GetRequiredService<TicketFlowWorkerHealth>();
        await WaitUntilAsync(() => dispatcher.Visits.Count(visit => string.Equals(visit, "ws-3", StringComparison.Ordinal)) >= 5);
        CollectionAssert.IsSubsetOf(new[] { "ws-1", "ws-3" }, dispatcher.Visits.ToArray());
        var state = health.Find(TicketFlowLoops.SurveyDispatch)!;
        Assert.AreEqual(0, state.ConsecutiveFailures, "Item failures never fail (and back off) the whole loop.");
        await WaitUntilAsync(() => health.Find(TicketFlowLoops.SurveyDispatch)!.LastItemFailures > 0 || dispatcher.FailedAttempts > 0);
        Assert.IsLessThan(dispatcher.Visits.Count(visit => string.Equals(visit, "ws-3", StringComparison.Ordinal)), dispatcher.FailedAttempts, "The failing workspace is backed off on its own.");
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.AreNotEqual(HealthStatus.Unhealthy, report.Status, "Isolated workspace failures degrade, never fail, health.");
        await host.StopAsync();
    }

    [TestMethod]
    public async Task CappedRunsContinueWhereThePreviousRunStoppedAndWrap()
    {
        var dispatcher = new RecordingDispatcher();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTicketFlow()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddTicketFlowWorkers(options =>
            {
                options.Jitter = 0;
                options.MaximumWorkspacesPerRun = 2;
                options.SurveyDispatch.Interval = TimeSpan.FromMilliseconds(20);
            });
        builder.Services.AddSingleton<ISurveyDispatcher>(dispatcher);
        using var host = builder.Build();
        var settings = host.Services.GetRequiredService<IWorkspaceSettingsStore>();
        var workspaces = Enumerable.Range(1, 5).Select(index => $"ws-{index}").ToList();
        foreach (var workspace in workspaces)
            await settings.CreateAsync(TestScopes.Create(workspaceId: workspace), new WorkspaceSettings(workspace, "en", "UTC"));
        await host.StartAsync();
        await WaitUntilAsync(() => dispatcher.Visits.Count >= 12);
        await host.StopAsync();
        var visits = dispatcher.Visits.ToList();
        CollectionAssert.AreEquivalent(workspaces, visits.Take(5).ToList(), "Consecutive capped runs cover every workspace before revisiting any.");
        CollectionAssert.AreEquivalent(workspaces, visits.Skip(5).Take(5).ToList(), "The position wraps to the beginning.");
    }

    [TestMethod]
    public async Task LoopsWithoutACompletedRunForSeveralIntervalsDegradeHealth()
    {
        var time = new FakeTimeProvider(TestClock.DefaultStart);
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton(new Counter());
        builder.Services.AddTicketFlow().AddTicketFlowWorkerLoop<Counter>("stuck", async (_, _, cancellationToken) =>
        {
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        });
        builder.Services.Configure<TicketFlowWorkerOptions>(options => options.Custom["stuck"] = new TicketFlowLoopOptions { Interval = TimeSpan.FromSeconds(30) });
        builder.Services.AddHealthChecks().AddTicketFlowWorkers();
        using var host = builder.Build();
        await host.StartAsync();
        var checks = host.Services.GetRequiredService<HealthCheckService>();
        await WaitUntilAsync(() => host.Services.GetRequiredService<TicketFlowWorkerHealth>().Find("stuck")!.Status == TicketFlowLoopStatus.Running);
        Assert.AreEqual(HealthStatus.Healthy, (await checks.CheckHealthAsync()).Status);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.AreEqual(HealthStatus.Degraded, (await checks.CheckHealthAsync()).Status, "Five intervals without a completed run is stale.");
        await host.StopAsync();
    }

    [TestMethod]
    public async Task MismatchedSlaWorkerIdentitiesFailStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTicketFlow().AddSla().AddAutomation(options => options.SlaWorkerClientId = new ApiClientId("other-sla")).AddTicketFlowWorkers(options =>
        {
            foreach (var loop in new[] { options.SlaTimers, options.AutomationSchedules, options.AutomationContacts })
                loop.Enabled = false;
        });
        using var host = builder.Build();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("AutomationOptions.SlaWorkerClientId", error.Message, StringComparison.Ordinal);
        var ids = TicketFlowServiceIdentities.Collect(host.Services);
        CollectionAssert.IsSubsetOf(new[] { new ApiClientId("sla-worker"), new ApiClientId("automation-worker"), new ApiClientId("other-sla") }, ids.ToArray());
    }

    [TestMethod]
    public async Task ContactMergeCatchUpStoresADurableCheckpoint()
    {
        var scope = TestScopes.Create();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddTicketFlow()
            .AddAccessControl().AddInMemoryAccessStores()
            .AddWorkspaces().AddInMemoryWorkspaceStores()
            .AddContacts().AddInMemoryContactStores()
            .AddConversations().AddInMemoryConversationStores()
            .AddTicketFlowWorkers(options =>
            {
                options.Jitter = 0;
                options.ContactMerges.Interval = TimeSpan.FromMilliseconds(20);
            });
        using var host = builder.Build();
        using (var serviceScope = host.Services.CreateScope())
        {
            var services = serviceScope.ServiceProvider;
            await GrantOwnerAsync(services, scope);
            await services.GetRequiredService<IWorkspaceSettingsStore>().CreateAsync(scope, new WorkspaceSettings("Acme", "en", "UTC"));
            var owner = new UserPrincipal(scope, new UserId("owner"));
            var contacts = services.GetRequiredService<IContactService>();
            var first = As<TicketValue<ContactView>>((await contacts.CreateAsync(owner, new ContactDraft(ContactProfile.Empty))).Value).Value;
            var second = As<TicketValue<ContactView>>((await contacts.CreateAsync(owner, new ContactDraft(ContactProfile.Empty))).Value).Value;
            Assert.IsInstanceOfType<TicketValue<ContactMergeOutcome>>((await contacts.MergeAsync(owner,
                new ContactMergeRequest(first.Id, first.Revision, second.Id, second.Revision, justification: "duplicate"))).Value);
        }
        await host.StartAsync();
        var checkpoints = host.Services.GetRequiredService<IWorkerCheckpointStore>();
        await WaitUntilAsync(async () => await checkpoints.GetAsync(scope, "contact-merges") is > 0);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task InMemoryCheckpointStoreSatisfiesTheContract()
    {
        var store = new InMemoryWorkerCheckpointStore();
        await WorkerCheckpointStoreContract.VerifyAsync(store, store);
    }

    private static async Task GrantOwnerAsync(IServiceProvider services, TicketScope scope) =>
        Assert.IsInstanceOfType<StoreApplied<StoredAccessGrant>>((await services.GetRequiredService<IAccessGrantStore>().CreateAsync(new AccessGrant(new GrantId("owner"),
            AccessPartition.From(scope), new UserId("owner"), BuiltInRoles.Owner.Id, new WorkspaceGrantScope(scope.WorkspaceId)))).Value);

    private static T As<T>(object? value) => value is T typed ? typed : throw new AssertFailedException($"Expected {typeof(T).Name} but got {value?.GetType().Name ?? "null"}.");

    private static Task WaitUntilAsync(Func<bool> condition, string? message = null) =>
        WaitUntilAsync(() => Task.FromResult(condition()), message);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string? message = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail(message ?? "The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class Counter
    {
        private int count;

        public int Count => Volatile.Read(ref count);

        public int Increment() => Interlocked.Increment(ref count);
    }

    private sealed class RecordingHandler : IConversationOutboxHandler
    {
        public ConcurrentQueue<ConversationOutboxEntry> Entries { get; } = new();

        public string Consumer => "test-recorder";

        public Task HandleAsync(ConversationOutboxEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Enqueue(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDispatcher : ISurveyDispatcher
    {
        public string? FailingWorkspace { get; init; }

        public ConcurrentQueue<string> Visits { get; } = new();

        private int failed;

        public int FailedAttempts => Volatile.Read(ref failed);

        public Task<SurveyDispatchSummary> DispatchDueAsync(TicketScope scope, int limit = 100, CancellationToken cancellationToken = default)
        {
            if (string.Equals(scope.WorkspaceId.Value, FailingWorkspace, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref failed);
                throw new InvalidOperationException("boom");
            }
            Visits.Enqueue(scope.WorkspaceId.Value);
            return Task.FromResult(new SurveyDispatchSummary(0, 0, 0, 0));
        }
    }
}
