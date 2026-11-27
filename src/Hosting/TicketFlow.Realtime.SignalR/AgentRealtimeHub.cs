using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Realtime.SignalR;

/// <summary>
/// The agent hub. The host authenticates the connection (cookies, bearer tokens); <see cref="IRealtimePrincipalResolver"/> maps the
/// user to a <see cref="PrincipalRef"/> once per connection (connections without a principal are aborted). Subscriptions and actions
/// are authorized by the realtime registry and agent actions; every delivery is re-authorized per event.
/// </summary>
public sealed class AgentRealtimeHub(IRealtimeSubscriptions subscriptions, IRealtimePrincipalResolver principals, IRealtimeAgentActions actions, IHubContext<AgentRealtimeHub> hub,
    Microsoft.Extensions.Options.IOptions<TicketFlowSignalROptions> options, RealtimeTypingThrottle throttle, IServiceAccountStore? serviceAccounts = null) : Hub
{
    private const string PrincipalKey = "tf.principal";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (!OriginAllowed(http?.Request.Headers.Origin.ToString(), options.Value))
        {
            Context.Abort();
            return;
        }
        var user = Context.User ?? http?.User;
        var principal = await principals.ResolveAsync(user ?? new System.Security.Claims.ClaimsPrincipal(), http, Context.ConnectionAborted).ConfigureAwait(false);
        if (principal is not { } resolved)
        {
            Context.Abort();
            return;
        }
        if (!AccessPrincipalGuard.VerifyScopeClaims(user, resolved.Scope))
        {
            Context.Abort();
            return;
        }
        var accounts = serviceAccounts ?? http?.RequestServices.GetService<IServiceAccountStore>();
        if (!await AccessPrincipalGuard.IsServiceAccountActiveForScopeAsync(resolved, resolved.Scope, accounts, Context.ConnectionAborted).ConfigureAwait(false))
        {
            Context.Abort();
            return;
        }
        if (subscriptions.Connect(new SignalRRealtimeConnection(Context.ConnectionId, new AgentSubscriber(resolved), hub.Clients.Client(Context.ConnectionId), Context.Abort)).Value is not RealtimeConnected)
        {
            Context.Abort();
            return;
        }
        Context.Items[PrincipalKey] = resolved;
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        subscriptions.Disconnect(Context.ConnectionId);
        throttle.Forget(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Subscribes to <c>conversation</c> (id), <c>queue</c> (inbox id) or <c>agent</c> (own topic; id ignored).</summary>
    public async Task<HubResult> Subscribe(string kind, string? id)
    {
        if (Principal() is not { } principal)
            return HubResult.Failure("unauthenticated");
        if (Topic(principal, kind, id) is not { } topic)
            return HubResult.Failure("invalid");
        return await subscriptions.SubscribeAsync(Context.ConnectionId, topic, Context.ConnectionAborted).ConfigureAwait(false) switch
        {
            RealtimeSubscribed => HubResult.Success(),
            RealtimeSubscriptionRefused refused => HubResult.Failure(RealtimeWireFormat.Token(refused.Reason)),
        };
    }

    public HubResult Unsubscribe(string kind, string? id)
    {
        if (Principal() is not { } principal)
            return HubResult.Failure("unauthenticated");
        return Topic(principal, kind, id) is { } topic && subscriptions.Unsubscribe(Context.ConnectionId, topic) ? HubResult.Success() : HubResult.Failure("not-subscribed");
    }

    public async Task<HubResult> Typing(string conversation, bool typing)
    {
        if (Principal() is not { } principal)
            return HubResult.Failure("unauthenticated");
        if (Conversation(conversation) is not { } id)
            return HubResult.Failure("invalid");
        if (!throttle.Allow(Context.ConnectionId, id.Value, typing))
            return HubResult.Success();
        return Map(await actions.SetTypingAsync(principal, id, typing, Context.ConnectionAborted).ConfigureAwait(false));
    }

    public async Task<HubResult> MarkRead(string conversation, long upToSequence)
    {
        if (Principal() is not { } principal)
            return HubResult.Failure("unauthenticated");
        if (Conversation(conversation) is not { } id)
            return HubResult.Failure("invalid");
        return Map(await actions.MarkReadAsync(principal, id, upToSequence, Context.ConnectionAborted).ConfigureAwait(false));
    }

    private static readonly ConditionalWeakTable<TicketFlowSignalROptions, OriginCache> AllowedOriginCache = new();

    /// <summary>True when the agent hub accepts a connection with this Origin header under <paramref name="options"/>.</summary>
    public static bool OriginAllowed(string? origin, TicketFlowSignalROptions options)
    {
        if (string.IsNullOrEmpty(origin))
            return !options.RequireAgentOrigin;
        if (options.AgentAllowedOrigins.Count == 0)
            return true;
        var normalized = TicketFlow.Channels.LiveChat.LiveChatOrigin.Normalize(origin);
        if (normalized is null)
            return false;
        var cache = AllowedOriginCache.GetOrCreateValue(options);
        return cache.GetNormalized(options.AgentAllowedOrigins).Contains(normalized);
    }

    private sealed class OriginCache
    {
        private sealed record Snapshot(string[] Raw, FrozenSet<string> Normalized);
        private volatile Snapshot? cached;

        public FrozenSet<string> GetNormalized(IList<string> origins)
        {
            var current = cached;
            if (current is not null && Matches(current.Raw, origins))
                return current.Normalized;
            var snapshot = new string[origins.Count];
            var normalized = new HashSet<string>(origins.Count, StringComparer.Ordinal);
            for (var i = 0; i < origins.Count; i++)
            {
                snapshot[i] = origins[i];
                if (TicketFlow.Channels.LiveChat.LiveChatOrigin.Normalize(origins[i]) is { } value)
                    normalized.Add(value);
            }
            var frozen = normalized.ToFrozenSet(StringComparer.Ordinal);
            cached = new Snapshot(snapshot, frozen);
            return frozen;
        }

        private static bool Matches(string[] snapshot, IList<string> origins)
        {
            if (snapshot.Length != origins.Count)
                return false;
            for (var i = 0; i < snapshot.Length; i++)
                if (!string.Equals(snapshot[i], origins[i], StringComparison.Ordinal))
                    return false;
            return true;
        }
    }

    private PrincipalRef? Principal() => Context.Items.TryGetValue(PrincipalKey, out var value) && value is PrincipalRef principal ? principal : (PrincipalRef?)null;

    private static RealtimeTopic? Topic(PrincipalRef principal, string? kind, string? id)
    {
        try
        {
            return kind switch
            {
                "conversation" when id is not null => new ConversationTopic(principal.Scope, new ConversationId(id)),
                "queue" when id is not null => new InboxQueueTopic(principal.Scope, new InboxId(id)),
                "agent" when principal is UserPrincipal user => new AgentTopic(principal.Scope, user.UserId),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ConversationId? Conversation(string? value)
    {
        try
        {
            return value is null ? null : new ConversationId(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static HubResult Map(RealtimeActionResult result) => result switch
    {
        RealtimeActionApplied => HubResult.Success(),
        RealtimeSubscriptionRefused refused => HubResult.Failure(RealtimeWireFormat.Token(refused.Reason)),
    };
}
