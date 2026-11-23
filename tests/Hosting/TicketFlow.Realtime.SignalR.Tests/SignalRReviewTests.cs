using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using TicketFlow.Access;
using TicketFlow.Channels.LiveChat;
using TicketFlow.Core;
using TicketFlow.Realtime;
using static TicketFlow.Realtime.SignalR.Tests.SignalRHost;

namespace TicketFlow.Realtime.SignalR.Tests;

[TestClass]
public sealed class SignalRReviewTests
{
    [TestMethod]
    public async Task TypingIsThrottledPerConnectionAndConversation()
    {
        await using var host = await CreateAsync();
        var visitor = host.Hub(VisitorPath);
        await visitor.StartAsync();
        Ok(await visitor.InvokeAsync<JsonElement>("Connect", WidgetKey, null));
        var conversation = Ok(await visitor.InvokeAsync<JsonElement>("SendMessage", "c1", "hi", null)).GetProperty("conversation").GetString()!;
        var agent = host.Hub(AgentPath, "agent");
        var agentEvents = Events(agent);
        await agent.StartAsync();
        Ok(await agent.InvokeAsync<JsonElement>("Subscribe", "conversation", conversation));

        for (var index = 0; index < 5; index++)
            Ok(await visitor.InvokeAsync<JsonElement>("Typing", conversation, true));
        Ok(await visitor.InvokeAsync<JsonElement>("Typing", conversation, false));
        Ok(await visitor.InvokeAsync<JsonElement>("Typing", conversation, false));
        await host.Service<RealtimeSubscriptionRegistry>().IdleAsync();
        await Task.Delay(200);
        var typing = new List<bool>();
        while (agentEvents.Reader.TryRead(out var payload))
            if (payload.GetProperty("type").GetString() == "typing")
                typing.Add(payload.GetProperty("data").GetProperty("typing").GetBoolean());
        CollectionAssert.AreEqual(new[] { true, false }, typing, "Repeats within the interval are dropped; one stop after a start passes.");
    }

    [TestMethod]
    public async Task AgentHubRejectsForeignOriginsWhenConfigured()
    {
        await using var host = await CreateAsync(options => options.AgentAllowedOrigins.Add("https://console.example.com"));
        var allowed = host.Hub(AgentPath, "agent", origin: "https://console.example.com");
        await allowed.StartAsync();
        Ok(await allowed.InvokeAsync<JsonElement>("Subscribe", "agent", null));

        var foreign = host.Hub(AgentPath, "agent", origin: "https://evil.example");
        await AssertClosedAsync(foreign);
        Assert.IsTrue(AgentRealtimeHub.OriginAllowed(null, new TicketFlowSignalROptions()), "Non-browser clients without Origin are accepted by default.");
        Assert.IsFalse(AgentRealtimeHub.OriginAllowed(null, new TicketFlowSignalROptions { RequireAgentOrigin = true }));
    }

    [TestMethod]
    public async Task EndedAndRevokedSessionsLoseTheirConnectionsAndCalls()
    {
        await using var host = await CreateAsync();
        var tabA = host.Hub(VisitorPath);
        await tabA.StartAsync();
        var token = Ok(await tabA.InvokeAsync<JsonElement>("Connect", WidgetKey, null)).GetProperty("token").GetString()!;
        Ok(await tabA.InvokeAsync<JsonElement>("SendMessage", "c1", "hi", null));
        var tabB = host.Hub(VisitorPath);
        var tabBEvents = Events(tabB);
        var tabBClosed = new TaskCompletionSource();
        tabB.Closed += _ =>
        {
            tabBClosed.TrySetResult();
            return Task.CompletedTask;
        };
        await tabB.StartAsync();
        Ok(await tabB.InvokeAsync<JsonElement>("Connect", WidgetKey, token));

        Ok(await tabA.InvokeAsync<JsonElement>("EndSession"));
        Assert.AreEqual("unauthenticated", Error(await tabA.InvokeAsync<JsonElement>("SendMessage", "c2", "after end", null)));
        Assert.AreEqual("revoked", (await NextAsync(tabBEvents, "session.ended")).GetProperty("data").GetProperty("reason").GetString());
        await tabBClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var again = host.Hub(VisitorPath);
        await again.StartAsync();
        Assert.AreEqual("unauthenticated", Error(await again.InvokeAsync<JsonElement>("Connect", WidgetKey, token)));

        // Revocation by an administrator (store write, no event) is caught by the per-call liveness check.
        var tabC = host.Hub(VisitorPath);
        await tabC.StartAsync();
        Ok(await tabC.InvokeAsync<JsonElement>("Connect", WidgetKey, null));
        var sessions = host.Service<ILiveChatSessionStore>();
        var topic = (VisitorSessionTopic)host.Service<IRealtimeSubscriptions>().Topics(tabC.ConnectionId!).Single();
        var session = (await sessions.GetAsync(AccessPartition.From(host.Scope), topic.Session))!;
        Assert.IsInstanceOfType<StoreApplied<StoredVisitorSession>>((await sessions.UpdateAsync(host.Scope, session.Session.With(revoked: true), session.Revision)).Value);
        Assert.AreEqual("unauthenticated", Error(await tabC.InvokeAsync<JsonElement>("SendMessage", "c1", "after revoke", null)));
    }

    private static async Task AssertClosedAsync(HubConnection connection)
    {
        var closed = new TaskCompletionSource();
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await connection.StartAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error) when (error is not AssertFailedException)
        {
            // The server aborted the connection while it was starting.
        }
        Assert.AreEqual(HubConnectionState.Disconnected, connection.State);
    }

    private static JsonElement Ok(JsonElement result)
    {
        Assert.IsTrue(result.GetProperty("ok").GetBoolean(), result.ToString());
        return result.GetProperty("data");
    }

    private static string? Error(JsonElement result)
    {
        Assert.IsFalse(result.GetProperty("ok").GetBoolean(), result.ToString());
        return result.GetProperty("error").GetString();
    }
}
