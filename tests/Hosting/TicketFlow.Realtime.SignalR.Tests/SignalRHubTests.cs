using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using TicketFlow.Conversations;
using static TicketFlow.Realtime.SignalR.Tests.SignalRHost;

namespace TicketFlow.Realtime.SignalR.Tests;

[TestClass]
public sealed class SignalRHubTests
{
    [TestMethod]
    public async Task VisitorMessageAgentReplyAndReconnectWithCursorOverSignalR()
    {
        await using var host = await CreateAsync();
        var visitor = host.Hub(VisitorPath);
        var visitorEvents = SignalRHost.Events(visitor);
        await visitor.StartAsync();
        var connected = Ok(await visitor.InvokeAsync<JsonElement>("Connect", WidgetKey, null));
        var token = connected.GetProperty("token").GetString()!;
        Assert.StartsWith("tfvs1.", token, StringComparison.Ordinal);
        Assert.IsTrue(connected.GetProperty("widget").GetProperty("online").GetBoolean());

        var sent = Ok(await visitor.InvokeAsync<JsonElement>("SendMessage", "c1", "Where is my order?", null));
        var conversation = sent.GetProperty("conversation").GetString()!;
        Assert.IsTrue(sent.GetProperty("started").GetBoolean());
        Assert.AreEqual("incoming", (await NextAsync(visitorEvents, "message")).GetProperty("data").GetProperty("direction").GetString());

        var agent = host.Hub(AgentPath, "agent");
        var agentEvents = SignalRHost.Events(agent);
        await agent.StartAsync();
        Ok(await agent.InvokeAsync<JsonElement>("Subscribe", "conversation", conversation));
        Ok(await agent.InvokeAsync<JsonElement>("Subscribe", "agent", null));
        Assert.AreEqual("invalid", Error(await agent.InvokeAsync<JsonElement>("Subscribe", "visitor", "anything")));

        Ok(await visitor.InvokeAsync<JsonElement>("Typing", conversation, true));
        var typing = await NextAsync(agentEvents, "typing");
        Assert.AreEqual("visitor", typing.GetProperty("data").GetProperty("actor").GetString());
        Ok(await agent.InvokeAsync<JsonElement>("Typing", conversation, true));
        var agentTyping = (await NextAsync(visitorEvents, "typing")).GetProperty("data");
        Assert.AreEqual("support", agentTyping.GetProperty("actor").GetString());
        Assert.IsFalse(agentTyping.TryGetProperty("agent", out _), "Visitors never learn agent identities.");

        Assert.IsInstanceOfType<ConversationApplied>((await host.ReplyAsync(conversation, "It ships today")).Value);
        await host.DispatchAsync();
        var reply = await NextAsync(visitorEvents, "message");
        Assert.AreEqual("visitor", reply.GetProperty("topic").GetString());
        Assert.AreEqual(1, reply.GetProperty("v").GetInt32());
        Assert.AreEqual("It ships today", reply.GetProperty("data").GetProperty("body").GetString());
        Assert.AreEqual("outgoing", reply.GetProperty("data").GetProperty("direction").GetString());
        var changed = await NextAsync(agentEvents, "conversation.changed");
        Assert.AreEqual($"conversation:{conversation}", changed.GetProperty("topic").GetString());
        Assert.IsFalse(changed.GetProperty("data").TryGetProperty("body", out _), "Agent notifications carry identifiers only.");

        // A second tab reconnects with the token and catches up from its cursor.
        var other = host.Hub(VisitorPath);
        await other.StartAsync();
        var resumed = Ok(await other.InvokeAsync<JsonElement>("Connect", WidgetKey, token));
        Assert.AreEqual(JsonValueKind.Null, resumed.GetProperty("token").ValueKind, "No rotation is due yet.");
        Assert.AreEqual(conversation, resumed.GetProperty("conversations")[0].GetProperty("conversation").GetString());
        var page = Ok(await other.InvokeAsync<JsonElement>("FetchMessages", conversation, 0L, 50));
        CollectionAssert.AreEqual(new[] { "Where is my order?", "It ships today" },
            page.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("body").GetString()).ToArray());
        var last = page.GetProperty("messages")[1].GetProperty("sequence").GetInt64();
        Assert.AreEqual(0, Ok(await other.InvokeAsync<JsonElement>("FetchMessages", conversation, last, 50)).GetProperty("messages").GetArrayLength());
        Assert.AreEqual(last, Ok(await other.InvokeAsync<JsonElement>("MarkRead", conversation, last)).GetInt64());
        Assert.AreEqual(last, (await NextAsync(agentEvents, "read")).GetProperty("data").GetProperty("upToSequence").GetInt64());
    }

    [TestMethod]
    public async Task HubsRefuseForeignOriginsBadTokensMissingPrincipalsAndUnauthorizedSubscriptions()
    {
        await using var host = await CreateAsync();
        var evil = host.Hub(VisitorPath, origin: "https://evil.example");
        await evil.StartAsync();
        Assert.AreEqual("origin-not-allowed", Error(await evil.InvokeAsync<JsonElement>("Connect", WidgetKey, null)));
        Assert.AreEqual("unauthenticated", Error(await evil.InvokeAsync<JsonElement>("SendMessage", "c1", "hi", null)), "Nothing works before Connect succeeds.");

        var visitor = host.Hub(VisitorPath);
        await visitor.StartAsync();
        Assert.AreEqual("unauthenticated", Error(await visitor.InvokeAsync<JsonElement>("Connect", WidgetKey, "tfvs1.vs_x.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")));
        Assert.AreEqual("not-found", Error(await visitor.InvokeAsync<JsonElement>("Connect", "wk_missing", null)));
        Ok(await visitor.InvokeAsync<JsonElement>("Connect", WidgetKey, null));
        var conversation = Ok(await visitor.InvokeAsync<JsonElement>("SendMessage", "c1", "hi", null)).GetProperty("conversation").GetString()!;

        var stranger = host.Hub(AgentPath, "stranger");
        await stranger.StartAsync();
        Assert.AreEqual("forbidden", Error(await stranger.InvokeAsync<JsonElement>("Subscribe", "conversation", conversation)));
        Assert.AreEqual("forbidden", Error(await stranger.InvokeAsync<JsonElement>("Typing", conversation, true)));
        var agent = host.Hub(AgentPath, "agent");
        await agent.StartAsync();
        Assert.AreEqual("not-found", Error(await agent.InvokeAsync<JsonElement>("Subscribe", "conversation", "missing")));

        var anonymous = host.Hub(AgentPath);
        var closed = new TaskCompletionSource();
        anonymous.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await anonymous.StartAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error) when (error is not AssertFailedException)
        {
            // The server aborts connections without a principal; the client may observe it while starting.
        }
        Assert.AreEqual(HubConnectionState.Disconnected, anonymous.State);
    }

    [TestMethod]
    public async Task AttachmentUploadAuthenticatesTheSessionToken()
    {
        await using var host = await CreateAsync();
        var visitor = host.Hub(VisitorPath);
        await visitor.StartAsync();
        var token = Ok(await visitor.InvokeAsync<JsonElement>("Connect", WidgetKey, null)).GetProperty("token").GetString()!;
        var client = host.Server.CreateClient();

        HttpRequestMessage Upload(string? bearer, string origin = Origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/ticketflow/livechat/attachments") { Content = new ByteArrayContent([1, 2, 3, 4]) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            request.Headers.Add("Origin", origin);
            request.Headers.Add(TicketFlowSignalRExtensions.FileNameHeader, Uri.EscapeDataString("my photo.png"));
            if (bearer is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            return request;
        }

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Upload(null))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.SendAsync(Upload(token[..^1] + (token[^1] == 'a' ? 'b' : 'a')))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.SendAsync(Upload(token, "https://evil.example"))).StatusCode);
        var response = await client.SendAsync(Upload(token));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var attachment = JsonElement.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("my photo.png", attachment.GetProperty("fileName").GetString());
        Assert.AreEqual(4, attachment.GetProperty("sizeBytes").GetInt64());

        var sent = Ok(await visitor.InvokeAsync<JsonElement>("SendMessage", "c1", "see photo", new[] { attachment.GetProperty("storageRef").GetString() }));
        var page = Ok(await visitor.InvokeAsync<JsonElement>("FetchMessages", sent.GetProperty("conversation").GetString(), 0L, 10));
        Assert.AreEqual("my photo.png", page.GetProperty("messages")[0].GetProperty("attachments")[0].GetProperty("fileName").GetString());
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
