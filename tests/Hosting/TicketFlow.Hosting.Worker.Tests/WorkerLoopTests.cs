using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TicketFlow.Access;
using TicketFlow.Contacts;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Hosting.Worker.StoreContracts;
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
        builder.Services.AddTicketFlow().AddTicketFlowWorkerLoop<Counter>("probe", (probe, _, _) => Task.FromResult(probe.Increment() >= 0 ? 0 : 0));
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
        var stopping = host.StopAsync();
        Assert.AreSame(stopping, await Task.WhenAny(stopping, Task.Delay(Timeout)), "Shutdown completes promptly.");
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
        Assert.AreEqual(10, health.Loops.Count);
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
            Assert.IsInstanceOfType<WorkspaceWriteApplied<StoredInbox>>((await services.GetRequiredService<IInboxStore>().CreateAsync(scope,
                new Inbox(new InboxId("support"), "Support", new ChannelBinding(new ChannelKind("email"), "cfg")))).Value);
            var owner = new UserPrincipal(scope, new UserId("owner"));
            var contact = As<ContactApplied<ContactView>>((await services.GetRequiredService<IContactService>().CreateAsync(owner, new ContactDraft(ContactProfile.Empty))).Value);
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
                options.SurveyDispatch.InitialBackoff = TimeSpan.FromMilliseconds(20);
            });
        builder.Services.AddSingleton<ISurveyDispatcher>(dispatcher);
        using var host = builder.Build();
        var settings = host.Services.GetRequiredService<IWorkspaceSettingsStore>();
        foreach (var workspace in new[] { "ws-1", "ws-2", "ws-3" })
            await settings.CreateAsync(TestScopes.Create(workspaceId: workspace), new WorkspaceSettings(workspace, "en", "UTC"));
        await host.StartAsync();
        var health = host.Services.GetRequiredService<TicketFlowWorkerHealth>();
        await WaitUntilAsync(() => dispatcher.Visits.Count(visit => visit == "ws-3") >= 2);
        CollectionAssert.IsSubsetOf(new[] { "ws-1", "ws-3" }, dispatcher.Visits.ToArray());
        Assert.IsGreaterThan(0, health.Find(TicketFlowLoops.SurveyDispatch)!.ConsecutiveFailures, "A failing workspace is reported.");
        await host.StopAsync();
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
            var first = As<ContactApplied<ContactView>>((await contacts.CreateAsync(owner, new ContactDraft(ContactProfile.Empty))).Value).Value;
            var second = As<ContactApplied<ContactView>>((await contacts.CreateAsync(owner, new ContactDraft(ContactProfile.Empty))).Value).Value;
            Assert.IsInstanceOfType<ContactApplied<ContactMergeOutcome>>((await contacts.MergeAsync(owner,
                new ContactMergeRequest(first.Id, first.Revision, second.Id, second.Revision, justification: "duplicate"))).Value);
        }
        await host.StartAsync();
        var checkpoints = host.Services.GetRequiredService<IWorkerCheckpointStore>();
        await WaitUntilAsync(() => checkpoints.GetAsync(scope, "contact-merges").GetAwaiter().GetResult() is > 0);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task InMemoryCheckpointStoreSatisfiesTheContract()
    {
        var store = new InMemoryWorkerCheckpointStore();
        await WorkerCheckpointStoreContract.VerifyAsync(store, store);
    }

    private static async Task GrantOwnerAsync(IServiceProvider services, TicketScope scope) =>
        Assert.IsInstanceOfType<AccessWriteApplied<StoredAccessGrant>>((await services.GetRequiredService<IAccessGrantStore>().CreateAsync(new AccessGrant(new GrantId("owner"),
            AccessPartition.From(scope), new UserId("owner"), BuiltInRoles.Owner.Id, new WorkspaceGrantScope(scope.WorkspaceId)))).Value);

    private static T As<T>(object? value) => value is T typed ? typed : throw new AssertFailedException($"Expected {typeof(T).Name} but got {value?.GetType().Name ?? "null"}.");

    private static async Task WaitUntilAsync(Func<bool> condition, string? message = null)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
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

        public Task<SurveyDispatchSummary> DispatchDueAsync(TicketScope scope, int limit = 100, CancellationToken cancellationToken = default)
        {
            if (scope.WorkspaceId.Value == FailingWorkspace)
                throw new InvalidOperationException("boom");
            Visits.Enqueue(scope.WorkspaceId.Value);
            return Task.FromResult(new SurveyDispatchSummary(0, 0, 0, 0));
        }
    }
}
