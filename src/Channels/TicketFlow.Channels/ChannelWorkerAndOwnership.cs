using Microsoft.Extensions.Options;
using TicketFlow.Access;
using TicketFlow.Core;

namespace TicketFlow.Channels;

/// <summary>Default <see cref="IChannelWorkerPrincipalResolver"/> over <see cref="ChannelsOptions.WorkerPrincipal"/>.</summary>
public sealed class OptionsChannelWorkerPrincipalResolver(IOptions<ChannelsOptions> options) : IChannelWorkerPrincipalResolver
{
    private readonly ChannelsOptions options = (options ?? throw new ArgumentNullException(nameof(options))).Value;

    public PrincipalRef Resolve(TicketScope scope) => options.ResolveWorker(scope);
}

/// <summary>
/// Default <see cref="IChannelAddressOwnershipValidator"/>: an address is split on ':' (<c>{provider}:{account}[:{local}...]</c>) and
/// refused when any enclosing address (a proper ':'-prefix) or any nested address (one starting with this address plus ':') of the
/// same kind is bound by a different scope in the partition, so a workspace cannot shadow or enclose another scope's provider
/// account. It cannot prove that the scope owns the provider account at all: applications that let workspace administrators bind
/// addresses should replace it with a validator that checks provider-account ownership. The check runs before the store write and
/// is not atomic with it; exact duplicates are still refused atomically by the store.
/// </summary>
public sealed class CrossScopeAddressOwnershipValidator(IChannelEndpointStore endpoints) : IChannelAddressOwnershipValidator
{
    private readonly IChannelEndpointStore endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));

    public async Task<bool> MayBindAsync(PrincipalRef actor, TicketScope scope, ChannelEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Address is not { } address)
            return true;
        var partition = AccessPartition.From(scope);
        for (var index = address.IndexOf(':'); index > 0; index = address.IndexOf(':', index + 1))
            if (await endpoints.FindByAddressAsync(partition, endpoint.Kind, address[..index], cancellationToken).ConfigureAwait(false) is { } enclosing
                && enclosing.Scope != scope)
                return false;
        var nested = await endpoints.ListByAddressPrefixAsync(partition, endpoint.Kind, address + ":", IChannelEndpointStore.MaximumPrefixMatches, cancellationToken).ConfigureAwait(false);
        return nested.All(item => item.Scope == scope);
    }
}
