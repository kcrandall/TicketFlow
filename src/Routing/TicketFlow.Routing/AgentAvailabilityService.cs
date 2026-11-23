using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Routing;

/// <summary>
/// Default <see cref="IAgentAvailabilityService"/>. Authorization is workspace-level and precedes existence checks. Coming online or
/// changing capacity makes the workspace's queued conversations due; going offline is picked up by the maintenance worker through the
/// profile's pending reassignment flag.
/// </summary>
public sealed class AgentAvailabilityService : IAgentAvailabilityService
{
    private readonly IAccessEvaluator access;
    private readonly IAgentAvailabilityStore agents;
    private readonly IRoutingQueueStore queue;
    private readonly RoutingEventPublisher events;
    private readonly RoutingOptions options;
    private readonly TimeProvider time;

    public AgentAvailabilityService(IAccessEvaluator access, IAgentAvailabilityStore agents, IRoutingQueueStore queue, IEnumerable<IRoutingEventObserver> observers,
        IOptions<RoutingOptions> options, TimeProvider time)
    {
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.agents = agents ?? throw new ArgumentNullException(nameof(agents));
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        events = new RoutingEventPublisher(observers ?? throw new ArgumentNullException(nameof(observers)));
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.options.Validate();
        this.time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public async Task<TicketResult<StoredAgentProfile>> GetAsync(PrincipalRef actor, UserId agent, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        RequireAgent(agent);
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.RosterRead, RoutingResources.Agent(actor.Scope, agent), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return await agents.GetAsync(actor.Scope, agent, cancellationToken).ConfigureAwait(false) is { } profile
            ? new TicketValue<StoredAgentProfile>(profile)
            : TicketFailure.NotFound;
    }

    public async Task<TicketResult<IReadOnlyList<StoredAgentProfile>>> ListAsync(PrincipalRef actor, UserId? after = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (limit is < 1 or > IAgentAvailabilityStore.MaximumList)
            return TicketFailure.Invalid($"Limits are 1-{IAgentAvailabilityStore.MaximumList}.");
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.RosterRead, RoutingResources.Workspace(actor.Scope), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return new TicketValue<IReadOnlyList<StoredAgentProfile>>(await agents.ListAsync(actor.Scope, after, limit, cancellationToken).ConfigureAwait(false));
    }

    public async Task<TicketResult<StoredAgentProfile>> ConfigureAsync(PrincipalRef actor, AgentRoutingSettings settings, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        ArgumentNullException.ThrowIfNull(settings);
        if (expectedRevision is < 1)
            return TicketFailure.Invalid("Expected revisions start at 1.");
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.StatusManage, RoutingResources.Agent(actor.Scope, settings.Agent), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var result = RoutingGuards.FromWrite(await agents.SaveSettingsAsync(actor.Scope, settings, expectedRevision, cancellationToken).ConfigureAwait(false));
        if (result.Value is TicketValue<StoredAgentProfile> applied && applied.Value.Presence.EffectiveStatus(time.GetUtcNow()) == AgentStatus.Online)
            await queue.NudgeAsync(actor.Scope, null, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<TicketResult<StoredAgentProfile>> SetStatusAsync(PrincipalRef actor, UserId agent, AgentStatus status, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        RequireAgent(agent);
        if (!Enum.IsDefined(status))
            return TicketFailure.Invalid("Unknown status.");
        var self = actor is UserPrincipal user && user.UserId == agent;
        var permission = self ? RoutingPermissions.StatusUpdate : RoutingPermissions.StatusManage;
        if (!await RoutingGuards.PermittedAsync(access, actor, permission, RoutingResources.Agent(actor.Scope, agent), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        var result = RoutingGuards.FromWrite(await agents.SetStatusAsync(actor.Scope, agent, status, options.AgentPresenceTtl, cancellationToken).ConfigureAwait(false));
        if (result.Value is TicketValue<StoredAgentProfile> applied)
        {
            if (status == AgentStatus.Online)
                await queue.NudgeAsync(actor.Scope, null, cancellationToken).ConfigureAwait(false);
            await events.PublishAsync(new AgentStatusChanged(actor.Scope, agent, applied.Value.Presence.Status, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    public async Task<TicketResult<StoredAgentProfile>> HeartbeatAsync(PrincipalRef actor, CancellationToken cancellationToken = default)
    {
        RoutingGuards.RequireActor(actor);
        if (actor is not UserPrincipal user)
            return TicketFailure.Rejected("Only user principals send presence heartbeats.");
        if (!await RoutingGuards.PermittedAsync(access, actor, RoutingPermissions.StatusUpdate, RoutingResources.Agent(actor.Scope, user.UserId), cancellationToken).ConfigureAwait(false))
            return TicketFailure.Forbidden;
        return RoutingGuards.FromWrite(await agents.HeartbeatAsync(actor.Scope, user.UserId, options.AgentPresenceTtl, cancellationToken).ConfigureAwait(false));
    }

    private static void RequireAgent(UserId agent)
    {
        if (agent == default)
            throw new ArgumentException("An agent identifier is required.", nameof(agent));
    }
}
