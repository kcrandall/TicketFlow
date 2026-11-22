using TicketFlow.Workspaces;

namespace TicketFlow.Channels;

/// <summary>Default <see cref="IChannelRegistry"/> over the registered <see cref="ITicketChannel"/>s; kinds must be unique.</summary>
public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly Dictionary<ChannelKind, ITicketChannel> channels;

    public ChannelRegistry(IEnumerable<ITicketChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        this.channels = [];
        foreach (var channel in channels)
        {
            ArgumentNullException.ThrowIfNull(channel, nameof(channels));
            if (!channel.Kind.IsInitialized || channel.Capabilities is null || channel.RecipientKinds is null)
                throw new ArgumentException("Channels must declare a kind, capabilities and recipient kinds.", nameof(channels));
            if (!this.channels.TryAdd(channel.Kind, channel))
                throw new ArgumentException($"More than one channel serves kind '{channel.Kind}'.", nameof(channels));
        }
    }

    public IReadOnlyCollection<ITicketChannel> Channels => channels.Values;

    public ITicketChannel? Find(ChannelKind kind) => kind.IsInitialized && channels.TryGetValue(kind, out var channel) ? channel : null;
}
