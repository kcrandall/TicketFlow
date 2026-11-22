using TicketFlow.Contacts;
using TicketFlow.Core;

namespace TicketFlow.Channels;

public sealed class ChannelsOptions
{
    /// <summary>The outbox consumer name of <see cref="ChannelOutboundDispatcher"/>.</summary>
    public const string DispatchConsumer = "channels.dispatch";

    /// <summary>
    /// The grant-holding principal channel workers act as in a scope (dispatch delivery updates; adapters' inbound handling). It must
    /// be an API client (or user) principal holding <c>conversation:ingest</c> (and, for inbound, <c>conversation:create</c>,
    /// <c>contact:read</c>, <c>contact:create</c>); automation and system principals cannot hold grants.
    /// </summary>
    public Func<TicketScope, PrincipalRef>? WorkerPrincipal { get; set; }

    /// <summary>Send attempts per outgoing message before it fails (1-50, default 5). Must stay below the outbox dispatcher's maximum attempts.</summary>
    public int MaximumSendAttempts { get; set; } = 5;

    /// <summary>How claimed sender evidence matches existing contacts (default: a unique claimed holder matches).</summary>
    public ClaimedIdentityMatching ClaimedIdentityMatching { get; set; } = ClaimedIdentityMatching.WhenUnique;

    /// <summary>Optional default calling code (1-3 digits) for national phone numbers in sender evidence.</summary>
    public string? DefaultCallingCode { get; set; }

    /// <summary>References sent with a reply (root plus the newest; 1-32, default 20).</summary>
    public int MaximumReplyReferences { get; set; } = 20;

    internal void Validate()
    {
        if (MaximumSendAttempts is < 1 or > 50 || !Enum.IsDefined(ClaimedIdentityMatching) || MaximumReplyReferences is < 1 or > 32
            || DefaultCallingCode is { } code && (code.Length is < 1 or > 3 || !code.All(char.IsAsciiDigit)))
            throw new InvalidOperationException("Channel options are invalid.");
    }

    /// <summary>Resolves <see cref="WorkerPrincipal"/> for <paramref name="scope"/>; throws when it is missing or returns another scope.</summary>
    public PrincipalRef ResolveWorker(TicketScope scope)
    {
        var principal = WorkerPrincipal?.Invoke(scope)
            ?? throw new InvalidOperationException("ChannelsOptions.WorkerPrincipal must be configured with a grant-holding principal.");
        if (principal.Value is null || principal.Scope != scope)
            throw new InvalidOperationException("ChannelsOptions.WorkerPrincipal must return a principal of the requested scope.");
        return principal;
    }
}
