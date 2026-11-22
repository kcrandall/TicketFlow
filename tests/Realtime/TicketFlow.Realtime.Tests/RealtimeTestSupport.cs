using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Channels;
using TicketFlow.Channels.Testing;
using TicketFlow.Conversations;
using TicketFlow.Core;
using TicketFlow.Realtime.Testing;
using TicketFlow.Testing;

namespace TicketFlow.Realtime.Tests;

internal static class RealtimeTestSupport
{
    public static Task<ChannelHost> HostAsync(Action<IServiceCollection>? configure = null, Action<RealtimeOptions>? options = null) =>
        ChannelHost.CreateAsync(configure: services =>
        {
            new TicketFlowBuilder(services).AddRealtime(options);
            configure?.Invoke(services);
        });

    public static IRealtimeSubscriptions Registry(this ChannelHost host) => host.Services.GetRequiredService<IRealtimeSubscriptions>();

    public static IRealtimePublisher Publisher(this ChannelHost host) => host.Services.GetRequiredService<IRealtimePublisher>();

    public static RecordingConnection Connect(this ChannelHost host, string id, PrincipalRef principal) => host.Connect(id, new AgentSubscriber(principal));

    public static RecordingConnection Connect(this ChannelHost host, string id, RealtimeSubscriber subscriber)
    {
        var connection = new RecordingConnection(id, subscriber, () => host.Services.GetRequiredService<RealtimeSubscriptionRegistry>().IdleAsync());
        Assert.IsInstanceOfType<RealtimeConnected>(host.Registry().Connect(connection).Value);
        return connection;
    }

    public static async Task<RealtimeRefusal?> SubscribeAsync(this ChannelHost host, RecordingConnection connection, RealtimeTopic topic) =>
        await host.Registry().SubscribeAsync(connection.Id, topic) switch
        {
            RealtimeSubscribed => null,
            RealtimeSubscriptionRefused refused => refused.Reason,
        };

    public static async Task<ConversationId> StartAsync(this ChannelHost host, string messageId = "<m1@x>", string from = "ada@example.com")
    {
        var result = await host.Pipeline.ReceiveAsync(host.Worker, host.Email(messageId, from));
        return ChannelHost.Is<InboundRecorded>(result.Value).Conversation;
    }

    public static async Task<StoredAccessRole> RoleAsync(this ChannelHost host, string id, params Permission[] permissions)
    {
        var written = await host.Services.GetRequiredService<IAccessRoleStore>().CreateAsync(host.Scope, new AccessRole(new RoleId(id), id, permissions));
        return ChannelHost.Is<AccessWriteApplied<StoredAccessRole>>(written.Value).Value;
    }

    public static UserPrincipal User(this ChannelHost host, string id, params string[] teams) => TestPrincipals.User(host.Scope, id, teams);

    public static ConversationApplied Applied(ConversationCommandResult result) => ChannelHost.Is<ConversationApplied>(result.Value);
}
